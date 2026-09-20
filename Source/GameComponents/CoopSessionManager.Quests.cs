using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimCoopMod.UI;
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

        private void TickQuests()
        {
            if (!CoopClient.Instance.IsConnected || _sharedQuestParticipants.Count == 0) return;

            foreach (int questId in _sharedQuestParticipants.Keys.ToList())
            {
                var quest = Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == questId);
                var participants = _sharedQuestParticipants[questId];
                var ids = _connectedPlayerIds.Where(id => _playerNames.TryGetValue(id, out var n) && participants.Contains(n)).ToList();

                if (quest == null || quest.State != QuestState.Ongoing)
                {
                    foreach (int id in ids)
                        CoopClient.Instance.SendQuestMessage(id, "end", questId, quest?.name ?? "", "", (int)(quest?.State ?? QuestState.EndedUnknownOutcome), 0, participants.Count);
                    _sharedQuestParticipants.Remove(questId);
                    _sharedQuestLastSig.Remove(questId);
                    continue;
                }

                string sig = quest.name + "|" + quest.description + "|" + participants.Count;
                if (_sharedQuestLastSig.TryGetValue(questId, out var old) && old == sig) continue;
                _sharedQuestLastSig[questId] = sig;
                foreach (int id in ids)
                    CoopClient.Instance.SendQuestMessage(id, "update", questId, quest.name, quest.description.ToString(), (int)quest.State, quest.challengeRating, participants.Count);
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
                        _sharedQuestLastSig.Remove(m.QuestId); // fuerza el próximo update a todos (cambió la cantidad de jugadores)
                        break;
                    }

                case "decline":
                    Messages.Message($"{m.FromPlayerName} rechazó la misión compartida.", MessageTypeDefOf.RejectInput, false);
                    break;

                case "update":
                case "end":
                    ApplyMirroredQuest(m);
                    break;
            }
        }

        /// <summary>Participante: acepta la invitación y crea la copia informativa en su pestaña de misiones.</summary>
        public static void AcceptQuestInvite(QuestMessagePayload invite)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;
            instance.ApplyMirroredQuest(new QuestMessagePayload
            {
                FromPlayerId = invite.FromPlayerId, FromPlayerName = invite.FromPlayerName, Kind = "update", QuestId = invite.QuestId,
                Name = invite.Name, Description = invite.Description, State = (int)QuestState.Ongoing, Rating = invite.Rating,
                Participants = invite.Participants + 1
            });
            CoopClient.Instance.SendQuestMessage(invite.FromPlayerId, "accept", invite.QuestId, invite.Name, "", (int)QuestState.Ongoing, invite.Rating, 0);
        }

        private void ApplyMirroredQuest(QuestMessagePayload m)
        {
            try
            {
                string key = m.FromPlayerId + ":" + m.QuestId;
                Quest quest = null;
                if (_mirroredQuestIds.TryGetValue(key, out int localId))
                    quest = Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == localId);

                string header = $"[Misión compartida de {m.FromPlayerName} — dificultad ×{QuestMultiplier(m.Participants):0.##} ({m.Participants} jugador(es) sumado(s))]\n\n";

                if (quest == null)
                {
                    if (m.Kind == "end") return; // ya no la tengo
                    quest = Quest.MakeRaw();
                    quest.id = Find.UniqueIDsManager.GetNextQuestID();
                    quest.root = DefDatabase<QuestScriptDef>.AllDefsListForReading.FirstOrDefault();
                    quest.appearanceTick = Find.TickManager.TicksGame;
                    quest.acceptanceExpireTick = -1;
                    Find.QuestManager.Add(quest);
                    quest.SetInitiallyAccepted();
                    _mirroredQuestIds[key] = quest.id;
                }

                quest.name = "[Compartida] " + m.Name;
                quest.description = header + m.Description;
                quest.challengeRating = m.Rating;

                if (m.Kind == "end" && quest.State == QuestState.Ongoing)
                {
                    var outcome = (QuestState)m.State == QuestState.EndedSuccess ? QuestEndOutcome.Success
                                : (QuestState)m.State == QuestState.EndedFailed ? QuestEndOutcome.Fail
                                : QuestEndOutcome.Unknown;
                    quest.End(outcome, true, true);
                    _mirroredQuestIds.Remove(key);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo actualizar la misión compartida {m.Name}: {e.Message}");
            }
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
