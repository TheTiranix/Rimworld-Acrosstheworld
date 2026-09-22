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

        public override Vector2 InitialSize => new Vector2(400f, 340f);

        public Dialog_ConnectToServer()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            // Recordar lo último tipeado para no escribirlo de nuevo cada vez.
            var settings = global::RimCoopMod.RimCoopMod.Instance?.Settings;
            _ip = settings?.LastServerIp ?? "127.0.0.1";
            _port = settings?.LastServerPort ?? "34500";
            _playerName = settings?.LastPlayerName ?? "Jugador";
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label("RimCoop - Conectar a servidor");
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.Label("IP del servidor:");
            _ip = listing.TextEntry(_ip);
            listing.Gap(6f);

            listing.Label("Puerto:");
            _port = listing.TextEntry(_port);
            listing.Gap(6f);

            listing.Label("Tu nombre de jugador:");
            _playerName = listing.TextEntry(_playerName);
            listing.Gap(16f);

            if (listing.ButtonText("Conectar"))
            {
                TryConnect();
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                listing.Gap(8f);
                GUI.color = Color.yellow;
                listing.Label(_statusMessage);
                GUI.color = Color.white;
            }

            listing.End();
        }

        private void TryConnect()
        {
            if (!int.TryParse(_port, out int port))
            {
                _statusMessage = "El puerto tiene que ser un número.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_ip))
            {
                _statusMessage = "Ingresá una IP válida.";
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

            _statusMessage = "Conectando...";

            CoopClient.Instance.Connect(_ip, port, _playerName);

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
