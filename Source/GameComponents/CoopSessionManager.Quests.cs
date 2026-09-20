using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimCoopMod.UI;
using RimCoopMod.World;
using RimWorld;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Misiones compartidas. El dueño de una misión (quien la aceptó) la puede compartir con jugadores con los que colabora.
    /// - Los que se suman ven la misma misión en su pestaña de misiones (nombre, descripción, estado) y se mantiene al día
    ///   mientras dure. Para cumplirla ayudan con sus colonos en la base del dueño (Colaborar / entrar a la base).
    /// - La amenaza de la misión (incursiones, generadores de amenazas) sube un 35 % por cada jugador que se suma.
    /// El estado interno de una misión (objetivos, partes, señales) es del dueño: los demás ven una copia informativa.
    /// </summary>
    public partial class CoopSessionManager
    {
        public const float QuestDifficultyPerPlayer = 0.35f;
        private const int QuestUpdateIntervalTicks = 300; // ~5 s de juego

        // Dueño: misión (id local) -> nombres de los jugadores sumados. Se guarda con la partida.
        private Dictionary<int, HashSet<string>> _sharedQuestParticipants = new Dictionary<int, HashSet<string>>();
        private readonly Dictionary<int, string> _sharedQuestLastSig = new Dictionary<int, string>();

        // Participante: "idDelDueño:idMisión" -> id de la misión copia en mi juego. Se guarda con la partida.
        private Dictionary<string, int> _mirroredQuestIds = new Dictionary<string, int>();

        private void ExposeQuestData()
        {
            var owned = _sharedQuestParticipants.Select(kv => kv.Key + "=" + string.Join("|", kv.Value)).ToList();
            Scribe_Collections.Look(ref owned, "rimcoopSharedQuests", LookMode.Value);
            var mirrored = _mirroredQuestIds.Select(kv => kv.Key + "=" + kv.Value).ToList();
            Scribe_Collections.Look(ref mirrored, "rimcoopMirroredQuests", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                _sharedQuestParticipants = new Dictionary<int, HashSet<string>>();
                foreach (var s in owned ?? new List<string>())
                {
                    var f = s.Split(new[] { '=' }, 2);
                    if (f.Length == 2 && int.TryParse(f[0], out int id))
                        _sharedQuestParticipants[id] = new HashSet<string>(f[1].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
                }
                _mirroredQuestIds = new Dictionary<string, int>();
                foreach (var s in mirrored ?? new List<string>())
                {
                    var f = s.Split(new[] { '=' }, 2);
                    if (f.Length == 2 && int.TryParse(f[1], out int id)) _mirroredQuestIds[f[0]] = id;
                }
            }
        }

        public static float QuestMultiplier(int participants) => 1f + QuestDifficultyPerPlayer * Math.Max(0, participants);

        private int ParticipantCount(int questId) => _sharedQuestParticipants.TryGetValue(questId, out var set) ? set.Count : 0;

        public static bool IsQuestShared(Quest quest, out float multiplier)
        {
            multiplier = 1f;
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (quest == null || instance == null || !instance._sharedQuestParticipants.TryGetValue(quest.id, out var set) || set.Count == 0) return false;
            multiplier = QuestMultiplier(set.Count);
            return true;
        }

        // =====================================================================
        // Dueño: compartir, aceptar, actualizar
        // =====================================================================

        /// <summary>Misiones que puedo compartir: las que acepté, siguen en curso y no son copia de otra persona.</summary>
        public static List<Quest> ShareableQuests()
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            var mirroredIds = new HashSet<int>(instance?._mirroredQuestIds.Values ?? Enumerable.Empty<int>());
            return Find.QuestManager.QuestsListForReading
                .Where(q => q.State == QuestState.Ongoing && !q.hidden && !q.dismissed && !mirroredIds.Contains(q.id))
                .ToList();
        }

        public static void ShareQuestWith(int playerId, Quest quest)
        {
            if (!CoopClient.Instance.IsConnected || quest == null) return;
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;
            CoopClient.Instance.SendQuestMessage(playerId, "invite", quest.id, quest.name, quest.description.ToString(), (int)quest.State,
                quest.challengeRating, instance.ParticipantCount(quest.id));
            Messages.Message($"Invitación a la misión \"{quest.name}\" enviada.", MessageTypeDefOf.NeutralEvent, false);
        }

        private readonly Dictionary<int, string> _sharedQuestLastXml = new Dictionary<int, string>();

        private void TickQuests()
        {
            if (!CoopClient.Instance.IsConnected || _sharedQuestParticipants.Count == 0) return;

            foreach (int questId in _sharedQuestParticipants.Keys.ToList())
            {
                var quest = Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == questId);
                var participants = _sharedQuestParticipants[questId];
                var ids = _connectedPlayerIds.Where(id => _playerNames.TryGetValue(id, out var n) && participants.Contains(n)).ToList();

                bool ended = quest == null || quest.State != QuestState.Ongoing;
                string xml = quest == null ? null : QuestTransfer.Serialize(quest);

                if (!ended && (xml == null || (_sharedQuestLastXml.TryGetValue(questId, out var old) && old == xml))) continue;
                _sharedQuestLastXml[questId] = xml;

                // La misión ENTERA (objetivos, partes, estado) viaja tal cual; cada jugador la ve idéntica a la del dueño.
                foreach (int id in ids)
                    CoopClient.Instance.SendQuestMessage(id, ended ? "end" : "update", questId, quest?.name ?? "", xml ?? "",
                        (int)(quest?.State ?? QuestState.EndedUnknownOutcome), quest?.challengeRating ?? 0, participants.Count, Find.TickManager.TicksGame);

                if (ended)
                {
                    _sharedQuestParticipants.Remove(questId);
                    _sharedQuestLastXml.Remove(questId);
                }
            }
        }

        // =====================================================================
        // Mensajes
        // =====================================================================

        private void HandleQuestMessage(QuestMessagePayload m)
        {
            switch (m.Kind)
            {
                case "invite":
                    if (!IsCollaborator(m.FromPlayerId)) return; // solo de quienes colaboran conmigo
                    Find.WindowStack.Add(new Dialog_QuestInvite(m));
                    break;

                case "accept":
                    {
                        if (!_playerNames.TryGetValue(m.FromPlayerId, out var joiner)) return;
                        if (!_sharedQuestParticipants.TryGetValue(m.QuestId, out var set))
                        {
                            set = new HashSet<string>();
                            _sharedQuestParticipants[m.QuestId] = set;
                        }
                        if (set.Add(joiner))
                            Messages.Message($"{joiner} se sumó a la misión: su dificultad ahora es ×{QuestMultiplier(set.Count):0.##}.", MessageTypeDefOf.NeutralEvent, false);
                        _sharedQuestLastXml.Remove(m.QuestId); // fuerza el próximo envío: el que se suma recibe la misión entera
                        break;
                    }

                case "decline":
                    Messages.Message($"{m.FromPlayerName} rechazó la misión compartida.", MessageTypeDefOf.RejectInput, false);
                    break;

                case "update":
                case "end":
                    ApplyMirroredQuest(m);
                    break;

                // Recompensas: lo que el dueño recibe al cumplir la misión, los que se sumaron lo reciben también.
                case "reward":
                    ReceiveItems(m.Description, m.FromPlayerName ?? "la misión compartida");
                    break;

                case "favor":
                    GiveSharedQuestFavor(m.Name, m.State);
                    break;
            }
        }

        /// <summary>Participante: acepta la invitación. La misión entera llega enseguida desde el dueño.</summary>
        public static void AcceptQuestInvite(QuestMessagePayload invite)
        {
            CoopClient.Instance.SendQuestMessage(invite.FromPlayerId, "accept", invite.QuestId, invite.Name, "", (int)QuestState.Ongoing, invite.Rating, 0);
            Messages.Message($"Te sumaste a la misión. Su dificultad ahora es ×{QuestMultiplier(invite.Participants + 1):0.##}.", MessageTypeDefOf.NeutralEvent, false);
        }

        // Copias de misiones de otros jugadores: no corren su propia lógica (la corre el dueño), solo se muestran.
        private static readonly HashSet<Quest> _transientInert = new HashSet<Quest>();

        public static bool IsMirroredQuest(Quest quest)
        {
            if (quest == null) return false;
            if (_transientInert.Contains(quest)) return true;
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            return instance != null && instance._mirroredQuestIds.ContainsValue(quest.id);
        }

        private void ApplyMirroredQuest(QuestMessagePayload m)
        {
            try
            {
                string key = m.FromPlayerId + ":" + m.QuestId;
                Quest fresh = QuestTransfer.Deserialize(m.Description);
                if (fresh == null) { CoopLog.Warning($"[RimCoop] No se pudo reconstruir la misión compartida {m.Name}."); return; }

                _transientInert.Add(fresh); // desde ya no reacciona a nada: el dueño es el único que la "juega"
                QuestTransfer.ShiftAbsoluteTicks(fresh, Find.TickManager.TicksGame - m.OwnerTicks); // los relojes de cada juego son independientes

                Quest local = null;
                if (_mirroredQuestIds.TryGetValue(key, out int localId))
                    local = Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == localId);

                if (local == null)
                {
                    fresh.id = Find.UniqueIDsManager.GetNextQuestID();
                    _mirroredQuestIds[key] = fresh.id;
                    Find.QuestManager.Add(fresh);
                }
                else
                {
                    QuestTransfer.CopyInto(local, fresh); // se actualiza la misma entrada de la pestaña, sin duplicarla
                    QuestTransfer.Detach(fresh);
                }
                _transientInert.Remove(fresh);
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo actualizar la misión compartida {m.Name}: {e.Message}");
            }
        }

        // =====================================================================
        // Recompensas: las que el dueño recibe llegan también a los que se sumaron
        // =====================================================================

        private List<int> OnlineParticipantIds(Quest quest)
        {
            if (quest == null || !_sharedQuestParticipants.TryGetValue(quest.id, out var names)) return new List<int>();
            return _connectedPlayerIds.Where(id => _playerNames.TryGetValue(id, out var n) && names.Contains(n)).ToList();
        }

        public static void ShareQuestRewardItems(QuestPart_DropPods part, Signal signal)
        {
            try
            {
                var instance = Current.Game?.GetComponent<CoopSessionManager>();
                if (instance == null || signal.tag != Traverse.Create(part).Field("inSignal").GetValue<string>()) return;
                if (!Traverse.Create(part).Field("joinPlayer").GetValue<bool>() || !IsQuestShared(part.quest, out _)) return;

                var items = Traverse.Create(part).Field("items").GetValue<List<Thing>>();
                var lines = (items ?? new List<Thing>()).Where(t => t != null && !t.Destroyed).Select(t =>
                    t.def.defName + "," + (t.Stuff?.defName ?? "") + "," + t.stackCount + "," + QualityOf(t) + "," + t.HitPoints).ToList();
                if (lines.Count == 0) return;

                foreach (int id in instance.OnlineParticipantIds(part.quest))
                    CoopClient.Instance.SendQuestMessage(id, "reward", part.quest.id, "", string.Join(";", lines), 0, 0, 0);
                CoopLog.Message($"[RimCoop] Recompensa de la misión compartida \"{part.quest.name}\" enviada a los jugadores sumados.");
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo compartir la recompensa: {e.Message}"); }
        }

        public static void ShareQuestRoyalFavor(QuestPart_GiveRoyalFavor part, Signal signal)
        {
            try
            {
                var instance = Current.Game?.GetComponent<CoopSessionManager>();
                var faction = Traverse.Create(part).Field("faction").GetValue<Faction>();
                if (instance == null || faction == null || signal.tag != Traverse.Create(part).Field("inSignal").GetValue<string>() || !IsQuestShared(part.quest, out _)) return;
                int amount = Traverse.Create(part).Field("amount").GetValue<int>();
                foreach (int id in instance.OnlineParticipantIds(part.quest))
                    CoopClient.Instance.SendQuestMessage(id, "favor", part.quest.id, faction.def.defName, "", amount, 0, 0);
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo compartir el favor real: {e.Message}"); }
        }

        private static void GiveSharedQuestFavor(string factionDefName, int amount)
        {
            try
            {
                var faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.def.defName == factionDefName);
                if (faction == null || amount <= 0) return;
                var pawn = Find.Maps.Where(mp => mp.IsPlayerHome).SelectMany(mp => mp.mapPawns.FreeColonists)
                    .FirstOrDefault(p => p.royalty != null && p.royalty.HasAnyTitleIn(faction));
                if (pawn == null) return;
                pawn.royalty.GainFavor(faction, amount);
                Messages.Message($"{pawn.LabelShortCap} recibió {amount} de favor de {faction.Name} por la misión compartida.", MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo dar el favor real: {e.Message}"); }
        }

        // =====================================================================
        // Dificultad: se aplica del lado del dueño, cuando la misión dispara su amenaza
        // =====================================================================

        private static readonly Dictionary<object, float> _questBaseThreat = new Dictionary<object, float>();

        public static void ScaleQuestIncident(QuestPart_Incident part)
        {
            try
            {
                var parms = Traverse.Create(part).Field("incidentParms").GetValue<IncidentParms>();
                if (parms == null || !IsQuestShared(part.quest, out float mult)) return;
                if (!_questBaseThreat.TryGetValue(part, out float basePoints)) { basePoints = parms.points; _questBaseThreat[part] = basePoints; }
                parms.points = basePoints * mult;
                CoopLog.Message($"[RimCoop] Misión compartida \"{part.quest.name}\": amenaza ×{mult:0.##} ({basePoints:F0} → {parms.points:F0} puntos).");
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo escalar la amenaza de la misión: {e.Message}"); }
        }

        public static void ScaleQuestThreats(QuestPart_ThreatsGenerator part)
        {
            try
            {
                if (part == null || !IsQuestShared(part.quest, out float mult)) return;
                if (!_questBaseThreat.TryGetValue(part, out float baseFactor)) { baseFactor = part.parms.currentThreatPointsFactor; _questBaseThreat[part] = baseFactor; }
                part.parms.currentThreatPointsFactor = baseFactor * mult;
                CoopLog.Message($"[RimCoop] Misión compartida \"{part.quest.name}\": generador de amenazas ×{mult:0.##}.");
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo escalar el generador de amenazas: {e.Message}"); }
        }
    }
}
