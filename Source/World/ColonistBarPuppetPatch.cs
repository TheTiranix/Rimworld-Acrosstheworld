using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// La barra de colonos (arriba a la izquierda) junta los "colonos libres" de TODOS los mapas
    /// cargados, no solo el propio — no filtra por si el mapa es tu base o no. Los títeres del mapa
    /// espejo se marcan con facción jugador para que se vean e interactúen bien (ver
    /// CoopSessionManager.Extras.ApplyPawnSnapshot), así que sin este parche aparecen ahí como si
    /// fueran colonos tuyos duplicados, con el mismo nombre que el colono real que representan del
    /// otro lado.
    /// </summary>
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.FreeColonists), MethodType.Getter)]
    public static class MapPawns_FreeColonists_ExcludePuppets_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref List<Pawn> __result)
        {
            if (__result == null || __result.Count == 0) return;
            bool anyPuppet = false;
            for (int i = 0; i < __result.Count; i++)
            {
                if (PuppetPawnRegistry.IsPuppet(__result[i])) { anyPuppet = true; break; }
            }
            if (!anyPuppet) return;
            __result = __result.Where(p => !PuppetPawnRegistry.IsPuppet(p)).ToList();
        }
    }
}
