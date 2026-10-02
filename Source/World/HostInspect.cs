using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Texto de inspección REAL (el que ve el dueño) de ciertos edificios con estado interno que no viaja (cuba de crecimiento,
    /// gestador de mechs, ensamblador de genes...). El dueño lo manda como parte del estado del edificio (ver BuildStateString)
    /// y en el espejo reemplaza al texto propio, que sin ese estado saldría vacío o con datos que no son.
    /// </summary>
    public static class HostInspect
    {
        private static readonly ConditionalWeakTable<Thing, string> Texts = new ConditionalWeakTable<Thing, string>();

        public static void Set(Thing thing, string text)
        {
            Texts.Remove(thing);
            if (!string.IsNullOrEmpty(text)) Texts.Add(thing, text);
        }

        public static string Get(Thing thing) => thing != null && Texts.TryGetValue(thing, out var text) ? text : null;
    }

    // Building no redefine GetInspectString (lo hace ThingWithComps): los edificios con texto propio (cuba de crecimiento, etc.) lo
    // sobreescriben llamando a base, así que por acá pasan todos.
    [HarmonyPatch(typeof(ThingWithComps), nameof(ThingWithComps.GetInspectString))]
    public static class ThingWithComps_GetInspectString_HostInspect_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ThingWithComps __instance, ref string __result)
        {
            if (!(__instance is Building)) return;
            string host = HostInspect.Get(__instance);
            if (!string.IsNullOrEmpty(host)) __result = host;
        }
    }
}
