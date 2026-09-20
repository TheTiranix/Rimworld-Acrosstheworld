using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>
    /// Dibuja un botón extra "RimCoop: Conectar" en la esquina del menú principal,
    /// sin tocar la lista interna de opciones original (más robusto ante updates del juego).
    /// </summary>
    [HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
    public static class MainMenuPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            float width = 200f;
            float height = 40f;
            var rect = new Rect(Verse.UI.screenWidth - width - 15f, 15f, width, height);

            if (Widgets.ButtonText(rect, "RimCoop: Conectar"))
            {
                Find.WindowStack.Add(new Dialog_ConnectToServer());
            }
        }
    }
}