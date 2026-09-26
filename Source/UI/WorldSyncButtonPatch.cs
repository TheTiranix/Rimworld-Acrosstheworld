using HarmonyLib;
using RimCoopMod.Networking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>
    /// A veces un asentamiento de otro jugador no aparece en el mapa mundial (se perdió el PlayerUpdate,
    /// se entró después de que se mandó, etc.). El servidor ya sabe reenviar la ubicación de todos ante
    /// un PlayersRequest (ver CoopServer/InitialColonySyncPatch); acá solo agregamos un botón para pedirlo
    /// de nuevo a mano sin tener que reconectarse. Se engancha en WorldGlobalControls porque ese OnGUI
    /// solo se llama mientras el mapa mundial está abierto (es el mismo panel de velocidad/fecha de ahí).
    /// </summary>
    [HarmonyPatch(typeof(WorldGlobalControls), nameof(WorldGlobalControls.WorldGlobalControlsOnGUI))]
    public static class WorldGlobalControls_OnGUI_SyncButton_Patch
    {
        private static float _lastRequest = -999f;
        private const float Cooldown = 5f;

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (Event.current.type == EventType.Layout) return;
            if (CoopClient.Instance == null || !CoopClient.Instance.IsConnected) return;

            bool onCooldown = Time.realtimeSinceStartup - _lastRequest < Cooldown;
            var buttonRect = new Rect((float)Verse.UI.screenWidth - 200f, 10f, 190f, 32f);

            if (Widgets.ButtonText(buttonRect, onCooldown ? Loc.T("WorldSyncButtonPatch.01") : Loc.T("WorldSyncButtonPatch.02"), true, true, !onCooldown))
            {
                _lastRequest = Time.realtimeSinceStartup;
                CoopClient.Instance.SendPlayersRequest();
                Messages.Message(Loc.T("WorldSyncButtonPatch.03"), MessageTypeDefOf.NeutralEvent, false);
            }
        }
    }
}
