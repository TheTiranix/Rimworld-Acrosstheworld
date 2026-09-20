using System.Linq;
using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld.Planet;

namespace RimCoopMod.World
{
    /// <summary>
    /// Se dispara justo cuando el jugador funda su colonia (botón "Asentarse" con la
    /// caravana en un tile vacío). Captura el tile exacto elegido y el número de colonos
    /// ANTES de que Settle() disuelva la caravana, y avisa al servidor para que los demás
    /// vean la base en ese mismo lugar del mapa mundial.
    /// </summary>
    [HarmonyPatch(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.Settle))]
    public static class SettleInEmptyTileUtility_Settle_Patch
    {
        public struct CapturedSettleInfo
        {
            public PlanetTile Tile;
            public int ColonistCount;
        }

        [HarmonyPrefix]
        public static void Prefix(Caravan caravan, out CapturedSettleInfo __state)
        {
            __state = new CapturedSettleInfo
            {
                Tile = caravan != null ? caravan.Tile : PlanetTile.Invalid,
                ColonistCount = caravan?.PawnsListForReading?.Count(p => p.IsFreeColonist) ?? 0
            };
        }

        [HarmonyPostfix]
        public static void Postfix(CapturedSettleInfo __state)
        {
            if (!__state.Tile.Valid) return;
            if (!CoopClient.Instance.IsConnected) return;

            CoopSessionManager.NotifyLocalSettlement(__state.Tile, __state.ColonistCount);
        }
    }
}
