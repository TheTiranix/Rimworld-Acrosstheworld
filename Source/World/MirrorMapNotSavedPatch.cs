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
    ///
    /// Cada cosa, región y distrito guarda el NÚMERO de su mapa en Game.Maps. Antes se sacaban los espejos de la lista y se
    /// volvían a poner al final: si después de un espejo había otro mapa (el de una caravana, por ejemplo), sus cosas quedaban
    /// apuntando al mapa equivocado después de cada guardado. Ahora los espejos se llevan primero al final de la lista
    /// corrigiendo esos números (igual que hace el juego al sacar un mapa, ver MapDeiniter), y recién ahí se sacan.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.ExposeData))]
    public static class Game_ExposeData_SkipMirrorMaps_Patch
    {
        [ThreadStatic] private static List<Map> _removedMirrorMaps;
        [ThreadStatic] private static sbyte _savedCurrentMapIndex;

        private static readonly AccessTools.FieldRef<Thing, sbyte> ThingMapIndex = AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");

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
                    .OrderBy(m => __instance.Maps.IndexOf(m))
                    .ToList();
                if (mirrorMaps == null || mirrorMaps.Count == 0) return;
                foreach (var m in mirrorMaps) MoveToEnd(__instance, m);
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("MirrorMapNotSavedPatch.01", e.Message));
                return;
            }

            // Ya están todos juntos al final: sacarlos no corre el número de ningún otro mapa.
            _savedCurrentMapIndex = __instance.currentMapIndex;
            Map current = _savedCurrentMapIndex >= 0 && _savedCurrentMapIndex < __instance.Maps.Count ? __instance.Maps[_savedCurrentMapIndex] : null;
            foreach (var m in mirrorMaps) __instance.Maps.Remove(m);
            _removedMirrorMaps = mirrorMaps;

            // Si estabas mirando un espejo, se guarda como mapa actual uno que sí queda en la partida (si no, al cargar: "Current map
            // index out of bounds").
            if (current != null && mirrorMaps.Contains(current))
                __instance.currentMapIndex = (sbyte)(__instance.Maps.Count > 0 ? 0 : -1);
        }

        // Finalizer y no Postfix: si el guardado falla a mitad, los espejos igual vuelven a la lista.
        [HarmonyFinalizer]
        public static Exception Finalizer(Game __instance, Exception __exception)
        {
            var removed = _removedMirrorMaps;
            _removedMirrorMaps = null;
            if (removed == null || __instance?.Maps == null) return __exception;
            foreach (var m in removed)
                if (m != null && !__instance.Maps.Contains(m)) __instance.Maps.Add(m); // mismo orden: cada uno vuelve a su número
            __instance.currentMapIndex = _savedCurrentMapIndex;
            return __exception;
        }

        /// <summary>Lleva un mapa al final de Game.Maps corrigiendo el número de mapa de sus cosas, regiones y distritos y el de los que se corren.</summary>
        private static void MoveToEnd(Game game, Map map)
        {
            var maps = game.Maps;
            int from = maps.IndexOf(map);
            int last = maps.Count - 1;
            if (from < 0 || from == last) return;

            Map current = game.currentMapIndex >= 0 && game.currentMapIndex < maps.Count ? maps[game.currentMapIndex] : null;
            for (int j = from + 1; j < maps.Count; j++) SetMapIndex(maps[j], (sbyte)(j - 1));
            SetMapIndex(map, (sbyte)last);
            maps.RemoveAt(from);
            maps.Add(map);
            if (current != null) game.currentMapIndex = (sbyte)maps.IndexOf(current);
        }

        private static void SetMapIndex(Map map, sbyte index)
        {
            var things = map.spawnedThings;
            for (int k = 0; k < things.Count; k++) ThingMapIndex(things[k]) = index;
            foreach (var room in map.regionGrid.AllRooms)
                foreach (var district in room.Districts) district.mapIndex = index;
            foreach (var region in map.regionGrid.AllRegions_NoRebuild_InvalidAllowed) region.mapIndex = index;
        }
    }
}
