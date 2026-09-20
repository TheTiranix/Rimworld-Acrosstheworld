using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimCoopMod.World
{
    /// <summary>
    /// La IA "libre" vive en el pawn REAL (el que está en el juego del dueño): decide sola qué
    /// hacer, y el títere lo sigue trabajo por trabajo (ver SyncPuppetJob). Si el títere además
    /// eligiera sus propios trabajos, en cada juego harían cosas distintas (dos IA con dados
    /// distintos) — así que el títere no busca trabajo por su cuenta.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "TryFindAndStartJob")]
    public static class Pawn_JobTracker_TryFindAndStartJob_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_JobTracker __instance)
        {
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            return !PuppetPawnRegistry.IsPuppet(pawn);
        }
    }

    /// <summary>Un títere no tiene crisis mentales propias: si el pawn real la tiene, se ve por su trabajo.</summary>
    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class MentalStateHandler_TryStartMentalState_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(MentalStateHandler __instance)
        {
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            return !PuppetPawnRegistry.IsPuppet(pawn);
        }
    }

    /// <summary>El fuego del espejo es solo para verse: no se propaga ni quema nada del mapa espejo.</summary>
    [HarmonyPatch(typeof(Fire), "Tick")]
    public static class Fire_Tick_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Fire __instance)
        {
            return CoopSessionManager.GetHostPlayerIdForMap(__instance.Map) < 0;
        }
    }

    /// <summary>Cambios de velocidad (1x/2x/3x): se avisa a todos para que el ritmo sea el mismo.</summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.CurTimeSpeed), MethodType.Setter)]
    public static class TickManager_CurTimeSpeed_Broadcast_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(TickManager __instance, TimeSpeed value)
        {
            if (value == TimeSpeed.Paused || __instance.CurTimeSpeed != value) return; // la pausa se vota; y si el cambio fue bloqueado no se avisa
            CoopSessionManager.BroadcastSpeed(value);
        }
    }
}

namespace RimCoopMod.World
{
    /// <summary>Cuando yo termino un proyecto, todos lo tienen (investigación compartida).</summary>
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    public static class ResearchManager_FinishProject_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ResearchProjectDef proj)
        {
            CoopSessionManager.OnLocalProjectFinished(proj);
        }
    }

    /// <summary>Condiciones globales (eclipse, llamarada solar, etc.): se avisa para aplicarlas en todas las bases.</summary>
    [HarmonyPatch(typeof(GameConditionManager), nameof(GameConditionManager.RegisterCondition))]
    public static class GameConditionManager_RegisterCondition_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GameConditionManager __instance, GameCondition cond)
        {
            CoopSessionManager.OnLocalConditionRegistered(__instance, cond);
        }
    }
}

namespace RimCoopMod.World
{
    /// <summary>Filtra solo el ruido conocido de copiar colonos (ver PawnTransfer.KnownNoise).</summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Error), new[] { typeof(string) })]
    public static class Log_Error_Quiet_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(string text) => !PawnTransfer.ShouldSuppressLog(text);
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Warning), new[] { typeof(string) })]
    public static class Log_Warning_Quiet_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(string text) => !PawnTransfer.ShouldSuppressLog(text);
    }

    /// <summary>
    /// El títere tiene que poder seguir al pawn real por donde pase, incluso por una puerta que a su
    /// facción (ej. un enemigo) no le abriría: si no encuentra camino, se teletransporta a saltos.
    /// </summary>
    [HarmonyPatch(typeof(Building_Door), "PawnCanOpen")]
    public static class Building_Door_PawnCanOpen_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn p, ref bool __result)
        {
            if (!__result && p != null && PuppetPawnRegistry.IsPuppet(p)) __result = true;
        }
    }
}
