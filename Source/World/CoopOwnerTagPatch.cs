using HarmonyLib;
using RimCoopMod.GameComponents;
using UnityEngine;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Cartelito chico arriba de la cabeza de cualquier colono que no sea tuyo, con el nombre de su dueño: un
    /// títere en el mapa espejo de otra base, o un colono real que te mandaron a la tuya con Colaborar. Un color
    /// distinto por jugador (CoopSessionManager.ColorForPlayer) para distinguir de un vistazo a varios jugadores
    /// mezclados en la misma base. Se engancha al mismo dibujado del nombre normal del pawn, así respeta la
    /// configuración de "mostrar nombres" y el nivel de zoom de cada uno (si no se ve el nombre, tampoco esto).
    /// </summary>
    [HarmonyPatch(typeof(PawnUIOverlay), nameof(PawnUIOverlay.DrawPawnGUIOverlay))]
    public static class PawnUIOverlay_DrawPawnGUIOverlay_OwnerTag_Patch
    {
        // Un poco más arriba que la cabeza: el nombre normal del juego se dibuja debajo del pawn (offset -0.6),
        // este va del lado opuesto para no pisarlo.
        private const float TagWorldOffsetZ = 0.9f;

        public static void Postfix(Pawn ___pawn)
        {
            Pawn pawn = ___pawn;
            if (pawn == null || !pawn.RaceProps.Humanlike) return;
            // Mismas condiciones que el método original para no dibujar cuando él tampoco lo hizo (fogueado, sin mapa, etc.).
            if (!pawn.Spawned || pawn.Map?.fogGrid == null || pawn.Map.fogGrid.IsFogged(pawn.Position)) return;

            int ownerId = CoopSessionManager.GetOwnerPlayerIdForTag(pawn);
            if (ownerId < 0) return;
            string name = CoopSessionManager.GetKnownPlayerName(ownerId);
            if (string.IsNullOrEmpty(name)) return;

            try
            {
                Vector2 pos = GenMapUI.LabelDrawPosFor(pawn, TagWorldOffsetZ);
                GenMapUI.DrawThingLabel(pos, name, CoopSessionManager.ColorForPlayer(ownerId));
            }
            catch { /* un cartelito de menos no debería tirar abajo el dibujado del resto del mapa */ }
        }
    }
}
