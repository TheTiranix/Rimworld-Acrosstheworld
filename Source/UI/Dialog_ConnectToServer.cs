using RimCoopMod.Networking;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    public class Dialog_ConnectToServer : Window
    {
        private string _ip;
        private string _port;
        private string _playerName;
        private string _statusMessage = "";

        public override Vector2 InitialSize => new Vector2(400f, 380f);

        public Dialog_ConnectToServer()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            // Recordar lo último tipeado para no escribirlo de nuevo cada vez.
            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            _ip = settings?.LastServerIp ?? "127.0.0.1";
            _port = settings?.LastServerPort ?? "34500";
            _playerName = string.IsNullOrEmpty(settings?.LastPlayerName) ? Loc.T("Common.Player") : settings.LastPlayerName;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label(Loc.T("Dialog_ConnectToServer.01"));
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.Label(Loc.T("Dialog_ConnectToServer.02"));
            _ip = listing.TextEntry(_ip);
            listing.Gap(6f);

            listing.Label(Loc.T("Dialog_ConnectToServer.03"));
            _port = listing.TextEntry(_port);
            listing.Gap(6f);

            listing.Label(Loc.T("Dialog_ConnectToServer.04"));
            _playerName = listing.TextEntry(_playerName);
            listing.Gap(16f);

            if (listing.ButtonText(Loc.T("Dialog_ConnectToServer.05")))
            {
                TryConnect();
            }

            listing.Gap(6f);
            if (listing.ButtonText(Loc.T("Dialog_ConnectToServer.06")))
            {
                Find.WindowStack.Add(new Dialog_ServerBrowser());
                Close();
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                listing.Gap(8f);
                GUI.color = Color.yellow;
                listing.Label(_statusMessage);
                GUI.color = Color.white;
            }

            listing.End();
            ModLanguage.DrawToggle(new Rect(inRect.x, inRect.yMax - 28f, 150f, 26f));
        }

        private void TryConnect()
        {
            if (!int.TryParse(_port, out int port))
            {
                _statusMessage = Loc.T("Dialog_ConnectToServer.07");
                return;
            }

            if (string.IsNullOrWhiteSpace(_ip))
            {
                _statusMessage = Loc.T("Dialog_ConnectToServer.08");
                return;
            }

            // Guardar ya, no solo si la conexión funciona: así no se pierde lo tipeado si el
            // servidor está apagado o la red falla justo esta vez.
            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            if (settings != null)
            {
                settings.LastServerIp = _ip;
                settings.LastServerPort = _port;
                settings.LastPlayerName = _playerName;
                global::RimCoopMod.RimCoopMod.Instance.WriteSettings();
            }

            _statusMessage = Loc.T("Dialog_ConnectToServer.09");

            CoopClient.Instance.Connect(_ip, port, _playerName);

            if (CoopClient.Instance.IsConnected)
            {
                Messages.Message(Loc.T("Dialog_ConnectToServer.10"), MessageTypeDefOf.PositiveEvent, false);
                GameComponents.CoopSessionManager.PendingWorldGeneration = true;
                Close();
            }
            else
            {
                _statusMessage = Loc.T("Dialog_ConnectToServer.11", CoopClient.Instance.LastError);
            }
        }
    }
}
