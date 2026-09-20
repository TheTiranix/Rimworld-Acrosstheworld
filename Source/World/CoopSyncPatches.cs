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

namespace RimCoopMod.World
{
    /// <summary>
    /// Usar un permiso de Royalty con un colono del mapa espejo: en vez de ejecutarlo acá (donde no existe la
    /// base real), se le pide al dueño que lo use con ese colono en su mapa.
    /// </summary>
    [HarmonyPatch]
    public static class RoyalPermit_OrderForceTarget_Patch
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var t in new[]
            {
                typeof(RoyalTitlePermitWorker_CallAid), typeof(RoyalTitlePermitWorker_DropResources), typeof(RoyalTitlePermitWorker_CallLaborers),
                typeof(RoyalTitlePermitWorker_CallShuttle), typeof(RoyalTitlePermitWorker_OrbitalStrike)
            })
            {
                var m = AccessTools.DeclaredMethod(t, "OrderForceTarget");
                if (m != null) yield return m;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(RoyalTitlePermitWorker_Targeted __instance, LocalTargetInfo target)
        {
            var caller = Traverse.Create(__instance).Field("caller").GetValue<Pawn>();
            if (caller == null || !PuppetPawnRegistry.TryGetInfo(caller, out var info)) return true;

            if (!CoopSessionManager.CanLocalPlayerCommand(caller))
            {
                Messages.Message("Ese no es tu colono.", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            var def = Traverse.Create(__instance).Field("def").GetValue<RoyalTitlePermitDef>();
            CoopClient.Instance.SendPawnSetting(info.HostPlayerId, info.HostPawnId, "permit", def?.defName ?? "", target.Cell.x + "," + target.Cell.z);
            Messages.Message("Se pidió usar el permiso en la base del dueño.", MessageTypeDefOf.NeutralEvent, false);
            return false;
        }
    }
}

namespace RimCoopMod.World
{
    /// <summary>Misiones compartidas: cuando la misión dispara una incursión/amenaza, sube un 35 % por cada jugador sumado.</summary>
    [HarmonyPatch(typeof(QuestPart_Incident), nameof(QuestPart_Incident.Notify_QuestSignalReceived))]
    public static class QuestPart_Incident_Scale_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(QuestPart_Incident __instance, Signal signal)
        {
            if (signal.tag == __instance.inSignal) CoopSessionManager.ScaleQuestIncident(__instance);
        }
    }

    [HarmonyPatch(typeof(QuestPartActivable), "Enable")]
    public static class QuestPartActivable_Enable_Scale_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(QuestPartActivable __instance)
        {
            if (__instance is QuestPart_ThreatsGenerator generator) CoopSessionManager.ScaleQuestThreats(generator);
        }
    }
}

namespace RimCoopMod.World
{
    /// <summary>
    /// Las copias de misiones compartidas no "juegan": no avanzan por su cuenta ni reaccionan a señales o eventos. Solo las
    /// actualiza el dueño (que es quien corre la misión de verdad); así no se duplican amenazas ni recompensas.
    /// </summary>
    [HarmonyPatch]
    public static class Quest_Inert_Patches
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "QuestTick", "Notify_SignalReceived", "Notify_PawnDiscarded", "Notify_ThingsProduced", "Notify_PlantHarvested", "Notify_PawnKilled", "Notify_PawnBorn", "Notify_FactionRemoved" })
            {
                var m = AccessTools.DeclaredMethod(typeof(Quest), name);
                if (m != null) yield return m;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(Quest __instance) => !CoopSessionManager.IsMirroredQuest(__instance);
    }

    /// <summary>Recompensa de objetos de una misión compartida: llega a todos los que se sumaron, no solo al dueño.</summary>
    [HarmonyPatch(typeof(QuestPart_DropPods), nameof(QuestPart_DropPods.Notify_QuestSignalReceived))]
    public static class QuestPart_DropPods_Share_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(QuestPart_DropPods __instance, Signal signal) => CoopSessionManager.ShareQuestRewardItems(__instance, signal);
    }

    [HarmonyPatch(typeof(QuestPart_GiveRoyalFavor), nameof(QuestPart_GiveRoyalFavor.Notify_QuestSignalReceived))]
    public static class QuestPart_GiveRoyalFavor_Share_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(QuestPart_GiveRoyalFavor __instance, Signal signal) => CoopSessionManager.ShareQuestRoyalFavor(__instance, signal);
    }
}
