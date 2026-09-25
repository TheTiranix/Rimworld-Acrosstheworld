using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>Cada carta que le llega al dueño real (incursión, plaga, etc.) también se le cuenta a quien lo mira.</summary>
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class LetterStack_ReceiveLetter_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Letter let)
        {
            CoopSessionManager.ForwardLetter(let);
        }
    }

    /// <summary>Burbuja social: el dueño real avisa cada interacción para que se vea también en el espejo.</summary>
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static class Pawn_InteractionsTracker_TryInteractWith_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, bool __result)
        {
            if (!__result) return;
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (pawn != null && recipient != null) CoopSessionManager.RecordInteraction(pawn, recipient, intDef);
        }
    }

    /// <summary>
    /// Un títere solo muere cuando el dueño real dice que murió (ver SyncPuppetPawns): con las
    /// heridas copiadas, su propio juego podría matarlo antes de tiempo y dejar un cadáver falso.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Pawn_Kill_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn __instance)
        {
            return !PuppetPawnRegistry.IsPuppet(__instance);
        }
    }

    /// <summary>Un colono que viajó entre jugadores murió: se da de baja del registro del servidor para que no se lo reconstruya después.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Pawn_Kill_ColonistLedger_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance)
        {
            if (PuppetPawnRegistry.IsPuppet(__instance)) return;
            CoopSessionManager.OnColonistKilled(__instance);
        }
    }

    /// <summary>
    /// El títere nunca tiene su propio Ideo real resuelto (es un objeto de la partida del dueño, no
    /// cruza — ver PawnSnapshot.IdeoName), así que se le agrega el nombre a mano en el panel de
    /// inspección para no perder esa info del todo.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
    public static class Pawn_GetInspectString_PuppetIdeo_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, ref string __result)
        {
            string ideoName = CoopSessionManager.GetPuppetIdeoName(__instance);
            if (!string.IsNullOrEmpty(ideoName))
                __result = string.IsNullOrEmpty(__result) ? $"Ideología: {ideoName}" : __result + $"\nIdeología: {ideoName}";

            // Biotech: ancho de banda del mecanitor, o supervisor y modo de trabajo del mech.
            string biotech = CoopSessionManager.GetPuppetBiotechInfo(__instance);
            if (!string.IsNullOrEmpty(biotech))
                __result = string.IsNullOrEmpty(__result) ? biotech : __result + "\n" + biotech;
        }
    }

    // ---- Configuración de colonos transferidos, vista desde el espejo: se le pide al dueño real ----

    internal static class PuppetSettingHelper
    {
        /// <summary>true = seguir con el código normal del juego; false = ya se manejó (mandado al dueño o rechazado).</summary>
        public static bool ShouldRunLocally(Pawn pawn, string kind, string key, string value)
        {
            if (pawn == null || CoopSessionManager.ApplyingMirrorSetting) return true;
            if (!PuppetPawnRegistry.TryGetInfo(pawn, out var info)) return true;

            if (!CoopSessionManager.CanLocalPlayerCommand(pawn))
            {
                Messages.Message("Ese no es tu colono.", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            CoopClient.Instance.SendPawnSetting(info.HostPlayerId, info.HostPawnId, kind, key, value);
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
    public static class Pawn_WorkSettings_SetPriority_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_WorkSettings __instance, WorkTypeDef w, int priority)
        {
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            return PuppetSettingHelper.ShouldRunLocally(pawn, "workprio", w?.defName, priority.ToString());
        }
    }

    [HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment))]
    public static class Pawn_TimetableTracker_SetAssignment_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_TimetableTracker __instance, int hour, TimeAssignmentDef ta)
        {
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            return PuppetSettingHelper.ShouldRunLocally(pawn, "timetable", hour.ToString(), ta?.defName);
        }
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Setter)]
    public static class Pawn_PlayerSettings_AreaRestriction_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_PlayerSettings __instance, Area value)
        {
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            return PuppetSettingHelper.ShouldRunLocally(pawn, "area", "", value?.Label ?? "");
        }
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.Master), MethodType.Setter)]
    public static class Pawn_PlayerSettings_Master_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_PlayerSettings __instance, Pawn value)
        {
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            int masterId = -1;
            if (value != null)
            {
                masterId = CoopSessionManager.ResolveHostThingId(value);
                if (masterId < 0 && PuppetPawnRegistry.IsPuppet(pawn)) return false; // el amo tiene que ser alguien de esa misma base
            }
            return PuppetSettingHelper.ShouldRunLocally(pawn, "master", "", masterId.ToString());
        }
    }

    [HarmonyPatch(typeof(Pawn_TrainingTracker), nameof(Pawn_TrainingTracker.SetWantedRecursive))]
    public static class Pawn_TrainingTracker_SetWantedRecursive_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_TrainingTracker __instance, TrainableDef td, bool checkOn)
        {
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            return PuppetSettingHelper.ShouldRunLocally(pawn, "training", td?.defName, checkOn ? "1" : "0");
        }
    }

    // ---- Edición de áreas (hogar, techo, nieve, permitidas) desde el mapa espejo ----

    [HarmonyPatch]
    public static class AreaDesignators_DesignateSingleCell_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in new[]
            {
                typeof(Designator_AreaHome), typeof(Designator_AreaBuildRoof), typeof(Designator_AreaNoRoof),
                typeof(Designator_AreaSnowClear), typeof(Designator_AreaPollutionClear), typeof(Designator_AreaAllowedExpand), typeof(Designator_AreaAllowedClear)
            })
            {
                var m = AccessTools.DeclaredMethod(t, "DesignateSingleCell");
                if (m != null) yield return m;
            }
        }

        // Se deja correr el código normal (así el cambio se ve al instante en el espejo) y además se le pide al dueño.
        [HarmonyPrefix]
        public static void Prefix(Designator __instance, IntVec3 c)
        {
            int host = CoopSessionManager.GetHostPlayerIdForMap(Find.CurrentMap);
            if (host < 0) return;

            string kind; string label = "";
            if (__instance is Designator_AreaHome) kind = "home";
            else if (__instance is Designator_AreaBuildRoof) kind = "buildroof";
            else if (__instance is Designator_AreaNoRoof) kind = "noroof";
            else if (__instance is Designator_AreaSnowClear) kind = "snow";
            else if (__instance is Designator_AreaPollutionClear) kind = "pollution";
            else if (__instance is Designator_AreaAllowed)
            {
                kind = "allowed";
                label = Designator_AreaAllowed.SelectedArea?.Label ?? "";
                if (label == "") return;
            }
            else return;

            bool add = __instance is Designator_AreaPollutionClear
                ? !(__instance is Designator_AreaPollutionClearClear) // el "clear" no expone el modo por el mismo camino: se decide por tipo
                : Traverse.Create(__instance).Field("mode").GetValue<DesignateMode>() == DesignateMode.Add;
            CoopSessionManager.QueueAreaEdit(host, kind, add, label, c);
        }
    }
}
