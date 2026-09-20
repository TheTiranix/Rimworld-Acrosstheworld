using System.Collections.Generic;
using System.Linq;
using RimCoopMod.Networking;
using RimCoopMod.World;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RimCoopMod.UI
{
    /// <summary>
    /// Elegís colonos de tu propia colonia para que se vayan a vivir/trabajar a la base de
    /// otro jugador. Se van de tu mapa (los perdés) y llegan como pawns reales al mapa del
    /// destino, con vos como su dueño real a los efectos de quién los puede ordenar ahí.
    /// </summary>
    public class Dialog_ChooseColonistsToSend : Window
    {
        private readonly CoopPlayerBase _target;
        private readonly List<Pawn> _candidates;
        private readonly HashSet<Pawn> _selected = new HashSet<Pawn>();
        private Vector2 _scroll;

        public override Vector2 InitialSize => new Vector2(400f, 500f);

        public Dialog_ChooseColonistsToSend(CoopPlayerBase target)
        {
            _target = target;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;

            var map = Find.AnyPlayerHomeMap;
            _candidates = map?.mapPawns?.FreeColonists?.ToList() ?? new List<Pawn>();
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label($"Colaborar con {_target.RemotePlayerName}");
            Text.Font = GameFont.Small;
            listing.Label("Estos colonos se van de tu colonia y pasan a vivir y trabajar en la base de destino.");
            listing.GapLine();

            var outRect = listing.GetRect(inRect.height - 150f);
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, _candidates.Count * 30f);
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);

            float y = 0f;
            foreach (var pawn in _candidates)
            {
                bool isSelected = _selected.Contains(pawn);
                var rowRect = new Rect(0f, y, viewRect.width, 28f);
                bool newVal = isSelected;
                Widgets.CheckboxLabeled(rowRect, pawn.LabelCap, ref newVal);
                if (newVal != isSelected)
                {
                    if (newVal) _selected.Add(pawn);
                    else _selected.Remove(pawn);
                }
                y += 30f;
            }

            Widgets.EndScrollView();

            listing.Gap(8f);
            if (listing.ButtonText("Enviar"))
            {
                Send();
            }

            listing.End();
        }

        /// <summary>
        /// Saca referencias que solo tienen sentido dentro de ESTA partida (grupo/pelotón,
        /// políticas de ropa y drogas) para que Scribe no intente guardarlas — del otro lado
        /// esos IDs no significan nada porque es una partida distinta. El destino le asigna
        /// sus propias políticas por defecto al recibirlo.
        /// </summary>
        private static void SanitizeForTransfer(Pawn pawn)
        {
            LordUtility.GetLord(pawn)?.RemovePawn(pawn);

            if (pawn.outfits != null) pawn.outfits.CurrentApparelPolicy = null;
            if (pawn.drugs != null) pawn.drugs.CurrentPolicy = null;

            // Igual que con las políticas: la ideología es un objeto de ESTA partida (Ideo_X);
            // del otro lado ese id no existe, así que lo sacamos acá. El destino le asigna la
            // ideología principal de su propia colonia al recibirlo.
            try { pawn.ideo?.SetIdeo(null); } catch { /* no todos los pawns lo permiten, no es crítico */ }
        }

        private void Send()
        {
            if (_selected.Count == 0)
            {
                Messages.Message("Elegí al menos un colono.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var blobs = new List<string>();
            foreach (var pawn in _selected)
            {
                SanitizeForTransfer(pawn);

                string xml = PawnTransfer.SerializePawn(pawn);
                if (string.IsNullOrEmpty(xml)) continue;

                blobs.Add(xml);
                if (pawn.Spawned) pawn.Destroy(DestroyMode.Vanish);
            }

            if (blobs.Count == 0)
            {
                Messages.Message("No se pudo preparar a ningún colono para el viaje.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            CoopClient.Instance.SendJoinRequest(_target.RemotePlayerId, blobs);
            Messages.Message($"Se mandaron {blobs.Count} colono(s). Esperando confirmación del destino...", MessageTypeDefOf.NeutralEvent, false);
            Close();
        }
    }
}
