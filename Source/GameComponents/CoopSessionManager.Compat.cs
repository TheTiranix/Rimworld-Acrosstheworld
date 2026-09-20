using System;
using System.Collections.Generic;
using System.Linq;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Compatibilidad entre jugadores: si uno tiene un DLC o un mod que el otro no, hay defs que no existen del otro
    /// lado (ítems, colonos, edificios) y cosas que no aparecen. Se intercambian las listas de mods activos y se avisa
    /// con claridad qué falta, en vez de fallar en silencio.
    /// </summary>
    public partial class CoopSessionManager
    {
        private readonly HashSet<int> _modListWarned = new HashSet<int>();

        private static readonly Dictionary<string, string> KnownExpansions = new Dictionary<string, string>
        {
            { "ludeon.rimworld.royalty", "Royalty" },
            { "ludeon.rimworld.ideology", "Ideology" },
            { "ludeon.rimworld.biotech", "Biotech" },
            { "ludeon.rimworld.anomaly", "Anomaly" },
            { "ludeon.rimworld.odyssey", "Odyssey" },
        };

        private static bool IsIgnoredModId(string id) => id.Contains("rimcoop") || id == "ludeon.rimworld" || id == "brrainz.harmony";

        private static string MyModIds() => string.Join(",",
            LoadedModManager.RunningModsListForReading.Select(m => (m.PackageIdPlayerFacing ?? "").ToLowerInvariant())
                .Where(id => id.Length > 0 && !IsIgnoredModId(id)).Distinct().OrderBy(x => x));

        private void SendModListTo(int toPlayerId)
        {
            if (!CoopClient.Instance.IsConnected) return;
            try { CoopClient.Instance.SendModList(toPlayerId, MyModIds()); }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo mandar la lista de mods: {e.Message}"); }
        }

        private void ReceiveModList(ModListPayload m)
        {
            var theirs = new HashSet<string>((m.ModIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var mine = new HashSet<string>(MyModIds().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));

            var missingHere = theirs.Except(mine).ToList();   // lo tiene él y yo no
            var missingThere = mine.Except(theirs).ToList();  // lo tengo yo y él no
            if (missingHere.Count == 0 && missingThere.Count == 0) return;
            if (!_modListWarned.Add(m.FromPlayerId)) return; // se avisa una vez por jugador

            string Name(string id) => KnownExpansions.TryGetValue(id, out var n) ? n + " (DLC)" : id;
            var lines = new List<string>();
            if (missingHere.Count > 0) lines.Add($"{m.FromPlayerName} tiene y vos NO: {string.Join(", ", missingHere.Select(Name))}");
            if (missingThere.Count > 0) lines.Add($"Vos tenés y {m.FromPlayerName} NO: {string.Join(", ", missingThere.Select(Name))}");

            string text = $"Vos y {m.FromPlayerName} no tienen los mismos DLC/mods activos:\n\n" + string.Join("\n", lines) +
                          "\n\nLo que dependa de eso puede no aparecer del otro lado (colonos, ítems, edificios o efectos). Para jugar sin problemas, activen los mismos DLC y mods.";
            CoopLog.Warning("[RimCoop] " + text.Replace("\n", " "));
            Find.WindowStack.Add(new Dialog_MessageBox(text, "Entendido", null, null, null, "RimCoop: DLC/mods distintos"));
        }
    }
}
