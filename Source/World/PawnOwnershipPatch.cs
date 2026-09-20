using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimCoopMod.World
{
    /// <summary>
    /// Bloquea cualquier orden directa (clic derecho, prioridad de trabajo forzada, drafteo, etc.)
    /// que el jugador LOCAL intente darle a un pawn que en realidad pertenece a otro jugador
    /// (transferido vía "Colaborar", o un títere del mapa espejo — ver PuppetPawnRegistry).
    /// Si el pawn SÍ es tuyo pero es un títere, la orden no se ejecuta acá (el títere no simula
    /// nada de verdad): se reenvía por red para que el dueño real del mapa la ejecute en SU pawn.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
    public static class Pawn_JobTracker_TryTakeOrderedJob_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_JobTracker __instance, Job job)
        {
            Pawn pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (pawn == null) return true;

            if (!CoopSessionManager.CanLocalPlayerCommand(pawn))
            {
                Messages.Message("Ese no es tu colono.", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            if (PuppetPawnRegistry.TryGetInfo(pawn, out var info))
            {
                // Es tuyo, pero es un títere visual: la orden real (mover, atacar, cosechar, lo
                // que sea) tiene que pasar por la red para que la ejecute el juego que de verdad
                // simula ese mapa.
                if (job != null)
                {
                    string targetDesc = job.targetA.HasThing ? $"Thing {job.targetA.Thing.LabelShortCap} (id local {job.targetA.Thing.thingIDNumber})" : $"celda {job.targetA.Cell}";
                    CoopLog.Message($"[RimCoop] Redirigiendo orden {job.def.defName} sobre {targetDesc} para el pawn títere {pawn.LabelShortCap} al jugador {info.HostPlayerId}.");
                    CoopSessionManager.SendOrder(info.HostPlayerId, info.HostPawnId, job);
                }
                return false;
            }

            return true;
        }
    }
}
