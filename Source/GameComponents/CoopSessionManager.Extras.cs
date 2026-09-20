using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimCoopMod.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Todo lo "de segundo nivel" que se refleja entre el juego real y el espejo: necesidades,
    /// salud, pensamientos, prioridades de trabajo, horarios, áreas, reglas de almacenamiento,
    /// zonas, burbujas sociales y cartas/eventos. La idea es la misma de siempre: el DUEÑO real es
    /// la verdad, el espejo la copia, y si el que mira edita algo se le pide al dueño que lo cambie.
    /// </summary>
    public partial class CoopSessionManager
    {
        // Mientras el espejo se está actualizando desde la foto del dueño, los parches que
        // interceptan las ediciones locales (prioridades, horario, área) tienen que dejar pasar.
        [ThreadStatic] public static bool ApplyingMirrorSetting;

        private int _slowCounter;
        private readonly List<InteractionEvent> _pendingInteractions = new List<InteractionEvent>();
        private bool _relayingLetter;

        private static string Inv(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        private static float ParseF(string s, float fallback = 0f) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

        // =====================================================================
        // Eventos / cartas
        // =====================================================================

        /// <summary>Llamado por el parche de LetterStack en el juego REAL: le cuenta la carta a quien me está mirando.</summary>
        public static void ForwardLetter(Letter letter)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || instance._relayingLetter || instance._watchers.Count == 0) return;
            if (!CoopClient.Instance.IsConnected || letter?.def == null) return;

            string text = (letter as ChoiceLetter)?.Text.ToString() ?? "";
            foreach (int watcher in instance._watchers)
            {
                CoopClient.Instance.SendEventNotice(watcher, letter.def.defName, letter.Label.ToString(), text);
            }
        }

        private void ReceiveEventNotice(EventNoticePayload n)
        {
            string owner = _remoteBases.Values.FirstOrDefault(b => b.RemotePlayerId == n.FromPlayerId)?.RemotePlayerName ?? "otro jugador";
            LetterDef def = DefDatabase<LetterDef>.GetNamedSilentFail(n.LetterDefName) ?? LetterDefOf.NeutralEvent;
            _relayingLetter = true;
            try
            {
                Find.LetterStack.ReceiveLetter("[" + owner + "] " + n.Label, n.Text, def, null, 0, true);
            }
            finally
            {
                _relayingLetter = false;
            }
        }

        // =====================================================================
        // Interacciones sociales (burbujitas)
        // =====================================================================

        public static void RecordInteraction(Pawn initiator, Pawn recipient, InteractionDef def)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || instance._watchers.Count == 0 || def == null) return;
            if (PuppetPawnRegistry.IsPuppet(initiator) || initiator.Map == null || !initiator.Map.IsPlayerHome) return;

            instance._pendingInteractions.Add(new InteractionEvent
            {
                InitiatorId = initiator.thingIDNumber,
                RecipientId = recipient.thingIDNumber,
                DefName = def.defName
            });
        }

        private List<InteractionEvent> DrainInteractions()
        {
            var copy = _pendingInteractions.ToList();
            _pendingInteractions.Clear();
            return copy;
        }

        private void ApplyInteractions(MapSnapshotPayload snapshot)
        {
            if (snapshot.Interactions.Count == 0) return;
            if (!_syncedPawns.TryGetValue(snapshot.HostPlayerId, out var pawns)) return;

            foreach (var ie in snapshot.Interactions)
            {
                if (!pawns.TryGetValue(ie.InitiatorId, out var from) || !pawns.TryGetValue(ie.RecipientId, out var to)) continue;
                if (from == null || to == null || !from.Spawned || !to.Spawned) continue;
                var def = DefDatabase<InteractionDef>.GetNamedSilentFail(ie.DefName);
                if (def?.interactionMote == null) continue;
                try
                {
                    MoteMaker.MakeInteractionBubble(from, to, def.interactionMote, def.GetSymbol(from.Faction, from.Ideo), def.GetSymbolColor(from.Faction));
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo mostrar la burbuja social {ie.DefName}: {e.Message}");
                }
            }
        }

        // =====================================================================
        // Necesidades, salud, pensamientos, trabajo, horario, área (por pawn)
        // =====================================================================

        private void FillPawnExtras(Pawn pawn, PawnSnapshot s, bool slow)
        {
            var needs = pawn.needs;
            if (needs != null)
            {
                var parts = new List<string>();
                if (needs.food != null) parts.Add("food=" + Inv(needs.food.CurLevel));
                if (needs.rest != null) parts.Add("rest=" + Inv(needs.rest.CurLevel));
                if (needs.mood != null) parts.Add("mood=" + Inv(needs.mood.CurLevel));
                if (needs.joy != null) parts.Add("joy=" + Inv(needs.joy.CurLevel));
                s.NeedsCsv = string.Join(";", parts);
            }

            if (!slow) return;
            s.HasSlowData = true;

            try
            {
                s.HediffsCsv = pawn.health?.hediffSet == null ? "" : string.Join(";",
                    pawn.health.hediffSet.hediffs.Where(h => h?.def != null)
                        .Select(h => h.def.defName + "," + (h.Part?.Index ?? -1) + "," + Inv(h.Severity)));

                var memories = pawn.needs?.mood?.thoughts?.memories?.Memories;
                s.MemoriesCsv = memories == null ? "" : string.Join(";", memories.Where(m => m?.def != null).Select(m => m.def.defName + "," + m.CurStageIndex));

                s.WorkPrioCsv = "";
                if (pawn.workSettings != null && pawn.Faction == Faction.OfPlayer && pawn.RaceProps.Humanlike)
                {
                    var prios = new List<string>();
                    foreach (var wt in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                    {
                        if (pawn.WorkTypeIsDisabled(wt)) continue;
                        int pr = pawn.workSettings.GetPriority(wt);
                        if (pr > 0) prios.Add(wt.defName + "=" + pr);
                    }
                    s.WorkPrioCsv = string.Join(";", prios);
                }

                s.TimetableCsv = pawn.timetable?.times == null ? "" : string.Join(",", pawn.timetable.times.Select(t => t?.defName ?? ""));
                s.AreaLabel = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label ?? "";

                s.IsPlayerFaction = pawn.Faction == Faction.OfPlayer;
                s.SkillsCsv = pawn.skills == null ? "" : string.Join(";", pawn.skills.skills.Select(k => k.def.defName + "," + k.Level + "," + Inv(k.xpSinceLastLevel) + "," + (int)k.passion));
                s.TraitsCsv = pawn.story?.traits == null ? "" : string.Join(";", pawn.story.traits.allTraits.Select(t => t.def.defName + "," + t.Degree));

                if (pawn.training != null && pawn.RaceProps.Animal)
                {
                    var stepsNow = Traverse.Create(pawn.training).Field("steps").GetValue<DefMap<TrainableDef, int>>();
                    s.TrainingCsv = string.Join(";", DefDatabase<TrainableDef>.AllDefsListForReading.Select(td =>
                        td.defName + "," + (pawn.training.GetWanted(td) ? 1 : 0) + "," + (stepsNow != null ? stepsNow[td] : 0)));
                }
                s.MasterPawnId = pawn.playerSettings?.Master?.thingIDNumber ?? -1;
                s.BondsCsv = pawn.relations == null ? "" : string.Join(",",
                    pawn.relations.DirectRelations.Where(r => r.def == PawnRelationDefOf.Bond && r.otherPawn != null).Select(r => r.otherPawn.thingIDNumber));

                if (pawn.guest != null)
                {
                    s.GuestStatus = pawn.guest.GuestStatus == GuestStatus.Guest && pawn.guest.HostFaction == null ? "" : pawn.guest.GuestStatus.ToString();
                    s.GuestMode = pawn.guest.IsSlave
                        ? pawn.guest.slaveInteractionMode?.defName ?? ""
                        : Traverse.Create(pawn.guest).Field("interactionMode").GetValue<PrisonerInteractionModeDef>()?.defName ?? "";
                    s.GuestResistance = pawn.guest.Resistance;
                    s.GuestWill = Traverse.Create(pawn.guest).Field("will").GetValue<float>();
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error juntando datos extra de {pawn.LabelShortCap}: {e.Message}");
            }
        }

        private void ApplyPuppetExtras(Pawn puppet, PawnSnapshot ps, Map map)
        {
            try
            {
                ApplyNeeds(puppet, ps.NeedsCsv);
                if (!ps.HasSlowData) return;

                ApplyHediffs(puppet, ps.HediffsCsv);
                ApplyMemories(puppet, ps.MemoriesCsv);

                ApplyHistoryAndStatus(puppet, ps, map);

                ApplyingMirrorSetting = true;
                try
                {
                    ApplyWorkPriorities(puppet, ps.WorkPrioCsv);
                    ApplyTimetable(puppet, ps.TimetableCsv);
                    if (puppet.playerSettings != null)
                    {
                        Area area = string.IsNullOrEmpty(ps.AreaLabel) ? null : map.areaManager.GetLabeled(ps.AreaLabel);
                        if (puppet.playerSettings.AreaRestrictionInPawnCurrentMap != area)
                            puppet.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                    }
                }
                finally
                {
                    ApplyingMirrorSetting = false;
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error aplicando datos extra al títere {puppet.LabelShortCap}: {e.Message}");
            }
        }

        // Habilidades (con la experiencia ganada), rasgos, animales (entrenamiento, amo, vínculos) y prisioneros/esclavos.
        private void ApplyHistoryAndStatus(Pawn puppet, PawnSnapshot ps, Map map)
        {
            int host = GetHostPlayerIdForMap(map);
            _syncedPawns.TryGetValue(host, out var pawns);

            try
            {
                if (puppet.skills != null && !string.IsNullOrEmpty(ps.SkillsCsv))
                {
                    foreach (var entry in ps.SkillsCsv.Split(';'))
                    {
                        var f = entry.Split(',');
                        if (f.Length != 4) continue;
                        var def = DefDatabase<SkillDef>.GetNamedSilentFail(f[0]);
                        if (def == null) continue;
                        var sk = puppet.skills.GetSkill(def);
                        if (sk == null || sk.TotallyDisabled) continue;
                        if (int.TryParse(f[1], out int lvl) && sk.Level != lvl) sk.Level = lvl;
                        sk.xpSinceLastLevel = ParseF(f[2]);
                        if (int.TryParse(f[3], out int pas)) sk.passion = (Passion)pas;
                    }
                }

                if (puppet.story?.traits != null && ps.TraitsCsv != null)
                {
                    var wanted = new List<KeyValuePair<TraitDef, int>>();
                    foreach (var entry in ps.TraitsCsv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = entry.Split(',');
                        var def = DefDatabase<TraitDef>.GetNamedSilentFail(f[0]);
                        if (def != null && f.Length == 2 && int.TryParse(f[1], out int deg)) wanted.Add(new KeyValuePair<TraitDef, int>(def, deg));
                    }
                    foreach (var t in puppet.story.traits.allTraits.ToList())
                    {
                        int idx = wanted.FindIndex(w => w.Key == t.def && w.Value == t.Degree);
                        if (idx >= 0) wanted.RemoveAt(idx);
                        else puppet.story.traits.RemoveTrait(t, false);
                    }
                    foreach (var w in wanted) puppet.story.traits.GainTrait(new Trait(w.Key, w.Value, false), false);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error copiando habilidades/rasgos de {puppet.LabelShortCap}: {e.Message}");
            }

            try
            {
                // ---- animales ----
                if (puppet.training != null && !string.IsNullOrEmpty(ps.TrainingCsv))
                {
                    var stepsMap = Traverse.Create(puppet.training).Field("steps").GetValue<DefMap<TrainableDef, int>>();
                    var learnedMap = Traverse.Create(puppet.training).Field("learned").GetValue<DefMap<TrainableDef, bool>>();
                    var wantedMap = Traverse.Create(puppet.training).Field("wantedTrainables").GetValue<DefMap<TrainableDef, bool>>();
                    ApplyingMirrorSetting = true;
                    try
                    {
                        foreach (var entry in ps.TrainingCsv.Split(';'))
                        {
                            var f = entry.Split(',');
                            if (f.Length != 3) continue;
                            var td = DefDatabase<TrainableDef>.GetNamedSilentFail(f[0]);
                            if (td == null || !int.TryParse(f[2], out int steps)) continue;
                            if (wantedMap != null && wantedMap[td] != (f[1] == "1")) wantedMap[td] = f[1] == "1";
                            if (stepsMap != null && stepsMap[td] != steps) stepsMap[td] = steps;
                            if (learnedMap != null) learnedMap[td] = steps >= td.steps;
                        }
                    }
                    finally { ApplyingMirrorSetting = false; }
                }

                if (puppet.playerSettings != null && pawns != null)
                {
                    Pawn master = null;
                    if (ps.MasterPawnId >= 0) pawns.TryGetValue(ps.MasterPawnId, out master);
                    if (puppet.playerSettings.Master != master && (ps.MasterPawnId < 0 || master != null))
                    {
                        ApplyingMirrorSetting = true;
                        try { puppet.playerSettings.Master = master; }
                        finally { ApplyingMirrorSetting = false; }
                    }
                }

                if (puppet.relations != null && pawns != null && ps.BondsCsv != null)
                {
                    var wantedBonds = new HashSet<Pawn>();
                    foreach (var idStr in ps.BondsCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(idStr, out int id) && pawns.TryGetValue(id, out var other) && other != null) wantedBonds.Add(other);

                    foreach (var rel in puppet.relations.DirectRelations.Where(r => r.def == PawnRelationDefOf.Bond).ToList())
                        if (!wantedBonds.Contains(rel.otherPawn)) puppet.relations.RemoveDirectRelation(rel);
                    foreach (var other in wantedBonds)
                        if (!puppet.relations.DirectRelationExists(PawnRelationDefOf.Bond, other)) puppet.relations.AddDirectRelation(PawnRelationDefOf.Bond, other);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error copiando datos de animal de {puppet.LabelShortCap}: {e.Message}");
            }

            try
            {
                // ---- prisioneros y esclavos (y reclutados) ----
                if (ps.IsPlayerFaction && puppet.Faction != Faction.OfPlayer) puppet.SetFaction(Faction.OfPlayer, null);

                if (puppet.guest != null && ps.GuestStatus != null)
                {
                    GuestStatus wantedStatus = GuestStatus.Guest;
                    bool hasStatus = ps.GuestStatus.Length > 0 && Enum.TryParse(ps.GuestStatus, out wantedStatus);
                    bool isCaptive = hasStatus && wantedStatus != GuestStatus.Guest;

                    if (isCaptive && puppet.guest.GuestStatus != wantedStatus)
                        puppet.guest.SetGuestStatus(Faction.OfPlayer, wantedStatus);
                    else if (!hasStatus && puppet.guest.HostFaction != null)
                        puppet.guest.SetGuestStatus(null, GuestStatus.Guest);

                    if (isCaptive && !string.IsNullOrEmpty(ps.GuestMode))
                    {
                        if (wantedStatus == GuestStatus.Slave)
                        {
                            var sm = DefDatabase<SlaveInteractionModeDef>.GetNamedSilentFail(ps.GuestMode);
                            if (sm != null) puppet.guest.slaveInteractionMode = sm;
                        }
                        else
                        {
                            var pm = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(ps.GuestMode);
                            if (pm != null) Traverse.Create(puppet.guest).Field("interactionMode").SetValue(pm);
                        }
                    }
                    Traverse.Create(puppet.guest).Field("resistance").SetValue(ps.GuestResistance);
                    Traverse.Create(puppet.guest).Field("will").SetValue(ps.GuestWill);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error copiando estado de prisionero/esclavo de {puppet.LabelShortCap}: {e.Message}");
            }
        }

        private static void ApplyNeeds(Pawn puppet, string csv)
        {
            var needs = puppet.needs;
            if (needs == null || string.IsNullOrEmpty(csv)) return;
            foreach (var entry in csv.Split(';'))
            {
                var kv = entry.Split('=');
                if (kv.Length != 2) continue;
                float v = ParseF(kv[1]);
                switch (kv[0])
                {
                    case "food": if (needs.food != null) needs.food.CurLevel = v; break;
                    case "rest": if (needs.rest != null) needs.rest.CurLevel = v; break;
                    case "mood": if (needs.mood != null) needs.mood.CurLevel = v; break;
                    case "joy": if (needs.joy != null) needs.joy.CurLevel = v; break;
                }
            }
        }

        // Heridas y enfermedades: se identifican por (tipo, parte del cuerpo, n-ésima repetición).
        private static void ApplyHediffs(Pawn puppet, string csv)
        {
            if (puppet.health?.hediffSet == null || csv == null) return;

            var wanted = new Dictionary<string, float>();
            var seenCount = new Dictionary<string, int>();
            if (csv.Length > 0)
            {
                foreach (var entry in csv.Split(';'))
                {
                    var f = entry.Split(',');
                    if (f.Length != 3) continue;
                    string baseKey = f[0] + "|" + f[1];
                    seenCount.TryGetValue(baseKey, out int n);
                    seenCount[baseKey] = n + 1;
                    wanted[baseKey + "|" + n] = ParseF(f[2]);
                }
            }

            var currentCount = new Dictionary<string, int>();
            var currentByKey = new Dictionary<string, Hediff>();
            foreach (var h in puppet.health.hediffSet.hediffs.ToList())
            {
                string baseKey = h.def.defName + "|" + (h.Part?.Index ?? -1);
                currentCount.TryGetValue(baseKey, out int n);
                currentCount[baseKey] = n + 1;
                string key = baseKey + "|" + n;
                if (!wanted.ContainsKey(key)) puppet.health.RemoveHediff(h);
                else currentByKey[key] = h;
            }

            foreach (var kv in wanted)
            {
                if (currentByKey.TryGetValue(kv.Key, out var existing))
                {
                    if (Mathf.Abs(existing.Severity - kv.Value) > 0.001f) existing.Severity = kv.Value;
                    continue;
                }

                var f = kv.Key.Split('|');
                var def = DefDatabase<HediffDef>.GetNamedSilentFail(f[0]);
                if (def == null) continue;
                try
                {
                    int partIdx = int.Parse(f[1]);
                    BodyPartRecord part = partIdx >= 0 ? puppet.RaceProps.body.GetPartAtIndex(partIdx) : null;
                    Hediff h = HediffMaker.MakeHediff(def, puppet, part);
                    h.Severity = kv.Value;
                    puppet.health.AddHediff(h, part);
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo copiar el hediff {def.defName} al títere: {e.Message}");
                }
            }
        }

        private static void ApplyMemories(Pawn puppet, string csv)
        {
            var handler = puppet.needs?.mood?.thoughts?.memories;
            if (handler == null || csv == null) return;

            // Lo que dice el dueño: lista de (pensamiento, etapa exacta).
            var wanted = new List<KeyValuePair<string, int>>();
            if (csv.Length > 0)
            {
                foreach (var entry in csv.Split(';'))
                {
                    var f = entry.Split(',');
                    wanted.Add(new KeyValuePair<string, int>(f[0], f.Length > 1 && int.TryParse(f[1], out int st) ? st : 0));
                }
            }

            // Primero se empareja lo que ya hay (mismo tipo); se corrige su etapa si difiere.
            foreach (var m in handler.Memories.ToList())
            {
                int idx = wanted.FindIndex(w => w.Key == m.def.defName);
                if (idx < 0) { handler.RemoveMemory(m); continue; }
                int stage = wanted[idx].Value;
                wanted.RemoveAt(idx);
                try { if (m.CurStageIndex != stage) m.SetForcedStage(stage); } catch { }
            }

            // Lo que falta se agrega con su etapa.
            foreach (var w in wanted)
            {
                var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(w.Key);
                if (def == null) continue;
                try
                {
                    handler.TryGainMemoryFast(def, null);
                    var added = handler.Memories.LastOrDefault(m => m.def == def);
                    if (added != null && added.CurStageIndex != w.Value) added.SetForcedStage(w.Value);
                }
                catch { }
            }
        }

        private static void ApplyWorkPriorities(Pawn puppet, string csv)
        {
            if (puppet.workSettings == null || csv == null) return;

            var wanted = new Dictionary<string, int>();
            foreach (var entry in csv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = entry.Split('=');
                if (kv.Length == 2 && int.TryParse(kv[1], out int pr)) wanted[kv[0]] = pr;
            }

            foreach (var wt in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                try
                {
                    if (puppet.WorkTypeIsDisabled(wt)) continue;
                    wanted.TryGetValue(wt.defName, out int pr);
                    if (puppet.workSettings.GetPriority(wt) != pr) puppet.workSettings.SetPriority(wt, pr);
                }
                catch { }
            }
        }

        private static void ApplyTimetable(Pawn puppet, string csv)
        {
            if (puppet.timetable == null || string.IsNullOrEmpty(csv)) return;
            var names = csv.Split(',');
            for (int h = 0; h < names.Length && h < 24; h++)
            {
                var def = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(names[h]);
                if (def != null && puppet.timetable.GetAssignment(h) != def) puppet.timetable.SetAssignment(h, def);
            }
        }

        /// <summary>Host: el que mira (dueño del colono transferido) pidió cambiar una configuración de ese colono.</summary>
        private void HandlePawnSetting(PawnSettingPayload req)
        {
            var map = Find.AnyPlayerHomeMap;
            Pawn pawn = map?.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == req.PawnId);
            if (pawn == null) return;

            if (!_pawnOwners.TryGetValue(pawn.thingIDNumber, out int owner) || owner != req.FromPlayerId)
            {
                CoopClient.Instance.SendJoinResult(req.FromPlayerId, false, "Ese no es tu colono: no podés cambiarle la configuración.");
                return;
            }

            try
            {
                switch (req.Kind)
                {
                    case "workprio":
                        {
                            var wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(req.Key);
                            if (wt != null && int.TryParse(req.Value, out int pr) && pawn.workSettings != null) pawn.workSettings.SetPriority(wt, pr);
                            break;
                        }
                    case "timetable":
                        {
                            var def = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(req.Value);
                            if (def != null && int.TryParse(req.Key, out int hour) && pawn.timetable != null) pawn.timetable.SetAssignment(hour, def);
                            break;
                        }
                    case "master":
                        if (pawn.playerSettings != null)
                            pawn.playerSettings.Master = int.TryParse(req.Value, out int masterId) && masterId >= 0
                                ? map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == masterId)
                                : null;
                        break;
                    case "training":
                        {
                            var td = DefDatabase<TrainableDef>.GetNamedSilentFail(req.Key);
                            if (td != null && pawn.training != null) pawn.training.SetWantedRecursive(td, req.Value == "1");
                            break;
                        }
                    case "area":
                        if (pawn.playerSettings != null)
                            pawn.playerSettings.AreaRestrictionInPawnCurrentMap = string.IsNullOrEmpty(req.Value) ? null : map.areaManager.GetLabeled(req.Value);
                        break;
                }
                CoopLog.Message($"[RimCoop] Jugador {req.FromPlayerId} cambió {req.Kind} de {pawn.LabelShortCap}.");
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error aplicando {req.Kind} a {pawn.LabelShortCap}: {e.Message}");
            }
        }

        // =====================================================================
        // Reglas de almacenamiento y zonas
        // =====================================================================

        private static string StorageString(StorageSettings s)
        {
            var f = s.filter;
            var defs = string.Join(",", f.AllowedThingDefs.Select(d => d.defName).OrderBy(x => x));
            return (int)s.Priority + "|" + (int)f.AllowedQualityLevels.min + "-" + (int)f.AllowedQualityLevels.max + "|" +
                   Inv(f.AllowedHitPointsPercents.min) + "-" + Inv(f.AllowedHitPointsPercents.max) + "|" + defs;
        }

        private static void ApplyStorageString(StorageSettings s, string str)
        {
            if (s == null || string.IsNullOrEmpty(str)) return;
            var parts = str.Split('|');
            if (parts.Length < 4) return;

            if (int.TryParse(parts[0], out int prio)) s.Priority = (StoragePriority)prio;

            var q = parts[1].Split('-');
            if (q.Length == 2 && int.TryParse(q[0], out int q0) && int.TryParse(q[1], out int q1))
                s.filter.AllowedQualityLevels = new QualityRange((QualityCategory)q0, (QualityCategory)q1);

            var hp = parts[2].Split('-');
            if (hp.Length == 2) s.filter.AllowedHitPointsPercents = new FloatRange(ParseF(hp[0]), ParseF(hp[1], 1f));

            s.filter.SetDisallowAll(null, null);
            foreach (var name in parts[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var d = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (d != null) s.filter.SetAllow(d, true);
            }
        }

        private static string ZoneSettingsString(Zone zone)
        {
            if (zone is Zone_Stockpile sp) return StorageString(sp.settings);
            if (zone is Zone_Growing g) return "plant=" + (g.GetPlantDefToGrow()?.defName ?? "") + ";sow=" + (g.allowSow ? 1 : 0) + ";cut=" + (g.allowCut ? 1 : 0);
            return "";
        }

        private static void ApplyZoneSettings(Zone zone, string str)
        {
            if (string.IsNullOrEmpty(str)) return;
            if (zone is Zone_Stockpile sp) { ApplyStorageString(sp.settings, str); return; }
            if (zone is Zone_Growing g)
            {
                foreach (var entry in str.Split(';'))
                {
                    var kv = entry.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "plant")
                    {
                        var plant = string.IsNullOrEmpty(kv[1]) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(kv[1]);
                        if (plant != null && g.GetPlantDefToGrow() != plant) g.SetPlantDefToGrow(plant);
                    }
                    else if (kv[0] == "sow") g.allowSow = kv[1] == "1";
                    else if (kv[0] == "cut") g.allowCut = kv[1] == "1";
                }
            }
        }

        private class MirrorSettings
        {
            public Zone Zone;
            public string Kind;
            public string CellsSig;
            public string LastSettings;
            public float CooldownUntil;
        }

        private class MirrorStore
        {
            public string LastSettings;
            public float CooldownUntil;
        }

        private readonly Dictionary<int, Dictionary<int, MirrorSettings>> _mirrorZones = new Dictionary<int, Dictionary<int, MirrorSettings>>();
        private readonly Dictionary<int, Dictionary<string, MirrorStore>> _mirrorStores = new Dictionary<int, Dictionary<string, MirrorStore>>();
        private const float EditCooldownSeconds = 4f;

        // Zonas del dueño reflejadas en el mapa espejo. Se recrean solo si cambió su forma/tipo;
        // las reglas (planta, filtros, prioridad) se actualizan sin recrear, para no pisar lo que
        // el que mira esté editando justo ahora (ver WatchMirrorSettingsEdits).
        private void ApplyZones(BaseSnapshotPayload snapshot, Map map)
        {
            if (!_mirrorZones.TryGetValue(snapshot.HostPlayerId, out var mine))
            {
                mine = new Dictionary<int, MirrorSettings>();
                _mirrorZones[snapshot.HostPlayerId] = mine;
            }

            var seen = new HashSet<int>();
            foreach (var zs in snapshot.Zones)
            {
                seen.Add(zs.ZoneId);
                string cellsSig = zs.Kind + "/" + zs.CellsCsv;

                if (mine.TryGetValue(zs.ZoneId, out var m) && m.CellsSig == cellsSig && map.zoneManager.AllZones.Contains(m.Zone))
                {
                    if (zs.SettingsStr != m.LastSettings && Time.realtimeSinceStartup >= m.CooldownUntil)
                    {
                        ApplyZoneSettings(m.Zone, zs.SettingsStr);
                        m.LastSettings = ZoneSettingsString(m.Zone);
                    }
                    continue;
                }

                if (m != null) { try { if (map.zoneManager.AllZones.Contains(m.Zone)) m.Zone.Delete(false); } catch { } }

                try
                {
                    Zone zone;
                    if (zs.Kind == "growing") zone = new Zone_Growing(map.zoneManager);
                    else zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);

                    foreach (var c in ParseCells(zs.CellsCsv)) if (c.InBounds(map)) zone.AddCell(c);
                    ApplyZoneSettings(zone, zs.SettingsStr);
                    mine[zs.ZoneId] = new MirrorSettings { Zone = zone, Kind = zs.Kind, CellsSig = cellsSig, LastSettings = ZoneSettingsString(zone) };
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo reconstruir la zona {zs.Kind}: {e.Message}");
                }
            }

            foreach (int id in mine.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                try { if (map.zoneManager.AllZones.Contains(mine[id].Zone)) mine[id].Zone.Delete(false); } catch { }
                mine.Remove(id);
            }
        }

        private void FillAreasAndStores(BaseSnapshotPayload payload, Map map)
        {
            var am = map.areaManager;
            void AddArea(string kind, Area a, string label)
            {
                if (a == null) return;
                payload.Areas.Add(new AreaSnapshot { Kind = kind, Label = label, CellsCsv = CellsToCsv(a.ActiveCells) });
            }

            AddArea("home", am.Home, "");
            AddArea("buildroof", am.BuildRoof, "");
            AddArea("noroof", am.NoRoof, "");
            AddArea("snow", am.SnowOrSandClear, "");
            AddArea("pollution", am.PollutionClear, "");
            foreach (var a in am.AllAreas.OfType<Area_Allowed>()) AddArea("allowed", a, a.Label);

            FillRoofs(payload, map);

            foreach (var b in map.listerBuildings.allBuildingsColonist.OfType<Building_Storage>())
            {
                payload.Stores.Add(new StoreSnapshot { X = b.Position.x, Z = b.Position.z, SettingsStr = StorageString(b.settings) });
            }
        }

        // Techos: por cada tipo, tramos horizontales "z,x0,x1;..." (mucho más chico que celda por celda).
        private static void FillRoofs(BaseSnapshotPayload payload, Map map)
        {
            var runs = new Dictionary<RoofDef, System.Text.StringBuilder>();
            int w = map.Size.x, h = map.Size.z;
            for (int z = 0; z < h; z++)
            {
                RoofDef cur = null;
                int start = 0;
                for (int x = 0; x <= w; x++)
                {
                    RoofDef r = x < w ? map.roofGrid.RoofAt(x, z) : null;
                    if (r == cur && x < w) continue;

                    if (cur != null)
                    {
                        if (!runs.TryGetValue(cur, out var sb)) { sb = new System.Text.StringBuilder(); runs[cur] = sb; }
                        if (sb.Length > 0) sb.Append(';');
                        sb.Append(z).Append(',').Append(start).Append(',').Append(x - 1);
                    }
                    cur = r;
                    start = x;
                }
            }
            foreach (var kv in runs) payload.Roofs.Add(new RoofSnapshot { DefName = kv.Key.defName, RunsCsv = kv.Value.ToString() });
        }

        private readonly Dictionary<int, string> _roofSigs = new Dictionary<int, string>();

        private void ApplyRoofs(BaseSnapshotPayload snapshot, Map map)
        {
            string sig = string.Join("#", snapshot.Roofs.Select(r => r.DefName + ":" + r.RunsCsv));
            if (_roofSigs.TryGetValue(snapshot.HostPlayerId, out var old) && old == sig) return;
            _roofSigs[snapshot.HostPlayerId] = sig;

            try
            {
                int w = map.Size.x, h = map.Size.z;
                var desired = new RoofDef[w * h];
                foreach (var r in snapshot.Roofs)
                {
                    var def = DefDatabase<RoofDef>.GetNamedSilentFail(r.DefName);
                    if (def == null) continue;
                    foreach (var run in r.RunsCsv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = run.Split(',');
                        if (f.Length != 3 || !int.TryParse(f[0], out int z) || !int.TryParse(f[1], out int x0) || !int.TryParse(f[2], out int x1)) continue;
                        if (z < 0 || z >= h) continue;
                        for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++) desired[z * w + x] = def;
                    }
                }

                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var want = desired[z * w + x];
                        if (map.roofGrid.RoofAt(x, z) != want) map.roofGrid.SetRoof(new IntVec3(x, 0, z), want);
                    }
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudieron copiar los techos: {e.Message}");
            }
        }

        private readonly Dictionary<string, string> _areaSigs = new Dictionary<string, string>();

        private void InvalidateAreaSigs(int hostPlayerId)
        {
            string prefix = hostPlayerId + "|";
            foreach (var k in _areaSigs.Keys.Where(k => k.StartsWith(prefix)).ToList()) _areaSigs.Remove(k);
        }

        private static Area FindArea(Map map, string kind, string label, bool createIfMissing)
        {
            var am = map.areaManager;
            switch (kind)
            {
                case "home": return am.Home;
                case "buildroof": return am.BuildRoof;
                case "noroof": return am.NoRoof;
                case "snow": return am.SnowOrSandClear;
                case "pollution": return am.PollutionClear;
                case "allowed":
                    {
                        var found = am.AllAreas.OfType<Area_Allowed>().FirstOrDefault(a => a.Label == label);
                        if (found != null || !createIfMissing) return found;
                        if (am.TryMakeNewAllowed(out Area_Allowed created)) { created.SetLabel(label); return created; }
                        return null;
                    }
            }
            return null;
        }

        private void ApplyAreasAndStores(BaseSnapshotPayload snapshot, Map map)
        {
            foreach (var a in snapshot.Areas)
            {
                string key = snapshot.HostPlayerId + "|" + a.Kind + "|" + a.Label;
                if (_areaSigs.TryGetValue(key, out var old) && old == a.CellsCsv) continue;

                try
                {
                    var area = FindArea(map, a.Kind, a.Label, true);
                    if (area == null) continue;
                    area.Clear();
                    foreach (var c in ParseCells(a.CellsCsv)) if (c.InBounds(map)) area[c] = true;
                    _areaSigs[key] = a.CellsCsv;
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo copiar el área {a.Kind}/{a.Label}: {e.Message}");
                }
            }

            // Un área permitida que el dueño borró tampoco puede quedar en el espejo.
            var wantedLabels = new HashSet<string>(snapshot.Areas.Where(x => x.Kind == "allowed").Select(x => x.Label));
            foreach (var extra in map.areaManager.AllAreas.OfType<Area_Allowed>().Where(x => !wantedLabels.Contains(x.Label)).ToList())
            {
                try
                {
                    _areaSigs.Remove(snapshot.HostPlayerId + "|allowed|" + extra.Label);
                    extra.Delete();
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo borrar el área espejo {extra.Label}: {e.Message}");
                }
            }

            ApplyRoofs(snapshot, map);

            if (!_mirrorStores.TryGetValue(snapshot.HostPlayerId, out var stores))
            {
                stores = new Dictionary<string, MirrorStore>();
                _mirrorStores[snapshot.HostPlayerId] = stores;
            }
            foreach (var ss in snapshot.Stores)
            {
                var cell = new IntVec3(ss.X, 0, ss.Z);
                if (!cell.InBounds(map)) continue;
                var building = cell.GetThingList(map).OfType<Building_Storage>().FirstOrDefault();
                if (building == null) continue;

                string key = ss.X + "," + ss.Z;
                if (!stores.TryGetValue(key, out var ms))
                {
                    ms = new MirrorStore { LastSettings = null };
                    stores[key] = ms;
                }
                if (ss.SettingsStr != ms.LastSettings && Time.realtimeSinceStartup >= ms.CooldownUntil)
                {
                    ApplyStorageString(building.settings, ss.SettingsStr);
                    ms.LastSettings = StorageString(building.settings);
                }
            }
        }

        /// <summary>
        /// Lado espejo: si el que mira editó reglas de una zona/estante (planta, filtro, prioridad)
        /// se detecta comparando con lo último que dijo el dueño y se le pide el mismo cambio.
        /// </summary>
        private void WatchMirrorSettingsEdits()
        {
            if (_coopMaps.Count == 0) return;

            WatchMirrorThingEdits();

            foreach (var kv in _coopMaps.ToList())
            {
                int host = kv.Key;
                var map = kv.Value;
                if (map == null) continue;

                if (_mirrorZones.TryGetValue(host, out var zones))
                {
                    foreach (var zk in zones)
                    {
                        var m = zk.Value;
                        if (m.Zone == null || !map.zoneManager.AllZones.Contains(m.Zone)) continue;
                        string cur = ZoneSettingsString(m.Zone);
                        if (cur == m.LastSettings) continue;
                        m.LastSettings = cur;
                        m.CooldownUntil = Time.realtimeSinceStartup + EditCooldownSeconds;
                        CoopClient.Instance.SendBuildRequest(host, "@zoneset:" + zk.Key, cur, 0, 0, 0);
                    }
                }

                if (_mirrorStores.TryGetValue(host, out var stores))
                {
                    foreach (var sk in stores)
                    {
                        var xz = sk.Key.Split(',');
                        var cell = new IntVec3(int.Parse(xz[0]), 0, int.Parse(xz[1]));
                        var building = cell.InBounds(map) ? cell.GetThingList(map).OfType<Building_Storage>().FirstOrDefault() : null;
                        if (building == null || sk.Value.LastSettings == null) continue;
                        string cur = StorageString(building.settings);
                        if (cur == sk.Value.LastSettings) continue;
                        sk.Value.LastSettings = cur;
                        sk.Value.CooldownUntil = Time.realtimeSinceStartup + EditCooldownSeconds;
                        CoopClient.Instance.SendBuildRequest(host, "@storeset", cur, cell.x, cell.z, 0);
                    }
                }
            }
        }

        // ---- Edición de áreas desde el espejo (se juntan las celdas de un mismo arrastre y se mandan juntas) ----

        private class AreaEdit
        {
            public int Host;
            public string Kind;
            public bool Add;
            public string Label;
            public List<IntVec3> Cells = new List<IntVec3>();
        }

        private static readonly Dictionary<string, AreaEdit> _areaEditBuffer = new Dictionary<string, AreaEdit>();
        private static int _areaEditFrame;

        public static void QueueAreaEdit(int hostPlayerId, string kind, bool add, string label, IntVec3 cell)
        {
            string key = hostPlayerId + "|" + kind + "|" + add + "|" + label;
            if (!_areaEditBuffer.TryGetValue(key, out var e))
            {
                e = new AreaEdit { Host = hostPlayerId, Kind = kind, Add = add, Label = label ?? "" };
                _areaEditBuffer[key] = e;
            }
            e.Cells.Add(cell);
            _areaEditFrame = Time.frameCount;
        }

        private void FlushAreaEdits()
        {
            if (_areaEditBuffer.Count == 0 || Time.frameCount <= _areaEditFrame) return;

            foreach (var e in _areaEditBuffer.Values)
            {
                CoopClient.Instance.SendBuildRequest(e.Host, "@area:" + e.Kind + ":" + (e.Add ? "1" : "0") + ":" + e.Label,
                    CellsToCsv(e.Cells), 0, 0, 0);
                InvalidateAreaSigs(e.Host);
            }
            _areaEditBuffer.Clear();
        }

        /// <summary>Host: pedidos de zona/área/almacén que llegan como BuildRequest con un DefName reservado.</summary>
        private bool HandleExtraRequest(BuildRequestPayload req, Map map)
        {
            try
            {
                if (HandleThingStateRequest(req, map)) return true;

                if (req.DefName.StartsWith("@zoneset:"))
                {
                    int id = int.Parse(req.DefName.Substring("@zoneset:".Length));
                    var zone = map.zoneManager.AllZones.FirstOrDefault(z => z.ID == id);
                    if (zone != null) ApplyZoneSettings(zone, req.StuffDefName);
                    return true;
                }

                if (req.DefName == "@storeset")
                {
                    var cell = new IntVec3(req.X, 0, req.Z);
                    var b = cell.InBounds(map) ? cell.GetThingList(map).OfType<Building_Storage>().FirstOrDefault() : null;
                    if (b != null) ApplyStorageString(b.settings, req.StuffDefName);
                    return true;
                }

                if (req.DefName.StartsWith("@area:"))
                {
                    var f = req.DefName.Split(new[] { ':' }, 4); // @area : kind : add : label
                    if (f.Length < 4) return true;
                    var area = FindArea(map, f[1], f[3], f[2] == "1");
                    if (area == null) return true;
                    bool add = f[2] == "1";
                    foreach (var c in ParseCells(req.StuffDefName)) if (c.InBounds(map)) area[c] = add;
                    CoopLog.Message($"[RimCoop] Jugador {req.FromPlayerId} editó el área {f[1]} ({(add ? "agregar" : "quitar")}).");
                    return true;
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error en el pedido {req.DefName}: {e.Message}");
                return true;
            }
            return false;
        }
    }
}
