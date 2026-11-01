using HarmonyLib;
using RimWorld;
using Verse;

namespace RimCoopMod.World
{
    // Los títeres SÍ hacen Tick (sin eso no caminan ni se animan sus trabajos), pero partes de la "vida" de un pawn mandan cartas o
    // cambian cosas en la partida de quien mira: cumpleaños y momentos de crecimiento (elegir rasgos para el hijo de otro), charlas
    // y romances entre títeres ("nuevos amantes"), inspiraciones, lo que hace un creepjoiner de Anomaly. Todo eso ya pasa en el
    // juego del dueño y llega con la foto (edad, relaciones, estado): acá se saltea.

    [HarmonyPatch(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.AgeTickInterval))]
    public static class Puppet_AgeTick_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn) => !PuppetPawnRegistry.IsPuppet(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.InteractionsTrackerTickInterval))]
    public static class Puppet_InteractionsTick_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn) => !PuppetPawnRegistry.IsPuppet(___pawn);
    }

    [HarmonyPatch(typeof(InspirationHandler), nameof(InspirationHandler.InspirationHandlerTickInterval))]
    public static class Puppet_InspirationTick_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(InspirationHandler __instance) => !PuppetPawnRegistry.IsPuppet(__instance.pawn);
    }

    [HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.RelationsTrackerTickInterval))]
    public static class Puppet_RelationsTick_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn ___pawn) => !PuppetPawnRegistry.IsPuppet(___pawn);
    }

    [HarmonyPatch(typeof(Pawn_CreepJoinerTracker), nameof(Pawn_CreepJoinerTracker.TickInterval))]
    public static class Puppet_CreepJoinerTick_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_CreepJoinerTracker __instance) => !PuppetPawnRegistry.IsPuppet(__instance.Pawn);
    }
}
