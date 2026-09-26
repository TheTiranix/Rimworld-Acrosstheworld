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
            string action = _request.ProposePause ? "pausar" : "despausar";
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

        private void Answer(bool accept)
        {
            if (_answered) return;
            _answered = true;

            CoopClient.Instance.SendPauseVoteResponse(_request.FromPlayerId, accept);
            Close();
        }
    }
}
