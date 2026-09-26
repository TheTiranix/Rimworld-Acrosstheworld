using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Si estás construyendo en TU propia colonia, no pasa nada raro. Pero si estás parado en el
    /// mapa espejo de otro jugador (ver EnterCoopMap), colocar un plano ahí no puede hacerse local
    /// — ese mapa no tiene tu economía de recursos ni tus colonos reales. En vez de eso, se manda
    /// el pedido al dueño real, que coloca el plano de verdad en su mapa; el progreso se ve acá
    /// solo porque BroadcastBaseSnapshot ya sincroniza construcciones/planos.
    /// </summary>
    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell))]
    public static class Designator_Build_DesignateSingleCell_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Designator_Build __instance, IntVec3 c)
        {
            int hostPlayerId = CoopSessionManager.GetHostPlayerIdForMap(Find.CurrentMap);
            if (hostPlayerId < 0) return true; // no es un mapa espejo, construir normal

            string defName = __instance.PlacingDef?.defName;
            if (string.IsNullOrEmpty(defName)) return true;

            Rot4 rot = Traverse.Create(__instance).Field("placingRot").GetValue<Rot4>();
            CoopClient.Instance.SendBuildRequest(hostPlayerId, defName, __instance.StuffDef?.defName, c.x, c.z, rot.AsInt);
            Messages.Message(Loc.T("BuildPatch.01"), MessageTypeDefOf.NeutralEvent, false);
            return false;
        }
    }
}

namespace RimCoopMod.World
{
    /// <summary>Desmontar en el mapa espejo: se le pide al dueño real, no se marca localmente.</summary>
    [HarmonyPatch(typeof(Designator_Deconstruct), nameof(Designator_Deconstruct.DesignateThing))]
    public static class Designator_Deconstruct_DesignateThing_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Thing t)
        {
            int host = CoopSessionManager.GetHostPlayerIdForMap(Find.CurrentMap);
            if (host < 0 || t == null) return true;
            CoopSessionManager.SendRemovalRequest(host, CoopSessionManager.BuildActionDeconstruct, t.Position);
            Messages.Message(Loc.T("BuildPatch.02"), MessageTypeDefOf.NeutralEvent, false);
            return false;
        }
    }

    /// <summary>Cancelar planos/obras en el mapa espejo.</summary>
    [HarmonyPatch(typeof(Designator_Cancel), nameof(Designator_Cancel.DesignateThing))]
    public static class Designator_Cancel_DesignateThing_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Thing t)
        {
            int host = CoopSessionManager.GetHostPlayerIdForMap(Find.CurrentMap);
            if (host < 0 || t == null) return true;
            CoopSessionManager.SendRemovalRequest(host, CoopSessionManager.BuildActionCancel, t.Position);
            Messages.Message(Loc.T("BuildPatch.03"), MessageTypeDefOf.NeutralEvent, false);
            return false;
        }
    }
}

namespace RimCoopMod.World
{
    /// <summary>Crear/ampliar zonas (almacén, cultivo) en el mapa espejo: se le pide al dueño.</summary>
    [HarmonyPatch(typeof(Designator_ZoneAdd), nameof(Designator_ZoneAdd.DesignateMultiCell))]
    public static class Designator_ZoneAdd_DesignateMultiCell_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Designator_ZoneAdd __instance, System.Collections.Generic.IEnumerable<IntVec3> cells)
        {
            int host = CoopSessionManager.GetHostPlayerIdForMap(Find.CurrentMap);
            if (host < 0) return true;
            CoopClient.Instance.SendBuildRequest(host, CoopSessionManager.ZoneAddPrefix + __instance.GetType().FullName,
                CoopSessionManager.CellsToCsv(cells), 0, 0, 0);
            Messages.Message(Loc.T("BuildPatch.04"), MessageTypeDefOf.NeutralEvent, false);
            return false;
        }
    }

    /// <summary>Borrar celdas de zona: Designator_ZoneDelete no sobreescribe DesignateMultiCell, así que se intercepta la base.</summary>
    [HarmonyPatch(typeof(Designator), nameof(Designator.DesignateMultiCell))]
    public static class Designator_DesignateMultiCell_ZoneDelete_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Designator __instance, System.Collections.Generic.IEnumerable<IntVec3> cells)
        {
            if (!(__instance is Designator_ZoneDelete)) return true;
            int host = CoopSessionManager.GetHostPlayerIdForMap(Find.CurrentMap);
            if (host < 0) return true;
            CoopClient.Instance.SendBuildRequest(host, CoopSessionManager.ZoneDelete, CoopSessionManager.CellsToCsv(cells), 0, 0, 0);
            Messages.Message(Loc.T("BuildPatch.05"), MessageTypeDefOf.NeutralEvent, false);
            return false;
        }
    }
}
