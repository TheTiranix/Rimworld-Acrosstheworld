using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Diagnóstico: cuando el menú de clic derecho se arma para un títere, deja en el log qué opciones salieron
    /// y cuáles se ejecutan solas (el juego abre el menú salvo que TODAS sean "autoTakeable").
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    public static class FloatMenuMakerMap_GetOptions_Diag_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(List<Pawn> selectedPawns, List<FloatMenuOption> __result)
        {
            if (selectedPawns == null || __result == null || __result.Count == 0) return;
            var puppet = selectedPawns.FirstOrDefault(PuppetPawnRegistry.IsPuppet);
            if (puppet == null) return;
            string opts = string.Join(" | ", __result.Select(o => (o.autoTakeable && !o.Disabled ? "[auto] " : "") + o.Label));
            CoopLog.Message(Loc.T("FloatMenuDiagPatch.01", puppet.LabelShortCap, puppet.Drafted, opts));
        }
    }
}
