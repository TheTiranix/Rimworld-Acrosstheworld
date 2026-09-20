using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Nadie puede pausar o despausar el juego compartido por su cuenta — hay que proponerlo y que
    /// los demás lo acepten (ver CoopSessionManager.RequestPauseToggle). Esto cubre tanto la barra
    /// espaciadora (TogglePaused) como los botones de velocidad (CurTimeSpeed = Paused/Normal).
    /// Si el mod no está conectado a un servidor, no interviene: el single-player sigue normal.
    /// </summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TogglePaused))]
    public static class TickManager_TogglePaused_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!CoopClient.Instance.IsConnected || CoopSessionManager.IsApplyingVotedPauseChange) return true;

            CoopSessionManager.RequestPauseToggle();
            return false;
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.CurTimeSpeed), MethodType.Setter)]
    public static class TickManager_CurTimeSpeed_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(TickManager __instance, TimeSpeed value)
        {
            if (!CoopClient.Instance.IsConnected || CoopSessionManager.IsApplyingVotedPauseChange) return true;

            bool togglingPauseState = (value == TimeSpeed.Paused) != (__instance.CurTimeSpeed == TimeSpeed.Paused);
            if (!togglingPauseState) return true; // pasar de Normal a Rápido, etc. no necesita voto

            CoopSessionManager.RequestPauseToggle();
            return false;
        }
    }
}
