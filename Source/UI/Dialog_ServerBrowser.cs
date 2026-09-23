using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RimCoopMod.Networking;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>
    /// Lista de servidores guardados al estilo Half-Life/Counter-Strike: agregás IP+puerto una vez,
    /// les ponés un apodo, y la próxima vez ves de un vistazo si están vivos y cuánta gente hay
    /// conectada (sin tener que escribir la IP de memoria cada vez ni entrar a ciegas).
    /// </summary>
    public class Dialog_ServerBrowser : Window
    {
        private class ServerEntry
        {
            public string Nickname;
            public string Ip;
            public string Port;
            public string Key => $"{Ip}:{Port}";
        }

        private readonly List<ServerEntry> _entries = new List<ServerEntry>();
        private readonly ConcurrentDictionary<string, CoopClient.ServerPingResult> _pingResults = new ConcurrentDictionary<string, CoopClient.ServerPingResult>();
        private readonly HashSet<string> _pinging = new HashSet<string>();

        private string _playerName;
        private string _newNickname = "";
        private string _newIp = "";
        private string _newPort = "34500";
        private string _statusMessage = "";
        private Vector2 _scroll;

        public override Vector2 InitialSize => new Vector2(560f, 520f);

        public Dialog_ServerBrowser()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            _playerName = settings?.LastPlayerName ?? "Jugador";

            LoadEntriesFromSettings();
            PingAll();
        }

        private void LoadEntriesFromSettings()
        {
            _entries.Clear();
            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            if (settings?.SavedServers == null) return;

            foreach (var line in settings.SavedServers)
            {
                var parts = line.Split('|');
                if (parts.Length != 3) continue;
                _entries.Add(new ServerEntry { Nickname = parts[0], Ip = parts[1], Port = parts[2] });
            }
        }

        private void SaveEntriesToSettings()
        {
            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            if (settings == null) return;
            settings.SavedServers = _entries.Select(e => $"{e.Nickname}|{e.Ip}|{e.Port}").ToList();
            global::RimCoopMod.RimCoopMod.Instance.WriteSettings();
        }

        private void PingAll()
        {
            foreach (var e in _entries) PingOne(e);
        }

        private void PingOne(ServerEntry e)
        {
            if (!int.TryParse(e.Port, out int port)) return;
            string key = e.Key;
            lock (_pinging) { if (!_pinging.Add(key)) return; }

            CoopClient.PingServer(e.Ip, port, result =>
            {
                _pingResults[key] = result;
                lock (_pinging) { _pinging.Remove(key); }
            });
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label("RimCoop - Servidores guardados");
            Text.Font = GameFont.Small;
            listing.Gap(6f);

            listing.Label("Tu nombre de jugador:");
            _playerName = listing.TextEntry(_playerName);
            listing.Gap(8f);

            if (listing.ButtonText("Actualizar todos"))
            {
                PingAll();
            }
            listing.GapLine();

            var outRect = listing.GetRect(inRect.height - 330f);
            float rowHeight = 58f;
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, _entries.Count * rowHeight + 4f);
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);

            float y = 0f;
            ServerEntry toRemove = null;
            foreach (var e in _entries)
            {
                var rowRect = new Rect(0f, y, viewRect.width, rowHeight - 4f);
                Widgets.DrawBoxSolid(rowRect, new Color(1f, 1f, 1f, 0.03f));

                var labelRect = new Rect(rowRect.x + 6f, rowRect.y + 2f, 230f, rowRect.height - 4f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(labelRect, $"{e.Nickname}\n{e.Ip}:{e.Port}");
                Text.Anchor = TextAnchor.UpperLeft;

                var statusRect = new Rect(labelRect.xMax + 4f, rowRect.y, 150f, rowRect.height);
                DrawStatus(statusRect, e);

                float btnW = 70f;
                var connectRect = new Rect(rowRect.xMax - btnW * 3 - 8f, rowRect.y + 6f, btnW, rowRect.height - 12f);
                var pingRect = new Rect(rowRect.xMax - btnW * 2 - 4f, rowRect.y + 6f, btnW, rowRect.height - 12f);
                var removeRect = new Rect(rowRect.xMax - btnW, rowRect.y + 6f, btnW, rowRect.height - 12f);

                if (Widgets.ButtonText(connectRect, "Conectar")) TryConnect(e);
                if (Widgets.ButtonText(pingRect, "Actualizar")) PingOne(e);
                if (Widgets.ButtonText(removeRect, "Quitar")) toRemove = e;

                y += rowHeight;
            }

            Widgets.EndScrollView();

            if (toRemove != null)
            {
                _entries.Remove(toRemove);
                _pingResults.TryRemove(toRemove.Key, out _);
                SaveEntriesToSettings();
            }

            listing.GapLine();
            listing.Label("Agregar servidor:");
            var addRect = listing.GetRect(28f);
            float w = addRect.width;
            var nickRect = new Rect(addRect.x, addRect.y, w * 0.3f - 4f, addRect.height);
            var ipRect = new Rect(nickRect.xMax + 4f, addRect.y, w * 0.35f - 4f, addRect.height);
            var portRect = new Rect(ipRect.xMax + 4f, addRect.y, w * 0.15f - 4f, addRect.height);
            var addBtnRect = new Rect(portRect.xMax + 4f, addRect.y, w * 0.2f - 4f, addRect.height);

            _newNickname = Widgets.TextField(nickRect, _newNickname);
            _newIp = Widgets.TextField(ipRect, _newIp);
            _newPort = Widgets.TextField(portRect, _newPort);
            if (Widgets.ButtonText(addBtnRect, "Agregar")) TryAdd();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                listing.Gap(6f);
                GUI.color = Color.yellow;
                listing.Label(_statusMessage);
                GUI.color = Color.white;
            }

            listing.End();
        }

        private void DrawStatus(Rect rect, ServerEntry e)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            bool isPinging;
            lock (_pinging) { isPinging = _pinging.Contains(e.Key); }

            if (isPinging)
            {
                GUI.color = Color.gray;
                Widgets.Label(rect, "Consultando...");
            }
            else if (_pingResults.TryGetValue(e.Key, out var result))
            {
                if (result.Success)
                {
                    bool sameVersion = result.ProtocolVersion == ProtocolInfo.Version;
                    GUI.color = sameVersion ? Color.green : Color.yellow;
                    string text = $"{result.ConnectedPlayers} jugador(es) - {result.LatencyMs} ms";
                    if (!string.IsNullOrEmpty(result.WorldSeed)) text += $"\nseed: {result.WorldSeed}";
                    if (!sameVersion) text += "\nversión distinta";
                    Widgets.Label(rect, text);
                }
                else
                {
                    GUI.color = Color.red;
                    Widgets.Label(rect, "Sin respuesta");
                }
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.Label(rect, "");
            }

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void TryAdd()
        {
            if (string.IsNullOrWhiteSpace(_newIp))
            {
                _statusMessage = "Ingresá una IP válida.";
                return;
            }
            if (!int.TryParse(_newPort, out _))
            {
                _statusMessage = "El puerto tiene que ser un número.";
                return;
            }

            var entry = new ServerEntry
            {
                Nickname = string.IsNullOrWhiteSpace(_newNickname) ? _newIp : _newNickname.Replace("|", ""),
                Ip = _newIp.Replace("|", ""),
                Port = _newPort.Replace("|", "")
            };
            _entries.Add(entry);
            SaveEntriesToSettings();
            PingOne(entry);

            _newNickname = "";
            _newIp = "";
            _newPort = "34500";
            _statusMessage = "";
        }

        private void TryConnect(ServerEntry e)
        {
            if (!int.TryParse(e.Port, out int port))
            {
                _statusMessage = "Puerto inválido.";
                return;
            }
            if (string.IsNullOrWhiteSpace(_playerName))
            {
                _statusMessage = "Ingresá tu nombre de jugador.";
                return;
            }

            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            if (settings != null)
            {
                settings.LastServerIp = e.Ip;
                settings.LastServerPort = e.Port;
                settings.LastPlayerName = _playerName;
                global::RimCoopMod.RimCoopMod.Instance.WriteSettings();
            }

            CoopClient.Instance.Connect(e.Ip, port, _playerName);

            if (CoopClient.Instance.IsConnected)
            {
                Messages.Message("Conectado al servidor. Generando mundo...", MessageTypeDefOf.PositiveEvent, false);
                GameComponents.CoopSessionManager.PendingWorldGeneration = true;
                Close();
            }
            else
            {
                _statusMessage = "No se pudo conectar: " + CoopClient.Instance.LastError;
            }
        }
    }
}
