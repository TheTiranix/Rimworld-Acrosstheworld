using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    public class Dialog_HostServer : Window
    {
        private string _port = "34500";
        private string _seed = GenText.RandomSeedString();
        private string _playerName = "Host";
        private string _coverage = "0.3";
        private string _statusMessage = "";

        public override Vector2 InitialSize => new Vector2(420f, 340f);

        public Dialog_HostServer()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label("RimCoop - Hostear partida");
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.Label("Puerto (los demás se conectan a este puerto):");
            _port = listing.TextEntry(_port);
            listing.Gap(6f);

            listing.Label("Seed del mundo:");
            using (new GUILayout.HorizontalScope())
            {
                // Listing_Standard no soporta layouts horizontales nativos,
                // así que ponemos el botón "aleatoria" en su propia línea.
            }
            _seed = listing.TextEntry(_seed);
            if (listing.ButtonText("Generar seed aleatoria"))
            {
                _seed = GenText.RandomSeedString();
            }
            listing.Gap(6f);

            listing.Label("Cobertura del planeta (0.05 a 1.0):");
            _coverage = listing.TextEntry(_coverage);
            listing.Gap(6f);

            listing.Label("Tu nombre de jugador (host):");
            _playerName = listing.TextEntry(_playerName);
            listing.Gap(16f);

            if (listing.ButtonText("Hostear y jugar"))
            {
                TryHost();
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

        private void TryHost()
        {
            if (!int.TryParse(_port, out int port))
            {
                _statusMessage = "El puerto tiene que ser un número.";
                return;
            }

            if (!float.TryParse(_coverage, out float coverage) || coverage <= 0f || coverage > 1f)
            {
                _statusMessage = "La cobertura tiene que ser un número entre 0.05 y 1.0.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_seed))
            {
                _statusMessage = "Ingresá una seed (o generá una aleatoria).";
                return;
            }

            try
            {
                // 1. Levantar el servidor
                CoopServer.Instance = new CoopServer();
                CoopServer.Instance.Start(port, _seed, coverage, "Normal", "Normal", "Normal");

                // 2. Conectar al propio host como cliente local (loopback)
                CoopClient.Instance.Connect("127.0.0.1", port, _playerName);

                if (!CoopClient.Instance.IsConnected)
                {
                    _statusMessage = "El servidor arrancó pero el host no pudo conectarse: " + CoopClient.Instance.LastError;
                    return;
                }

                Messages.Message($"Servidor hosteado en el puerto {port}. Seed: {_seed}", MessageTypeDefOf.PositiveEvent, false);
                CoopSessionManager.PendingWorldGeneration = true;
                Close();
            }
            catch (System.Exception e)
            {
                _statusMessage = "Error al hostear: " + e.Message;
                Log.Error("[RimCoop] Error al hostear: " + e);
            }
        }
    }
}