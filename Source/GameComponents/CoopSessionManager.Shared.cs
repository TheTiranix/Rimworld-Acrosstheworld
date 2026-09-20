using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Lo que se comparte entre TODOS los jugadores aunque cada uno tenga su propia simulación:
    /// la investigación (un árbol tecnológico común), la riqueza de cada colonia (para mostrarla)
    /// y las condiciones globales (eclipse, llamarada solar...) que pegan en todas las bases a la vez.
    /// La fecha/hora del juego sigue siendo de cada partida.
    /// </summary>
    public partial class CoopSessionManager
    {
        private const int WealthCheckIntervalTicks = 2500;   // ~40 s de juego
        private const int ResearchProgressIntervalTicks = 1800; // ~30 s de juego

        private int _lastSentWealth = -1;
        private string _lastSentResearchProgress = "";

        [ThreadStatic] public static bool ApplyingRemoteResearch;
        [ThreadStatic] public static bool ApplyingRemoteCondition;

        private void TickShared()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick % WealthCheckIntervalTicks == 0) UpdateLocalWealth(force: false);
            if (tick % ResearchProgressIntervalTicks == 0) SendResearchProgress();
        }

        // =====================================================================
        // Riqueza
        // =====================================================================

        private static int ComputeLocalWealth()
        {
            float total = 0f;
            foreach (var map in Find.Maps)
                if (map.IsPlayerHome) total += map.wealthWatcher.WealthTotal;
            return Mathf.RoundToInt(total);
        }

        /// <summary>Actualiza la riqueza que se manda junto al aviso de posición. Solo avisa si cambió >10 % (no se manda todo el tiempo).</summary>
        private void UpdateLocalWealth(bool force)
        {
            try
            {
                int wealth = ComputeLocalWealth();
                CoopClient.Instance.LocalWealth = wealth;
                if (_localTile < 0 || !CoopClient.Instance.IsConnected) return;

                bool changedALot = _lastSentWealth < 0 || Math.Abs(wealth - _lastSentWealth) > Math.Max(500, _lastSentWealth / 10);
                if (!force && changedALot)
                {
                    _lastSentWealth = wealth;
                    CoopClient.Instance.SendUpdate(_localTile, CountLocalColonists());
                }
                else if (force)
                {
                    _lastSentWealth = wealth;
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo calcular la riqueza: {e.Message}");
            }
        }

        // =====================================================================
        // Investigación compartida
        // =====================================================================

        private static string ResearchProgressString()
        {
            var rm = Find.ResearchManager;
            var parts = new List<string>();
            foreach (var kv in Traverse.Create(rm).Field("progress").GetValue<Dictionary<ResearchProjectDef, float>>())
            {
                if (kv.Value > 0f && !kv.Key.IsFinished) parts.Add(kv.Key.defName + "=" + Inv(kv.Value));
            }
            return string.Join(";", parts);
        }

        private void SendResearchProgress()
        {
            if (!CoopClient.Instance.IsConnected) return;
            try
            {
                string now = ResearchProgressString();
                if (now == _lastSentResearchProgress) return;
                _lastSentResearchProgress = now;
                CoopClient.Instance.SendResearchSync("progress", now);
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo mandar el progreso de investigación: {e.Message}"); }
        }

        /// <summary>Todo lo que ya investigué (terminado y en curso): lo recibe quien recién entra, y lo mezcla con lo suyo.</summary>
        private void SendResearchFull()
        {
            if (!CoopClient.Instance.IsConnected || Current.ProgramState != ProgramState.Playing) return;
            try
            {
                string finished = string.Join(",", DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(p => p.IsFinished).Select(p => p.defName));
                CoopClient.Instance.SendResearchSync("full", "F:" + finished + "#P:" + ResearchProgressString());
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo mandar la investigación completa: {e.Message}"); }
        }

        public static void OnLocalProjectFinished(ResearchProjectDef proj)
        {
            if (ApplyingRemoteResearch || proj == null || !CoopClient.Instance.IsConnected) return;
            CoopClient.Instance.SendResearchSync("finished", proj.defName);
        }

        private void ReceiveResearchSync(ResearchSyncPayload m)
        {
            if (Find.ResearchManager == null) return;
            var completed = new List<ResearchProjectDef>();

            ApplyingRemoteResearch = true;
            try
            {
                switch (m.Kind)
                {
                    case "finished":
                        MergeFinished(new[] { m.Data }, completed);
                        break;
                    case "progress":
                        MergeProgress(m.Data);
                        break;
                    case "full":
                        {
                            var parts = (m.Data ?? "").Split('#');
                            string fin = parts.Length > 0 && parts[0].StartsWith("F:") ? parts[0].Substring(2) : "";
                            string prog = parts.Length > 1 && parts[1].StartsWith("P:") ? parts[1].Substring(2) : "";
                            MergeFinished(fin.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), completed);
                            MergeProgress(prog);
                            break;
                        }
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error aplicando la investigación de {m.FromPlayerName}: {e.Message}");
            }
            finally
            {
                ApplyingRemoteResearch = false;
            }

            if (completed.Count == 1)
                Messages.Message($"Investigación compartida: {completed[0].LabelCap} (por {m.FromPlayerName}).", MessageTypeDefOf.PositiveEvent, false);
            else if (completed.Count > 1)
                Messages.Message($"Investigación compartida: {completed.Count} proyectos nuevos de {m.FromPlayerName}.", MessageTypeDefOf.PositiveEvent, false);
        }

        private static void MergeFinished(IEnumerable<string> defNames, List<ResearchProjectDef> completed)
        {
            foreach (var name in defNames)
            {
                var proj = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
                if (proj == null || proj.IsFinished) continue;
                try
                {
                    Find.ResearchManager.FinishProject(proj, false, null, false);
                    completed.Add(proj);
                }
                catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo terminar {name}: {e.Message}"); }
            }
        }

        // El progreso es un "pozo" común: se queda el mayor de los dos.
        private static void MergeProgress(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            var dict = Traverse.Create(Find.ResearchManager).Field("progress").GetValue<Dictionary<ResearchProjectDef, float>>();
            foreach (var entry in data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = entry.Split('=');
                if (kv.Length != 2) continue;
                var proj = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(kv[0]);
                if (proj == null || proj.IsFinished) continue;
                float remote = ParseF(kv[1]);
                dict.TryGetValue(proj, out float local);
                if (remote > local) dict[proj] = remote;
            }
        }

        // =====================================================================
        // Condiciones que pegan en todas las bases (eclipse, llamarada solar, ...)
        // =====================================================================

        private static readonly HashSet<string> SharedConditions = new HashSet<string>
        {
            "Eclipse", "SolarFlare", "ToxicFallout", "VolcanicWinter", "Aurora", "HeatWave", "ColdSnap", "Flashstorm", "NoxiousHaze", "Drought"
        };

        public static void OnLocalConditionRegistered(GameConditionManager manager, GameCondition cond)
        {
            if (ApplyingRemoteCondition || cond?.def == null || cond.Permanent) return;
            if (!CoopClient.Instance.IsConnected || !SharedConditions.Contains(cond.def.defName)) return;

            var map = Traverse.Create(manager).Field("map").GetValue<Map>();
            if (map == null || !map.IsPlayerHome || GetHostPlayerIdForMap(map) >= 0) return;

            CoopClient.Instance.SendWorldEvent(cond.def.defName, cond.TicksLeft);
        }

        private void ReceiveWorldEvent(WorldEventPayload e)
        {
            var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(e.DefName);
            if (def == null || !SharedConditions.Contains(def.defName)) return;

            bool applied = false;
            ApplyingRemoteCondition = true;
            try
            {
                foreach (var map in Find.Maps.Where(m => m.IsPlayerHome && GetHostPlayerIdForMap(m) < 0))
                {
                    if (map.gameConditionManager.ConditionIsActive(def)) continue;
                    map.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(def, Math.Max(600, e.Duration)));
                    applied = true;
                }
            }
            catch (Exception ex)
            {
                CoopLog.Warning($"[RimCoop] No se pudo aplicar la condición {e.DefName}: {ex.Message}");
            }
            finally
            {
                ApplyingRemoteCondition = false;
            }

            if (applied)
                Messages.Message($"{def.LabelCap} también afecta a tu base (originado en la de {e.FromPlayerName}).", MessageTypeDefOf.NegativeEvent, false);
        }
    }
}
