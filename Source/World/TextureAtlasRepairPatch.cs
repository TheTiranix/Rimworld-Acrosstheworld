using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// PawnTextureAtlas guarda a los pawns en un Dictionary&lt;Pawn, ...&gt; y Pawn.GetHashCode() es su thingIDNumber. Si el número de
    /// un pawn cambia mientras está ahí adentro (los colonos y títeres recibidos se renumeran, ver PawnTransfer.ReassignUniqueIds),
    /// su clave queda con el hash viejo: al liberar frames el juego busca "frameAssignments[pawn]", no lo encuentra, y tira
    /// KeyNotFoundException en CADA frame para siempre ("The given key '...' was not present in the dictionary" en
    /// GlobalTextureAtlasManagerUpdate) — el resto del Update() del juego se aborta y los colonos dejan de dibujarse bien.
    ///
    /// Este parche solo repara el daño: si GC() falla, reconstruye el diccionario con los hashes actuales (soltando los frames de los
    /// pawns que ya no están en el mapa) y sigue, en vez de dejar el error repitiéndose.
    /// </summary>
    [HarmonyPatch(typeof(PawnTextureAtlas), nameof(PawnTextureAtlas.GC))]
    public static class PawnTextureAtlas_GC_Repair_Patch
    {
        private static int _repairs;

        [HarmonyFinalizer]
        public static Exception Finalizer(PawnTextureAtlas __instance, Exception __exception)
        {
            if (__exception == null) return null;

            try
            {
                var trav = Traverse.Create(__instance);
                var assignments = trav.Field("frameAssignments").GetValue<Dictionary<Pawn, PawnTextureAtlasFrameSet>>();
                var free = trav.Field("freeFrameSets").GetValue<List<PawnTextureAtlasFrameSet>>();
                if (assignments == null || free == null) return __exception;

                var rebuilt = new Dictionary<Pawn, PawnTextureAtlasFrameSet>();
                foreach (var kv in assignments.ToList()) // enumerar no usa el hash: sirve aunque las claves estén desfasadas
                {
                    if (kv.Key == null || !kv.Key.SpawnedOrAnyParentSpawned || rebuilt.ContainsKey(kv.Key)) free.Add(kv.Value);
                    else rebuilt[kv.Key] = kv.Value;
                }
                trav.Field("frameAssignments").SetValue(rebuilt);

                if (_repairs++ < 3) CoopLog.Warning(Loc.T("TextureAtlasRepairPatch.01", __exception.GetType().Name));
                return null; // error absorbido: no se repite en cada frame
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("TextureAtlasRepairPatch.02", e.Message));
                return __exception;
            }
        }
    }
}
