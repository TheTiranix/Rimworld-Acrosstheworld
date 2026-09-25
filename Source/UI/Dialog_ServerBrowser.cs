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
    /// Lista de servidores al estilo Half-Life/Counter-Strike, con dos pestañas:
    ///  - Guardados: agregás IP+puerto una vez con un apodo, podés marcar favoritos (van primero) y ves de un vistazo
    ///    si están vivos, cuánta gente hay, el ping, el nombre de la partida y la seed. Se actualizan solos cada tanto.
    ///  - LAN: busca solo los servidores de la red local (broadcast UDP, ver LanDiscovery), sin tipear la IP.
    /// </summary>
    public class Dialog_ServerBrowser : Window
    {
        private class ServerEntry
        {
            public string Nickname;
            public string Ip;
            public string Port;
            public bool Favorite;
            public string Key => $"{Ip}:{Port}";
        }

        private enum Tab { Saved, Lan }

        private const float AutoRefreshSeconds = 15f;
        private const float LanScanIntervalSeconds = 8f;

        private readonly List<ServerEntry> _entries = new List<ServerEntry>();
        private readonly ConcurrentDictionary<string, CoopClient.ServerPingResult> _pingResults = new ConcurrentDictionary<string, CoopClient.ServerPingResult>();
        private readonly HashSet<string> _pinging = new HashSet<string>();

        private Tab _tab = Tab.Saved;
        private float _lastAutoRefresh;
        private volatile List<LanServerInfo> _lanResults = new List<LanServerInfo>();
        private volatile bool _scanning;
        private float _lastLanScan = -999f;

        private string _playerName;
        private string _newNickname = "";
        private string _newIp = "";
        private string _newPort = "34500";
        private string _statusMessage = "";
        private Vector2 _scroll;

        public override Vector2 InitialSize => new Vector2(720f, 560f);

        public Dialog_ServerBrowser()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            _playerName = settings?.LastPlayerName ?? "Jugador";

            LoadEntriesFromSettings();
            PingAll();
            _lastAutoRefresh = Time.realtimeSinceStartup;
        }

        // ------------------------------------------------------------ persistencia

        private void LoadEntriesFromSettings()
        {
            _entries.Clear();
            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            if (settings?.SavedServers == null) return;

            // "Apodo|IP|Puerto" (las guardadas antes de los favoritos) o "Apodo|IP|Puerto|1".
            foreach (var line in settings.SavedServers)
            {
                var parts = line.Split('|');
                if (parts.Length < 3) continue;
                _entries.Add(new ServerEntry { Nickname = parts[0], Ip = parts[1], Port = parts[2], Favorite = parts.Length > 3 && parts[3] == "1" });
            }
        }

        private void SaveEntriesToSettings()
        {
            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            if (settings == null) return;
            settings.SavedServers = _entries.Select(e => $"{e.Nickname}|{e.Ip}|{e.Port}|{(e.Favorite ? 1 : 0)}").ToList();
            global::RimCoopMod.RimCoopMod.Instance.WriteSettings();
        }

        // ------------------------------------------------------------ consultas

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

        private void ScanLan()
        {
            if (_scanning) return;
            _scanning = true;
            _lastLanScan = Time.realtimeSinceStartup;
            LanDiscovery.Scan(found =>
            {
                _lanResults = found;
                _scanning = false;
            });
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            float now = Time.realtimeSinceStartup;

            if (now - _lastAutoRefresh > AutoRefreshSeconds)
            {
                _lastAutoRefresh = now;
                PingAll();
            }

            if (_tab == Tab.Lan && !_scanning && now - _lastLanScan > LanScanIntervalSeconds) ScanLan();
        }

        // ------------------------------------------------------------ UI

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 30f, 32f), "RimCoop - Servidores");
            Text.Font = GameFont.Small;

            float y = inRect.y + 38f;
            Widgets.Label(new Rect(inRect.x, y + 4f, 150f, 24f), "Tu nombre de jugador:");
            _playerName = Widgets.TextField(new Rect(inRect.x + 155f, y, 220f, 28f), _playerName);
            y += 36f;

            // Pestañas
            float tabW = 130f;
            if (DrawTabButton(new Rect(inRect.x, y, tabW, 30f), "Guardados", _tab == Tab.Saved)) _tab = Tab.Saved;
            if (DrawTabButton(new Rect(inRect.x + tabW + 6f, y, tabW, 30f), "LAN", _tab == Tab.Lan))
            {
                if (_tab != Tab.Lan) _lastLanScan = -999f; // al entrar a la pestaña se busca de inmediato
                _tab = Tab.Lan;
            }

            var refreshRect = new Rect(inRect.xMax - 150f, y, 150f, 30f);
            if (_tab == Tab.Saved)
            {
                if (Widgets.ButtonText(refreshRect, "Actualizar todos")) { PingAll(); _lastAutoRefresh = Time.realtimeSinceStartup; }
            }
            else
            {
                if (Widgets.ButtonText(refreshRect, _scanning ? "Buscando..." : "Buscar de nuevo", true, true, !_scanning)) ScanLan();
            }
            y += 38f;

            float footer = _tab == Tab.Saved ? 110f : 40f;
            var listRect = new Rect(inRect.x, y, inRect.width, inRect.height - (y - inRect.y) - footer);

            if (_tab == Tab.Saved) DrawSavedList(listRect);
            else DrawLanList(listRect);

            float fy = listRect.yMax + 6f;
            if (_tab == Tab.Saved) DrawAddForm(new Rect(inRect.x, fy, inRect.width, footer - 6f));

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = Color.yellow;
                Widgets.Label(new Rect(inRect.x, inRect.yMax - 24f, inRect.width, 24f), _statusMessage);
                GUI.color = Color.white;
            }
        }

        private static bool DrawTabButton(Rect rect, string label, bool active)
        {
            if (active) Widgets.DrawHighlightSelected(rect);
            return Widgets.ButtonText(rect, label, drawBackground: !active);
        }

        private void DrawSavedList(Rect outRect)
        {
            // Los favoritos van primero (el resto conserva el orden en que se agregaron).
            var ordered = _entries.OrderByDescending(e => e.Favorite).ToList();

            if (ordered.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Color.gray;
                Widgets.Label(outRect, "No hay servidores guardados todavía.\nAgregá uno abajo, o probá la pestaña LAN.");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            const float rowHeight = 62f;
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, ordered.Count * rowHeight + 4f);
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);

            float y = 0f;
            ServerEntry toRemove = null;
            bool changed = false;
            foreach (var e in ordered)
            {
                var row = new Rect(0f, y, viewRect.width, rowHeight - 4f);
                Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.03f));

                bool fav = e.Favorite;
                Widgets.CheckboxLabeled(new Rect(row.x + 4f, row.y + 4f, 62f, 24f), "Fav", ref fav);
                if (fav != e.Favorite) { e.Favorite = fav; changed = true; }

                var labelRect = new Rect(row.x + 72f, row.y + 2f, 200f, row.height - 4f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(labelRect, $"{e.Nickname}\n{e.Ip}:{e.Port}");
                Text.Anchor = TextAnchor.UpperLeft;

                DrawStatus(new Rect(labelRect.xMax + 4f, row.y, 240f, row.height), e.Key);

                const float btnW = 76f;
                if (Widgets.ButtonText(new Rect(row.xMax - btnW * 2 - 8f, row.y + 8f, btnW, row.height - 16f), "Conectar")) TryConnect(e.Ip, e.Port);
                if (Widgets.ButtonText(new Rect(row.xMax - btnW - 4f, row.y + 8f, btnW, row.height - 16f), "Quitar")) toRemove = e;

                y += rowHeight;
            }

            Widgets.EndScrollView();

            if (toRemove != null)
            {
                _entries.Remove(toRemove);
                _pingResults.TryRemove(toRemove.Key, out _);
                changed = true;
            }
            if (changed) SaveEntriesToSettings();
        }

        private void DrawLanList(Rect outRect)
        {
            var found = _lanResults;
            if (found.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Color.gray;
                Widgets.Label(outRect, _scanning
                    ? "Buscando servidores en la red local..."
                    : "No se encontró ningún servidor en la red local.\nTienen que estar en la misma red y con el servidor abierto\n(el firewall tiene que dejar pasar UDP " + LanDiscovery.DiscoveryPort + ").");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            const float rowHeight = 62f;
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, found.Count * rowHeight + 4f);
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);

            float y = 0f;
            foreach (var s in found)
            {
                var row = new Rect(0f, y, viewRect.width, rowHeight - 4f);
                Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.03f));

                var labelRect = new Rect(row.x + 8f, row.y + 2f, 250f, row.height - 4f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(labelRect, $"Partida: {s.SaveName}\n{s.Ip}:{s.Port}");

                bool sameVersion = s.ProtocolVersion == ProtocolInfo.Version;
                GUI.color = sameVersion ? Color.green : Color.yellow;
                Widgets.Label(new Rect(labelRect.xMax + 4f, row.y, 200f, row.height),
                    $"{s.Players} jugador(es)\nseed: {s.Seed}" + (sameVersion ? "" : "\nversión distinta"));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;

                bool alreadySaved = _entries.Any(e => e.Ip == s.Ip && e.Port == s.Port.ToString());
                const float btnW = 76f;
                if (Widgets.ButtonText(new Rect(row.xMax - btnW * 2 - 8f, row.y + 8f, btnW, row.height - 16f), "Conectar")) TryConnect(s.Ip, s.Port.ToString());
                if (Widgets.ButtonText(new Rect(row.xMax - btnW - 4f, row.y + 8f, btnW, row.height - 16f), alreadySaved ? "Guardado" : "Guardar", true, true, !alreadySaved))
                {
                    _entries.Add(new ServerEntry { Nickname = s.SaveName, Ip = s.Ip, Port = s.Port.ToString() });
                    SaveEntriesToSettings();
                    PingAll();
                }

                y += rowHeight;
            }

            Widgets.EndScrollView();
        }

        private void DrawAddForm(Rect rect)
        {
            Widgets.DrawLineHorizontal(rect.x, rect.y, rect.width);
            Widgets.Label(new Rect(rect.x, rect.y + 4f, rect.width, 24f), "Agregar servidor (apodo, IP y puerto):");

            var addRect = new Rect(rect.x, rect.y + 32f, rect.width, 28f);
            float w = addRect.width;
            var nickRect = new Rect(addRect.x, addRect.y, w * 0.28f - 4f, addRect.height);
            var ipRect = new Rect(nickRect.xMax + 4f, addRect.y, w * 0.34f - 4f, addRect.height);
            var portRect = new Rect(ipRect.xMax + 4f, addRect.y, w * 0.16f - 4f, addRect.height);
            var addBtnRect = new Rect(portRect.xMax + 4f, addRect.y, w * 0.22f - 4f, addRect.height);

            _newNickname = Widgets.TextField(nickRect, _newNickname);
            _newIp = Widgets.TextField(ipRect, _newIp);
            _newPort = Widgets.TextField(portRect, _newPort);
            if (Widgets.ButtonText(addBtnRect, "Agregar")) TryAdd();
        }

        private void DrawStatus(Rect rect, string key)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            bool isPinging;
            lock (_pinging) { isPinging = _pinging.Contains(key); }

            if (_pingResults.TryGetValue(key, out var result))
            {
                if (result.Success)
                {
                    bool sameVersion = result.ProtocolVersion == ProtocolInfo.Version;
                    GUI.color = sameVersion ? Color.green : Color.yellow;
                    string text = $"{result.ConnectedPlayers} jugador(es) - {result.LatencyMs} ms";
                    if (!string.IsNullOrEmpty(result.SaveName)) text += $"\nPartida: {result.SaveName}";
                    if (!string.IsNullOrEmpty(result.WorldSeed)) text += $" (seed {result.WorldSeed})";
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
                Widgets.Label(rect, isPinging ? "Consultando..." : "");
            }

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // ------------------------------------------------------------ acciones

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
                Nickname = string.IsNullOrWhiteSpace(_newNickname) ? _newIp.Trim() : _newNickname.Replace("|", "").Trim(),
                Ip = _newIp.Replace("|", "").Trim(),
                Port = _newPort.Replace("|", "").Trim()
            };
            _entries.Add(entry);
            SaveEntriesToSettings();
            PingOne(entry);

            _newNickname = "";
            _newIp = "";
            _newPort = "34500";
            _statusMessage = "";
        }

        private void TryConnect(string ip, string portText)
        {
            if (!int.TryParse(portText, out int port))
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
                settings.LastServerIp = ip;
                settings.LastServerPort = portText;
                settings.LastPlayerName = _playerName;
                global::RimCoopMod.RimCoopMod.Instance.WriteSettings();
            }

            CoopClient.Instance.Connect(ip, port, _playerName);

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
