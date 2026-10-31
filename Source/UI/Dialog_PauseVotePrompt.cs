using System.Linq;
using RimCoopMod.Networking;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>
    /// Alguien propuso pausar o despausar el juego compartido. No se puede ignorar en silencio:
    /// se responde sí o no, y esa respuesta viaja de vuelta al que propuso para contar los votos.
    /// </summary>
    public class Dialog_PauseVotePrompt : Window
    {
        private readonly PauseVoteRequestPayload _request;
        private bool _answered;

        public override Vector2 InitialSize => new Vector2(400f, 160f);

        public Dialog_PauseVotePrompt(PauseVoteRequestPayload request)
        {
            _request = request;
            doCloseX = false;
            forcePause = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            string action = Loc.T(_request.ProposePause ? "Dialog_PauseVotePrompt.02" : "Dialog_PauseVotePrompt.03");
            listing.Label(Loc.T("Dialog_PauseVotePrompt.01", _request.FromPlayerName, action));
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            var buttonRect = listing.GetRect(35f);
            var yesRect = buttonRect.LeftHalf().ContractedBy(4f);
            var noRect = buttonRect.RightHalf().ContractedBy(4f);

            if (Widgets.ButtonText(yesRect, Loc.T("Common.Yes"))) Answer(true);
            if (Widgets.ButtonText(noRect, Loc.T("Common.No"))) Answer(false);

            listing.End();
        }

        public static void CloseFrom(int playerId)
        {
            foreach (var w in Find.WindowStack.Windows.OfType<Dialog_PauseVotePrompt>().Where(w => w._request.FromPlayerId == playerId).ToList()) w.Close(false);
        }

        public static void CloseAll()
        {
            foreach (var w in Find.WindowStack.Windows.OfType<Dialog_PauseVotePrompt>().ToList()) w.Close(false);
        }

        private void Answer(bool accept)
        {
            if (_answered) return;
            _answered = true;

            CoopClient.Instance.SendPauseVoteResponse(_request.FromPlayerId, accept);
            Close();
        }
    }
}
