using System.Linq;
using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// SettleInEmptyTileUtility.Settle (ver SettlementSyncPatch) solo dispara cuando se funda
    /// una colonia ADICIONAL con una caravana en curso. La colonia inicial de una partida nueva
    /// (Page_SelectStartingSite) y la recarga de una partida guardada arman el mapa y los colonos
    /// por otro camino que nunca pasa por ahí. GameComponentUtility.StartedNewGame/LoadedGame
    /// corre una sola vez, ya con el mapa y los colonos listos, así que es el punto correcto
    /// para cubrir ambos casos.
    /// </summary>
    [HarmonyPatch(typeof(GameComponentUtility))]
    public static class GameComponentUtility_GameStart_Patch
    {
        [HarmonyPatch(nameof(GameComponentUtility.StartedNewGame))]
        [HarmonyPostfix]
        public static void StartedNewGamePostfix() => NotifyIfHomeMapExists(cleanupSavedBases: false); // partida nueva: las bases que se vieron al elegir sitio son validas, no hay que borrarlas

        [HarmonyPatch(nameof(GameComponentUtility.LoadedGame))]
        [HarmonyPostfix]
        public static void LoadedGamePostfix() => NotifyIfHomeMapExists(cleanupSavedBases: true);

        private static void NotifyIfHomeMapExists(bool cleanupSavedBases)
        {
            // Las bases de otros jugadores (y sus mapas espejo) que quedaron guardadas en la partida son
            // viejas y el mod crea las suyas por red: si no se limpian, aparecen duplicadas.
            if (cleanupSavedBases) CoopSessionManager.CleanupSavedMirrorState();

            if (!CoopClient.Instance.IsConnected) return;
            CoopClient.Instance.SendPlayersRequest();

            var homeMap = Find.Maps?.FirstOrDefault(m => m.IsPlayerHome);
            if (homeMap == null) return;

            CoopSessionManager.NotifyLocalSettlement(homeMap.Tile, homeMap.mapPawns.FreeColonistsCount);
        }
    }
}
