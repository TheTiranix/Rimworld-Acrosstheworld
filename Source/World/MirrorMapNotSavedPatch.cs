using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Los mapas espejo (CoopPlayerBase.Map, cuando estás "adentro" mirando la base de otro jugador)
    /// no se guardan con la partida: su contenido es transitorio, se reconstruye solo de la red apenas
    /// volvés a entrar (ver CoopSessionManager.EnterCoopMap). Guardarlos de verdad los recargaba rotos
    /// al abrir la partida (faltaba el snowGrid y otros datos por drama de orden de carga), lo que
    /// tiraba miles de excepciones al regenerar el dibujo del mapa y podía dejar títeres/colonos con
    /// datos duplicados o sin dueño claro.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.ExposeData))]
    public static class Game_ExposeData_SkipMirrorMaps_Patch
    {
        [ThreadStatic] private static List<Map> _removedMirrorMaps;

        [HarmonyPrefix]
        public static void Prefix(Game __instance)
        {
            _removedMirrorMaps = null;
            if (Scribe.mode != LoadSaveMode.Saving || __instance?.Maps == null) return;

            List<Map> mirrorMaps = null;
            try
            {
                mirrorMaps = Find.WorldObjects?.AllWorldObjects.OfType<CoopPlayerBase>()
                    .Where(wo => wo.HasMap)
                    .Select(wo => wo.Map)
                    .ToList();
            }
            catch (System.Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo listar mapas espejo antes de guardar: {e.Message}");
                return;
            }
            if (mirrorMaps == null || mirrorMaps.Count == 0) return;

            foreach (var m in mirrorMaps) __instance.Maps.Remove(m);
            _removedMirrorMaps = mirrorMaps;
        }

        [HarmonyPostfix]
        public static void Postfix(Game __instance)
        {
            var removed = _removedMirrorMaps;
            _removedMirrorMaps = null;
            if (removed == null || __instance?.Maps == null) return;
            foreach (var m in removed)
                if (m != null && !__instance.Maps.Contains(m)) __instance.Maps.Add(m);
        }
    }
}
