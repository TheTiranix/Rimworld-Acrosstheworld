using RimCoopMod.GameComponents;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>Otro jugador te propuso ser aliados o hacer las paces: aceptar, rechazar o decidir más tarde (ver CoopSessionManager.Diplomacy).</summary>
    public class Dialog_DiplomacyProposal : Window
    {
        public readonly int FromPlayerId;
        public readonly string FromName;
        public readonly bool Peace;

        public override Vector2 InitialSize => new Vector2(500f, 260f);

        public Dialog_DiplomacyProposal(int fromPlayerId, string fromName, bool peace)
        {
            FromPlayerId = fromPlayerId;
            FromName = fromName;
            Peace = peace;
            doCloseX = false;
            closeOnClickedOutside = false;
            closeOnCancel = false; // Esc no la descarta en silencio: queda pendiente hasta que se responda
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), Loc.T(Peace ? "Diplomacy.ProposalPeaceTitle" : "Diplomacy.ProposalAllyTitle", FromName));
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 44f, inRect.width, inRect.height - 100f), Loc.T(Peace ? "Diplomacy.ProposePeaceDesc" : "Diplomacy.ProposeAllyDesc", FromName));

            float third = inRect.width / 3f;
            if (Widgets.ButtonText(new Rect(0f, inRect.height - 40f, third - 6f, 36f), Loc.T("Diplomacy.Accept")))
            {
                CoopSessionManager.RequestDiplomacy(FromPlayerId, Peace ? "peace" : "ally");
                Close();
            }
            if (Widgets.ButtonText(new Rect(third + 3f, inRect.height - 40f, third - 6f, 36f), Loc.T("Diplomacy.Later")))
            {
                CoopSessionManager.SnoozeDiplomacy(FromName, Peace);
                Close();
            }
            if (Widgets.ButtonText(new Rect(third * 2f + 6f, inRect.height - 40f, third - 6f, 36f), Loc.T("Diplomacy.Reject")))
            {
                CoopSessionManager.RequestDiplomacy(FromPlayerId, "decline");
                Close();
            }
        }
    }
}
