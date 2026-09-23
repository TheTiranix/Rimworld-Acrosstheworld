using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// La barra de colonos (arriba a la izquierda) junta los "colonos libres" de TODOS los mapas
    /// cargados, no solo el propio — no filtra por si el mapa es tu base o no. Los títeres del mapa
    /// espejo se marcan con facción jugador para que se vean e interactúen bien (ver
    /// CoopSessionManager.Extras.ApplyPawnSnapshot), así que sin este parche aparecían ahí como si
    /// fueran colonos tuyos duplicados, con el mismo nombre que el colono real que representan del
    /// otro lado. Ojo: los colonos que SÍ son tuyos (los que mandaste a colaborar) también son
    /// títeres ahí — a esos hay que dejarlos, porque los podés seguir ordenando desde la barra.
    /// </summary>
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.FreeColonists), MethodType.Getter)]
    public static class MapPawns_FreeColonists_ExcludePuppets_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref List<Pawn> __result)
        {
            if (__result == null || __result.Count == 0) return;
            bool anyForeignPuppet = false;
            for (int i = 0; i < __result.Count; i++)
            {
                if (IsForeignPuppet(__result[i])) { anyForeignPuppet = true; break; }
            }
            if (!anyForeignPuppet) return;
            __result = __result.Where(p => !IsForeignPuppet(p)).ToList();
        }

        internal static bool IsForeignPuppet(Pawn pawn)
        {
            if (!PuppetPawnRegistry.TryGetInfo(pawn, out var info)) return false;

            var snap = CoopSessionManager.GetRemoteSnapshot(info.HostPlayerId);
            var ps = snap?.Pawns.FirstOrDefault(p => p.PawnId == info.HostPawnId);
            if (ps == null) return true; // sin datos todavía: por las dudas no lo mostramos como propio

            return ps.OwnerPlayerId != CoopClient.Instance.LocalPlayerId;
        }
    }

    /// <summary>
    /// Anomaly: los ghouls/shamblers de tu facción (subhumanos controlables) también salen en la barra de colonos.
    /// Mismo problema y mismo arreglo que MapPawns_FreeColonists_ExcludePuppets_Patch.
    /// </summary>
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.ColonySubhumansControllable), MethodType.Getter)]
    public static class MapPawns_ColonySubhumansControllable_ExcludePuppets_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref List<Pawn> __result)
        {
            if (__result == null || __result.Count == 0) return;
            bool anyForeignPuppet = false;
            for (int i = 0; i < __result.Count; i++)
            {
                if (MapPawns_FreeColonists_ExcludePuppets_Patch.IsForeignPuppet(__result[i])) { anyForeignPuppet = true; break; }
            }
            if (!anyForeignPuppet) return;
            __result = __result.Where(p => !MapPawns_FreeColonists_ExcludePuppets_Patch.IsForeignPuppet(p)).ToList();
        }
    }
}
