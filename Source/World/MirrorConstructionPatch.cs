using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimCoopMod.GameComponents;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimCoopMod.World
{
    /// <summary>
    /// Los títeres del mapa espejo ejecutan de verdad los trabajos del pawn real (para que se vean caminar y trabajar), incluida la
    /// construcción. Pero construir de verdad EN EL ESPEJO no tiene sentido y rompe la copia: el dueño real es quien construye, y lo
    /// que hace el títere acá crea cosas que el espejo no rastrea:
    ///  - la obra puede "fallar" (el títere es de facción jugador y tiene chance de error), lo que destruye el marco y crea un plano
    ///    nuevo suelto, una y otra vez ("Construcción fallida" varias veces por segundo sobre la misma cama);
    ///  - al completarse queda un edificio local y, si antes había fallado, un plano que ya nadie borra (plano arriba de un muro ya hecho).
    /// Acá se bloquean las tres operaciones que cambian el mapa al construir cuando el mapa es un espejo; el progreso real llega por la
    /// foto de la base del dueño.
    /// </summary>
    internal static class MirrorConstruction
    {
        public static bool OnMirrorMap(Thing thing)
        {
            var map = thing?.Map;
            return map != null && CoopSessionManager.GetHostPlayerIdForMap(map) >= 0;
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.FailConstruction))]
    public static class Frame_FailConstruction_Mirror_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Frame __instance) => !MirrorConstruction.OnMirrorMap(__instance);
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class Frame_CompleteConstruction_Mirror_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Frame __instance) => !MirrorConstruction.OnMirrorMap(__instance);
    }

    /// <summary>Un plano se convierte en marco cuando alguien le entrega el primer material: en el espejo lo hace solo el dueño real.</summary>
    [HarmonyPatch]
    public static class Blueprint_TryReplaceWithSolidThing_Mirror_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // GetTypes() sobre un ensamblado grande puede tirar ReflectionTypeLoadException si algún tipo depende de algo que no
            // está: en ese caso se sigue con los que sí cargaron, en vez de romper el arranque del mod.
            Type[] types;
            try { types = typeof(Blueprint).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            foreach (var type in types.Where(t => typeof(Blueprint).IsAssignableFrom(t)))
            {
                var method = AccessTools.DeclaredMethod(type, "TryReplaceWithSolidThing");
                if (method != null) yield return method;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix(Blueprint __instance, Pawn workerPawn, out Thing createdThing, out bool jobEnded, ref bool __result)
        {
            createdThing = null;
            jobEnded = false;
            if (!MirrorConstruction.OnMirrorMap(__instance)) return true;

            // Igual que cuando algo bloquea la obra en el juego normal: se corta el trabajo y no se crea nada.
            try { workerPawn?.jobs?.EndCurrentJob(JobCondition.Incompletable); } catch { }
            jobEnded = true;
            __result = false;
            return false;
        }
    }
}
