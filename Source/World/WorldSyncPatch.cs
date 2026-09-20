using HarmonyLib;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Cuando el jugador conectado a un servidor entra a la pantalla de "Crear mundo",
    /// forzamos los parámetros (seed, cobertura, etc.) a los que envió el servidor,
    /// para que todos generen exactamente el mismo planeta.
    /// </summary>
    [HarmonyPatch(typeof(Page_CreateWorldParams), nameof(Page_CreateWorldParams.PreOpen))]
    public static class Page_CreateWorldParams_PreOpen_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Page_CreateWorldParams __instance)
        {
            if (!CoopSessionManager.PendingWorldGeneration) return;
            if (!CoopClient.Instance.IsConnected) return;

            // Estos campos son privados en la clase del juego; se acceden por reflexión
            // con Harmony's Traverse para no depender de que sean públicos.
            var traverse = Traverse.Create(__instance);
            traverse.Field("seedString").SetValue(CoopClient.Instance.LastKnownSeed);
            traverse.Field("planetCoverage").SetValue(CoopClient.Instance.LastKnownCoverage);

            Messages.Message("Usando la seed del servidor: " + CoopClient.Instance.LastKnownSeed, MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
