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
    /// Lo que se comparte entre jugadores aunque cada uno tenga su propia simulación:
    /// la investigación (solo entre quienes COLABORAN: se mandaron colonos entre sí), la riqueza de
    /// cada colonia (para mostrarla) y las condiciones de clima (eclipse, lluvia tóxica...) que se
    /// ven también en el mapa espejo de quien mira esa base, sin tocar su colonia principal.
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
            if (tick % QuestUpdateIntervalTicks == 0) { TickQuests(); TickShips(); }
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
                CoopLog.Warning(Loc.T("SessionManager_Shared.01", e.Message));
            }
        }

        // =====================================================================
        // Investigación compartida
        // =====================================================================

        // ---- Con quién se comparte: solo con quienes colaboran (se mandaron colonos entre sí) ----

        private HashSet<string> _collaboratorNames = new HashSet<string>();
        private HashSet<string> _pendingLeaveNames = new HashSet<string>(); // corté con alguien que estaba desconectado: se le avisa cuando vuelva
        private readonly Dictionary<int, string> _playerNames = new Dictionary<int, string>();

        public override void ExposeData()
        {
            base.ExposeData();
            var names = _collaboratorNames.ToList();
            Scribe_Collections.Look(ref names, "rimcoopCollaborators", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars) _collaboratorNames = new HashSet<string>(names ?? new List<string>());

            ExposeQuestData();
            ExposePendingInteractions();

            var owners = _pawnOwnerNames.Select(kv => kv.Key + "=" + kv.Value).ToList();
            Scribe_Collections.Look(ref owners, "rimcoopPawnOwners", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                _pawnOwnerNames = new Dictionary<int, string>();
                foreach (var s in owners ?? new List<string>())
                {
                    var f = s.Split(new[] { '=' }, 2);
                    if (f.Length == 2 && int.TryParse(f[0], out int id)) _pawnOwnerNames[id] = f[1];
                }
            }

            var pending = _pendingLeaveNames.ToList();
            Scribe_Collections.Look(ref pending, "rimcoopPendingLeaves", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars) _pendingLeaveNames = new HashSet<string>(pending ?? new List<string>());
        }

        // Quién es dueño de cada colono que me mandaron (los "de otro jugador" que viven en mi base). Se guarda por NOMBRE con la
        // partida: si no, al cargar se perdía y esos colonos pasaban a ser míos.
        private Dictionary<int, string> _pawnOwnerNames = new Dictionary<int, string>();

        private void RestorePawnOwners()
        {
            foreach (var kv in _pawnOwnerNames)
            {
                foreach (var p in _playerNames)
                    if (p.Value == kv.Value) { _pawnOwners[kv.Key] = p.Key; break; }
            }
        }

        private bool IsCollaborator(int playerId) =>
            _playerNames.TryGetValue(playerId, out var name) && _collaboratorNames.Contains(name);

        private List<int> OnlineCollaborators(int exceptPlayerId = -1) =>
            _connectedPlayerIds.Where(id => id != CoopClient.Instance.LocalPlayerId && id != exceptPlayerId && IsCollaborator(id)).ToList();

        /// <summary>¿Estoy compartiendo investigación con este jugador? (lo usa el botón de la base)</summary>
        public static bool IsCollaboratingWith(int playerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            return instance != null && instance.IsCollaborator(playerId);
        }

        /// <summary>Corta la colaboración: se deja de compartir investigación (en las dos puntas). Los colonos ya enviados no se tocan.</summary>
        public static void StopCollaborating(int playerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || !instance._playerNames.TryGetValue(playerId, out var name)) return;
            if (!instance._collaboratorNames.Remove(name)) return;

            if (instance._connectedPlayerIds.Contains(playerId)) CoopClient.Instance.SendResearchSync(playerId, "leave", "");
            else instance._pendingLeaveNames.Add(name);
            Messages.Message(Loc.T("SessionManager_Shared.02", name), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Empieza (o se confirma) una colaboración: desde ahora se comparte la investigación con ese jugador.</summary>
        private void AddCollaborator(int playerId)
        {
            if (!_playerNames.TryGetValue(playerId, out var name) || string.IsNullOrEmpty(name)) return;
            _pendingLeaveNames.Remove(name); // volvieron a colaborar: ya no hay que avisar que se cortó
            bool isNew = _collaboratorNames.Add(name);
            if (isNew) Messages.Message(Loc.T("SessionManager_Shared.03", name), MessageTypeDefOf.PositiveEvent, false);
            SendResearchFullTo(playerId);
        }

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
            var targets = OnlineCollaborators();
            if (targets.Count == 0) return;
            try
            {
                string now = ResearchProgressString();
                if (now == _lastSentResearchProgress) return;
                _lastSentResearchProgress = now;
                foreach (int id in targets) CoopClient.Instance.SendResearchSync(id, "progress", now);
            }
            catch (Exception e) { CoopLog.Warning(Loc.T("SessionManager_Shared.04", e.Message)); }
        }

        /// <summary>Al conectarme: mi investigación completa a cada colaborador que esté conectado.</summary>
        private void SendResearchFull()
        {
            foreach (int id in OnlineCollaborators()) SendResearchFullTo(id);
        }

        private void SendResearchFullTo(int playerId)
        {
            if (!CoopClient.Instance.IsConnected || Current.ProgramState != ProgramState.Playing || !IsCollaborator(playerId)) return;
            try
            {
                string finished = string.Join(",", DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(p => p.IsFinished).Select(p => p.defName));
                CoopClient.Instance.SendResearchSync(playerId, "full", "F:" + finished + "#P:" + ResearchProgressString());
            }
            catch (Exception e) { CoopLog.Warning(Loc.T("SessionManager_Shared.05", e.Message)); }
        }

        public static void OnLocalProjectFinished(ResearchProjectDef proj)
        {
            if (ApplyingRemoteResearch || proj == null || !CoopClient.Instance.IsConnected) return;
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;
            foreach (int id in instance.OnlineCollaborators()) CoopClient.Instance.SendResearchSync(id, "finished", proj.defName);
        }

        private void ReceiveResearchSync(ResearchSyncPayload m)
        {
            if (Find.ResearchManager == null) return;
            if (!IsCollaborator(m.FromPlayerId)) return; // solo se acepta de quienes colaboran conmigo

            if (m.Kind == "leave")
            {
                if (_playerNames.TryGetValue(m.FromPlayerId, out var leaver) && _collaboratorNames.Remove(leaver))
                    Messages.Message(Loc.T("SessionManager_Shared.06", leaver), MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            var completed = new List<ResearchProjectDef>();
            string progressChanged = "";

            ApplyingRemoteResearch = true;
            try
            {
                switch (m.Kind)
                {
                    case "finished":
                        MergeFinished(new[] { m.Data }, completed);
                        break;
                    case "progress":
                        progressChanged = MergeProgress(m.Data);
                        break;
                    case "full":
                        {
                            var parts = (m.Data ?? "").Split('#');
                            string fin = parts.Length > 0 && parts[0].StartsWith("F:") ? parts[0].Substring(2) : "";
                            string prog = parts.Length > 1 && parts[1].StartsWith("P:") ? parts[1].Substring(2) : "";
                            MergeFinished(fin.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), completed);
                            progressChanged = MergeProgress(prog);
                            break;
                        }
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("SessionManager_Shared.07", m.FromPlayerName, e.Message));
            }
            finally
            {
                ApplyingRemoteResearch = false;
            }

            if (completed.Count == 1)
                Messages.Message(Loc.T("SessionManager_Shared.08", completed[0].LabelCap, m.FromPlayerName), MessageTypeDefOf.PositiveEvent, false);
            else if (completed.Count > 1)
                Messages.Message(Loc.T("SessionManager_Shared.09", completed.Count, m.FromPlayerName), MessageTypeDefOf.PositiveEvent, false);

            // Si aprendí algo nuevo, se lo paso a MIS otros colaboradores (así un grupo de 3 o más queda parejo).
            // Solo se reenvía lo que cambió acá, así que no da vueltas para siempre.
            foreach (int id in OnlineCollaborators(exceptPlayerId: m.FromPlayerId))
            {
                foreach (var proj in completed) CoopClient.Instance.SendResearchSync(id, "finished", proj.defName);
                if (progressChanged.Length > 0) CoopClient.Instance.SendResearchSync(id, "progress", progressChanged);
            }
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
                catch (Exception e) { CoopLog.Warning(Loc.T("SessionManager_Shared.10", name, e.Message)); }
            }
        }

        // El progreso es un "pozo" común: se queda el mayor de los dos.
        private static string MergeProgress(string data)
        {
            var changed = new List<string>();
            if (string.IsNullOrEmpty(data)) return "";
            var dict = Traverse.Create(Find.ResearchManager).Field("progress").GetValue<Dictionary<ResearchProjectDef, float>>();
            foreach (var entry in data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = entry.Split('=');
                if (kv.Length != 2) continue;
                var proj = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(kv[0]);
                if (proj == null || proj.IsFinished) continue;
                float remote = ParseF(kv[1]);
                dict.TryGetValue(proj, out float local);
                if (remote > local) { dict[proj] = remote; changed.Add(proj.defName + "=" + Inv(remote)); }
            }
            return string.Join(";", changed);
        }

        // =====================================================================
        // Condiciones que pegan en todas las bases (eclipse, llamarada solar, ...)
        // =====================================================================

        // Un evento de clima/condición que le pasa a MI base también se ve en el mapa espejo de quien la está mirando
        // (así no hay desincronización), pero NO le pega a la colonia principal de ese jugador.
        public static void OnLocalConditionRegistered(GameConditionManager manager, GameCondition cond)
        {
            if (ApplyingRemoteCondition || cond?.def == null || cond.Permanent) return;
            if (!CoopClient.Instance.IsConnected) return;

            var map = Traverse.Create(manager).Field("map").GetValue<Map>();
            if (map == null || !map.IsPlayerHome || GetHostPlayerIdForMap(map) >= 0) return;

            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;
            foreach (int watcher in instance._watchers)
                CoopClient.Instance.SendWorldEvent(watcher, cond.def.defName, cond.TicksLeft);
        }

        private void ReceiveWorldEvent(WorldEventPayload e)
        {
            var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(e.DefName);
            if (def == null) return;
            if (!_coopMaps.TryGetValue(e.FromPlayerId, out var mirror) || mirror == null) return; // no estoy mirando esa base

            ApplyingRemoteCondition = true;
            try
            {
                if (!mirror.gameConditionManager.ConditionIsActive(def))
                {
                    mirror.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(def, Math.Max(600, e.Duration)));
                    Messages.Message(Loc.T("SessionManager_Shared.11", e.FromPlayerName, def.LabelCap), MessageTypeDefOf.NeutralEvent, false);
                }
            }
            catch (Exception ex)
            {
                CoopLog.Warning(Loc.T("SessionManager_Shared.12", e.DefName, ex.Message));
            }
            finally
            {
                ApplyingRemoteCondition = false;
            }
        }

        private static string ActiveConditionsCsv(Map map) =>
            string.Join(";", map.gameConditionManager.ActiveConditions.Where(c => c?.def != null && !c.Permanent).Select(c => c.def.defName + "|" + c.TicksLeft));

        // Deja las condiciones del mapa espejo iguales a las del real (también cuando terminan, o si el espejo se abrió mientras ya estaban).
        private static void ApplyConditionsToMirror(string csv, Map mirror)
        {
            if (csv == null) return;
            var wanted = new Dictionary<string, int>();
            foreach (var entry in csv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split('|');
                if (f.Length == 2 && int.TryParse(f[1], out int left)) wanted[f[0]] = left;
            }

            ApplyingRemoteCondition = true;
            try
            {
                foreach (var cond in mirror.gameConditionManager.ActiveConditions.ToList())
                    if (cond?.def != null && !cond.Permanent && !wanted.ContainsKey(cond.def.defName)) cond.End();

                foreach (var kv in wanted)
                {
                    var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(kv.Key);
                    if (def != null && !mirror.gameConditionManager.ConditionIsActive(def))
                        mirror.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(def, Math.Max(600, kv.Value)));
                }
            }
            catch (Exception e) { CoopLog.Warning(Loc.T("SessionManager_Shared.13", e.Message)); }
            finally { ApplyingRemoteCondition = false; }
        }
    }
}
