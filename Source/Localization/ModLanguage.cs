using System;
using RimWorld;
using RimWorld.Planet;
using RimCoopMod.Networking;
using UnityEngine;
using Verse;

namespace RimCoopMod
{
    /// <summary>
    /// Idioma del mod dentro del juego: la opción guardada ("auto" / "en" / "es"), su aplicación sobre Loc y los textos que viven
    /// en defs (etiquetas del mundo y de la tecla del chat), que se reescriben al cambiar de idioma.
    /// </summary>
    public static class ModLanguage
    {
        public const string Auto = "auto";

        /// <summary>El idioma que se usa de verdad ("en" o "es") para una opción dada.</summary>
        public static string Resolve(string setting)
        {
            if (setting == Loc.English || setting == Loc.Spanish) return setting;
            try
            {
                string folder = LanguageDatabase.activeLanguage?.folderName;
                if (string.IsNullOrEmpty(folder)) folder = Prefs.LangFolderName;
                if (!string.IsNullOrEmpty(folder)) return Loc.Normalize(folder);
            }
            catch { }
            return Loc.SystemLanguage();
        }

        public static string CurrentSetting => global::RimCoopMod.RimCoopMod.Instance?.Settings?.Language ?? Auto;

        /// <summary>Aplica la opción guardada (al arrancar y al cambiarla).</summary>
        public static void Apply()
        {
            Loc.SetLanguage(Resolve(CurrentSetting));
        }

        public static void Set(string setting)
        {
            var mod = global::RimCoopMod.RimCoopMod.Instance;
            if (mod?.Settings == null) return;
            mod.Settings.Language = setting;
            mod.WriteSettings();
            Apply();
            RefreshDefs();
        }

        public static string SettingLabel(string setting)
        {
            switch (setting)
            {
                case Loc.English: return "English";
                case Loc.Spanish: return "Español";
                default: return Loc.T("Language.Auto");
            }
        }

        /// <summary>Botón chico para alternar entre inglés y español sin abrir las opciones del mod.</summary>
        public static void DrawToggle(Rect rect)
        {
            string other = Loc.IsSpanish ? Loc.English : Loc.Spanish;
            if (Widgets.ButtonText(rect, Loc.T("Language.Button", SettingLabel(Loc.Language))))
                Set(other);
            TooltipHandler.TipRegion(rect, Loc.T("Language.Tooltip", SettingLabel(other)));
        }

        /// <summary>Reescribe las etiquetas de los defs propios en el idioma actual (los XML vienen en inglés).</summary>
        public static void RefreshDefs()
        {
            try
            {
                var world = DefDatabase<WorldObjectDef>.GetNamedSilentFail("RimCoop_CoopPlayerBase");
                if (world != null) { world.label = Loc.T("Def.PlayerBase"); ResetCaches(world); }

                var key = DefDatabase<KeyBindingDef>.GetNamedSilentFail("RimCoop_ToggleChat");
                if (key != null)
                {
                    key.label = Loc.T("Def.ToggleChat.Label");
                    key.description = Loc.T("Def.ToggleChat.Desc");
                    ResetCaches(key);
                }
            }
            catch (Exception e) { CoopLog.Warning(Loc.T("Language.DefsError", e.Message)); }
        }

        private static void ResetCaches(Def def)
        {
            try
            {
                var f = typeof(Def).GetField("cachedLabelCap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f != null) f.SetValue(def, f.FieldType == typeof(string) ? "" : Activator.CreateInstance(f.FieldType));
            }
            catch { }
        }

        public static void Install()
        {
            Apply();
            // Los defs no existen todavía cuando se construye el Mod: se reescriben cuando el juego termina de cargarlos.
            LongEventHandler.ExecuteWhenFinished(RefreshDefs);
            Loc.LanguageChanged += RefreshDefs;
        }
    }
}
