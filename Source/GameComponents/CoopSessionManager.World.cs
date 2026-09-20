using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimCoopMod.Networking;
using RimCoopMod.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Estado "vivo" del mapa que no son construcciones ni ítems simples: pisos, nieve, plantas,
    /// suciedad, fuego, estado de los objetos (interruptores, baterías, recetas) y cadáveres.
    /// Mismo esquema de siempre: el dueño real manda, el espejo copia.
    /// </summary>
    public partial class CoopSessionManager
    {
        private bool _forceFullPlants;
        private int _baseCounter;
        private readonly Dictionary<int, Dictionary<int, Plant>> _syncedPlants = new Dictionary<int, Dictionary<int, Plant>>();
        private readonly Dictionary<string, string> _gridSigs = new Dictionary<string, string>();
        private readonly Dictionary<Thing, string> _thingStateSigs = new Dictionary<Thing, string>();

        // =====================================================================
        // Velocidad del juego
        // =====================================================================

        [ThreadStatic] public static bool ApplyingRemoteSpeed;

        public static void BroadcastSpeed(TimeSpeed speed)
        {
            if (!CoopClient.Instance.IsConnected || ApplyingRemoteSpeed || IsApplyingVotedPauseChange) return;
            CoopClient.Instance.SendSpeedChange((int)speed);
        }

        private void ReceiveSpeedChange(SpeedChangePayload p)
        {
            var tm = Find.TickManager;
            if (tm == null || tm.CurTimeSpeed == TimeSpeed.Paused) return; // pausar/despausar solo se decide por votación
            var speed = (TimeSpeed)p.Speed;
            if (speed == TimeSpeed.Paused || tm.CurTimeSpeed == speed) return;

            ApplyingRemoteSpeed = true;
            try { tm.CurTimeSpeed = speed; }
            finally { ApplyingRemoteSpeed = false; }
        }

        // =====================================================================
        // Cadáveres
        // =====================================================================

        /// <summary>
        /// El pawn real murió (aparece como cadáver en su mapa). En el espejo el títere se convierte
        /// en cadáver también — sin pasar por Kill(), que dispararía cartas y pensamientos de "murió
        /// un colono" en el juego de quien mira. Si el cadáver desaparece del lado real, se borra acá.
        /// </summary>
        private void HandleDeadPuppet(Dictionary<int, Pawn> known, PawnSnapshot ps, Map map)
        {
            if (!known.TryGetValue(ps.PawnId, out var puppet) || puppet == null) return;
            var cell = new IntVec3(ps.X, 0, ps.Z);

            try
            {
                if (puppet.Spawned)
                {
                    PuppetPawnRegistry.Unregister(puppet);
                    _puppetJobFingerprints.Remove(puppet);
                    _puppetItems.Remove(puppet);
                    _puppetCarriedKey.Remove(puppet);

                    puppet.health.SetDead();
                    var pos = cell.InBounds(map) ? cell : puppet.Position;
                    puppet.DeSpawn(DestroyMode.Vanish);
                    Corpse corpse = puppet.MakeCorpse(null, null);
                    GenSpawn.Spawn(corpse, pos, map);
                }
                else if (puppet.Corpse != null && puppet.Corpse.Spawned && puppet.Corpse.Position != cell && cell.InBounds(map))
                {
                    puppet.Corpse.Position = cell; // alguien lo cargó/movió en el mundo real
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo convertir el títere {puppet.LabelShortCap} en cadáver: {e.Message}");
            }
        }

        // =====================================================================
        // Suciedad y fuego: la "cantidad" no es stackCount
        // =====================================================================

        private static bool ApplySpecialCount(Thing thing, int count)
        {
            if (thing is Filth filth)
            {
                filth.thickness = Math.Max(1, count);
                return true;
            }
            if (thing is Fire fire)
            {
                fire.fireSize = Mathf.Clamp(count / 100f, 0.1f, 1.75f);
                return true;
            }
            if (thing is Blight blight)
            {
                Traverse.Create(blight).Field("severity").SetValue(Mathf.Clamp(count / 100f, 0.05f, 1f));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Lo que el títere haga localmente (una cosecha que suelta un ítem, una comida cocinada,
        /// suciedad nueva) no existe en el mundo real: si no es algo que el dueño mandó, se borra.
        /// Lo que sí existe allá se vuelve a crear por la foto.
        /// </summary>
        private static void ReconcileLocalItems(Map map, Dictionary<int, Thing> known)
        {
            var tracked = new HashSet<Thing>(known.Values);
            foreach (var t in map.listerThings.AllThings.ToList())
            {
                if (!t.Spawned || tracked.Contains(t)) continue;
                if (t.def.category == ThingCategory.Item || t is Filth)
                {
                    try { t.Destroy(DestroyMode.Vanish); } catch { }
                }
            }
        }

        // =====================================================================
        // Estado de los objetos: interruptor, batería, combustible, puerta fija, recetas
        // =====================================================================

        // Parte EDITABLE por cualquiera de los dos lados (puerta fija, interruptor deseado, combustible objetivo, recetas).
        private static string EditableState(Thing t)
        {
            var parts = new List<string>();
            try
            {
                if (t is Building_Door) parts.Add("hold=" + (Traverse.Create(t).Field("holdOpenInt").GetValue<bool>() ? 1 : 0));

                var flick = t.TryGetComp<CompFlickable>();
                if (flick != null) parts.Add("want=" + (Traverse.Create(flick).Field("wantSwitchOn").GetValue<bool>() ? 1 : 0));

                var fuel = t.TryGetComp<CompRefuelable>();
                if (fuel != null)
                {
                    parts.Add("ftarget=" + Inv(fuel.TargetFuelLevel));
                    parts.Add("fauto=" + (Traverse.Create(fuel).Field("allowAutoRefuel").GetValue<bool>() ? 1 : 0));
                }

                if (t is IBillGiver giver && giver.BillStack != null)
                {
                    var bills = new List<string>();
                    foreach (var bill in giver.BillStack.Bills)
                    {
                        if (bill?.recipe == null) continue;
                        var bp = bill as Bill_Production;
                        bills.Add(bill.recipe.defName + "~" + (bp?.repeatMode?.defName ?? "") + "~" + (bp?.repeatCount ?? 0) + "~" + (bp?.targetCount ?? 0) + "~" +
                                  (bill.suspended ? 1 : 0) + "~" + (bp != null && bp.paused ? 1 : 0) + "~" +
                                  Inv(bill.ingredientSearchRadius) + "~" + bill.allowedSkillRange.min + "-" + bill.allowedSkillRange.max + "~" +
                                  (bp?.GetStoreMode()?.defName ?? "") + "~" + (bp != null && bp.pauseWhenSatisfied ? 1 : 0) + "~" + (bp?.unpauseWhenYouHave ?? 0) + "~" +
                                  (bp != null && bp.includeEquipped ? 1 : 0) + "~" + (bp != null && bp.includeTainted ? 1 : 0) + "~" +
                                  Inv(bp?.hpRange.min ?? 0f) + "-" + Inv(bp?.hpRange.max ?? 1f) + "~" +
                                  (int)(bp?.qualityRange.min ?? QualityCategory.Awful) + "-" + (int)(bp?.qualityRange.max ?? QualityCategory.Legendary) + "~" +
                                  (bp != null && bp.limitToAllowedStuff ? 1 : 0) + "~" +
                                  string.Join(",", bill.ingredientFilter.AllowedThingDefs.Select(d => d.defName).OrderBy(x => x)));
                    }
                    parts.Add("bills=" + string.Join("^", bills)); // vacío también cuenta: así se propaga que se borró la última receta
                }
            }
            catch { }
            return string.Join("|", parts);
        }

        // Estado completo que manda el dueño: lo editable + lo que solo el dueño puede cambiar (interruptor real, batería, combustible actual).
        private static string BuildStateString(Thing t)
        {
            var parts = new List<string>();
            string editable = EditableState(t);
            if (editable.Length > 0) parts.Add(editable);
            try
            {
                var flick = t.TryGetComp<CompFlickable>();
                if (flick != null) parts.Add("sw=" + (flick.SwitchIsOn ? 1 : 0));

                var battery = t.TryGetComp<CompPowerBattery>();
                if (battery != null) parts.Add("bat=" + Inv(battery.StoredEnergy));

                var fuel = t.TryGetComp<CompRefuelable>();
                if (fuel != null) parts.Add("fuel=" + Inv(fuel.Fuel));
            }
            catch { }
            return string.Join("|", parts);
        }

        private class EditTrack
        {
            public int Host;
            public string Sig;
            public float CooldownUntil;
        }

        private readonly Dictionary<Thing, EditTrack> _thingEditTracks = new Dictionary<Thing, EditTrack>();

        // Lado espejo: llega el estado del dueño.
        private void ApplyThingState(Thing t, string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            if (_thingEditTracks.TryGetValue(t, out var track) && Time.realtimeSinceStartup < track.CooldownUntil) return; // el que mira acaba de editar esto: no pisarlo
            if (_thingStateSigs.TryGetValue(t, out var old) && old == state) return;
            _thingStateSigs[t] = state;

            ApplyStateParts(t, state);

            int host = GetHostPlayerIdForMap(t.Map);
            if (host >= 0) _thingEditTracks[t] = new EditTrack { Host = host, Sig = EditableState(t), CooldownUntil = track?.CooldownUntil ?? 0f };
        }

        private static void ApplyStateParts(Thing t, string state)
        {
            try
            {
                foreach (var part in state.Split('|'))
                {
                    int eq = part.IndexOf('=');
                    if (eq < 0) continue;
                    string key = part.Substring(0, eq), val = part.Substring(eq + 1);

                    switch (key)
                    {
                        case "hold":
                            if (t is Building_Door) Traverse.Create(t).Field("holdOpenInt").SetValue(val == "1");
                            break;
                        case "want":
                            {
                                var f = t.TryGetComp<CompFlickable>();
                                if (f != null)
                                {
                                    Traverse.Create(f).Field("wantSwitchOn").SetValue(val == "1");
                                    FlickUtility.UpdateFlickDesignation(t);
                                }
                                break;
                            }
                        case "sw":
                            { var f = t.TryGetComp<CompFlickable>(); if (f != null && f.SwitchIsOn != (val == "1")) f.SwitchIsOn = val == "1"; break; }
                        case "bat":
                            { var b = t.TryGetComp<CompPowerBattery>(); if (b != null) Traverse.Create(b).Field("storedEnergy").SetValue(ParseF(val)); break; }
                        case "fuel":
                            { var r = t.TryGetComp<CompRefuelable>(); if (r != null) Traverse.Create(r).Field("fuel").SetValue(ParseF(val)); break; }
                        case "ftarget":
                            { var r = t.TryGetComp<CompRefuelable>(); if (r != null) r.TargetFuelLevel = ParseF(val); break; }
                        case "fauto":
                            { var r = t.TryGetComp<CompRefuelable>(); if (r != null) Traverse.Create(r).Field("allowAutoRefuel").SetValue(val == "1"); break; }
                        case "bills":
                            ApplyBills(t, val);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo copiar el estado de {t.def.defName}: {e.Message}");
            }
        }

        // Recetas: se actualizan las que ya existen (para no perder su filtro de ingredientes, radio, etc.) y solo se crean/borran las que cambian.
        private static void ApplyBills(Thing t, string csv)
        {
            if (!(t is IBillGiver giver) || giver.BillStack == null) return;

            var existing = giver.BillStack.Bills.ToList();
            var result = new List<Bill>();

            foreach (var entry in csv.Split(new[] { '^' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split('~');
                if (f.Length < 6) continue;
                var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(f[0]);
                if (recipe == null) continue;

                try
                {
                    Bill bill = existing.FirstOrDefault(b => b.recipe == recipe);
                    if (bill != null) existing.Remove(bill);
                    else bill = recipe.MakeNewBill();

                    bill.suspended = f[4] == "1";
                    if (bill is Bill_Production bp)
                    {
                        var mode = DefDatabase<BillRepeatModeDef>.GetNamedSilentFail(f[1]);
                        if (mode != null) bp.repeatMode = mode;
                        if (int.TryParse(f[2], out int rc)) bp.repeatCount = rc;
                        if (int.TryParse(f[3], out int tc)) bp.targetCount = tc;
                        bp.paused = f[5] == "1";
                    }

                    if (f.Length >= 17)
                    {
                        bill.ingredientSearchRadius = ParseF(f[6], bill.ingredientSearchRadius);
                        var sk = f[7].Split('-');
                        if (sk.Length == 2 && int.TryParse(sk[0], out int s0) && int.TryParse(sk[1], out int s1)) bill.allowedSkillRange = new IntRange(s0, s1);

                        if (bill is Bill_Production bp2)
                        {
                            var store = DefDatabase<BillStoreModeDef>.GetNamedSilentFail(f[8]);
                            if (store != null && bp2.GetStoreMode() != store) bp2.SetStoreMode(store, null);
                            bp2.pauseWhenSatisfied = f[9] == "1";
                            if (int.TryParse(f[10], out int unp)) bp2.unpauseWhenYouHave = unp;
                            bp2.includeEquipped = f[11] == "1";
                            bp2.includeTainted = f[12] == "1";
                            var hp = f[13].Split('-');
                            if (hp.Length == 2) bp2.hpRange = new FloatRange(ParseF(hp[0]), ParseF(hp[1], 1f));
                            var ql = f[14].Split('-');
                            if (ql.Length == 2 && int.TryParse(ql[0], out int q0) && int.TryParse(ql[1], out int q1)) bp2.qualityRange = new QualityRange((QualityCategory)q0, (QualityCategory)q1);
                            bp2.limitToAllowedStuff = f[15] == "1";
                        }

                        // Filtro de ingredientes: se aplica solo si difiere (así no se pisa nada al pedo).
                        var wantedDefs = new HashSet<string>(f[16].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                        if (!wantedDefs.SetEquals(bill.ingredientFilter.AllowedThingDefs.Select(d => d.defName)))
                        {
                            bill.ingredientFilter.SetDisallowAll(null, null);
                            foreach (var dn in wantedDefs)
                            {
                                var d = DefDatabase<ThingDef>.GetNamedSilentFail(dn);
                                if (d != null) bill.ingredientFilter.SetAllow(d, true);
                            }
                        }
                    }
                    result.Add(bill);
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo copiar la receta {recipe.defName}: {e.Message}");
                }
            }

            giver.BillStack.Clear();
            foreach (var b in result) giver.BillStack.AddBill(b);
        }

        // Lado espejo: si el que mira tocó algo editable (gizmos, recetas), se le pide al dueño el mismo cambio.
        private void WatchMirrorThingEdits()
        {
            foreach (var kv in _thingEditTracks.ToList())
            {
                var thing = kv.Key;
                var track = kv.Value;
                if (thing == null || thing.Destroyed || !thing.Spawned) { _thingEditTracks.Remove(thing); continue; }

                string cur = EditableState(thing);
                if (cur == track.Sig) continue;

                track.Sig = cur;
                track.CooldownUntil = Time.realtimeSinceStartup + EditCooldownSeconds;

                int hostThingId = ResolveHostThingId(thing);
                if (hostThingId < 0) continue;
                CoopClient.Instance.SendBuildRequest(track.Host, "@thingstate:" + hostThingId, cur, 0, 0, 0);
            }
        }

        // Host: el que mira tocó un objeto de mi base.
        private bool HandleThingStateRequest(BuildRequestPayload req, Map map)
        {
            if (!req.DefName.StartsWith("@thingstate:")) return false;
            if (!int.TryParse(req.DefName.Substring("@thingstate:".Length), out int id)) return true;

            Thing t = FindThingById(map, id);
            if (t == null) return true;
            if (EditableState(t) != req.StuffDefName) ApplyStateParts(t, req.StuffDefName);
            CoopLog.Message($"[RimCoop] Jugador {req.FromPlayerId} cambió el estado de {t.def.defName}.");
            return true;
        }

        // =====================================================================
        // Pisos, nieve y plantas
        // =====================================================================

        private static Dictionary<string, StringBuilder> EncodeRuns(int w, int h, Func<int, int, string> keyAt)
        {
            var runs = new Dictionary<string, StringBuilder>();
            for (int z = 0; z < h; z++)
            {
                string cur = null;
                int start = 0;
                for (int x = 0; x <= w; x++)
                {
                    string k = x < w ? keyAt(x, z) : null;
                    if (k == cur && x < w) continue;

                    if (cur != null)
                    {
                        if (!runs.TryGetValue(cur, out var sb)) { sb = new StringBuilder(); runs[cur] = sb; }
                        if (sb.Length > 0) sb.Append(';');
                        sb.Append(z).Append(',').Append(start).Append(',').Append(x - 1);
                    }
                    cur = k;
                    start = x;
                }
            }
            return runs;
        }

        private static string[] DecodeRuns(List<RoofSnapshot> list, int w, int h)
        {
            var grid = new string[w * h];
            foreach (var r in list)
            {
                foreach (var run in r.RunsCsv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = run.Split(',');
                    if (f.Length != 3 || !int.TryParse(f[0], out int z) || !int.TryParse(f[1], out int x0) || !int.TryParse(f[2], out int x1)) continue;
                    if (z < 0 || z >= h) continue;
                    for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++) grid[z * w + x] = r.DefName;
                }
            }
            return grid;
        }

        private void FillWorldLayers(BaseSnapshotPayload payload, Map map)
        {
            int w = map.Size.x, h = map.Size.z;

            foreach (var kv in EncodeRuns(w, h, (x, z) => map.terrainGrid.TopTerrainAt(new IntVec3(x, 0, z))?.defName))
                payload.Terrains.Add(new RoofSnapshot { DefName = kv.Key, RunsCsv = kv.Value.ToString() });

            foreach (var kv in EncodeRuns(w, h, (x, z) =>
            {
                int q = Mathf.RoundToInt(map.snowGrid.GetDepth(new IntVec3(x, 0, z)) * 10f);
                return q > 0 ? q.ToString() : null;
            }))
                payload.Snow.Add(new RoofSnapshot { DefName = kv.Key, RunsCsv = kv.Value.ToString() });

            payload.ConditionsCsv = ActiveConditionsCsv(map);

            _baseCounter++;
            payload.HasPlants = _forceFullPlants || _baseCounter % 6 == 1; // ~cada 30 s (o cuando alguien recién entra)
            _forceFullPlants = false;
            if (!payload.HasPlants) return;

            var byDef = new Dictionary<string, StringBuilder>();
            foreach (Plant p in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>())
            {
                if (!p.Spawned) continue;
                if (!byDef.TryGetValue(p.def.defName, out var sb)) { sb = new StringBuilder(); byDef[p.def.defName] = sb; }
                if (sb.Length > 0) sb.Append(';');
                sb.Append(p.thingIDNumber).Append(',').Append(p.Position.x).Append(',').Append(p.Position.z).Append(',').Append(Inv(p.Growth));
            }
            foreach (var kv in byDef) payload.Plants.Add(new RoofSnapshot { DefName = kv.Key, RunsCsv = kv.Value.ToString() });
        }

        private void ApplyWorldLayers(BaseSnapshotPayload snapshot, Map map)
        {
            int w = map.Size.x, h = map.Size.z;
            string prefix = snapshot.HostPlayerId + "|";

            // ---- pisos construidos ----
            try
            {
                string sig = string.Join("#", snapshot.Terrains.Select(r => r.DefName + ":" + r.RunsCsv));
                if (!_gridSigs.TryGetValue(prefix + "terrain", out var old) || old != sig)
                {
                    _gridSigs[prefix + "terrain"] = sig;
                    var desired = DecodeRuns(snapshot.Terrains, w, h);
                    for (int z = 0; z < h; z++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            var cell = new IntVec3(x, 0, z);
                            string want = desired[z * w + x];
                            var cur = map.terrainGrid.TopTerrainAt(cell);
                            if (want == null)
                            {
                                if (cur != null) map.terrainGrid.RemoveTopLayer(cell, false);
                            }
                            else if (cur == null || cur.defName != want)
                            {
                                var def = DefDatabase<TerrainDef>.GetNamedSilentFail(want);
                                if (def != null) map.terrainGrid.SetTerrain(cell, def);
                            }
                        }
                    }
                }
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudieron copiar los pisos: {e.Message}"); }

            // ---- nieve ----
            try
            {
                string sig = string.Join("#", snapshot.Snow.Select(r => r.DefName + ":" + r.RunsCsv));
                if (!_gridSigs.TryGetValue(prefix + "snow", out var old) || old != sig)
                {
                    _gridSigs[prefix + "snow"] = sig;
                    var desired = DecodeRuns(snapshot.Snow, w, h);
                    for (int z = 0; z < h; z++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            var cell = new IntVec3(x, 0, z);
                            int want = desired[z * w + x] != null ? int.Parse(desired[z * w + x]) : 0;
                            int cur = Mathf.RoundToInt(map.snowGrid.GetDepth(cell) * 10f);
                            if (cur != want) map.snowGrid.SetDepth(cell, want / 10f);
                        }
                    }
                }
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo copiar la nieve: {e.Message}"); }

            // ---- clima: condiciones del mapa real ----
            ApplyConditionsToMirror(snapshot.ConditionsCsv, map);

            // ---- plantas y árboles ----
            if (snapshot.HasPlants) ApplyPlants(snapshot, map);
        }

        private void ApplyPlants(BaseSnapshotPayload snapshot, Map map)
        {
            try
            {
                if (!_syncedPlants.TryGetValue(snapshot.HostPlayerId, out var known))
                {
                    known = new Dictionary<int, Plant>();
                    _syncedPlants[snapshot.HostPlayerId] = known;
                }

                var desired = new Dictionary<int, KeyValuePair<ThingDef, KeyValuePair<IntVec3, float>>>();
                var byCell = new Dictionary<IntVec3, int>();
                foreach (var pg in snapshot.Plants)
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(pg.DefName);
                    if (def == null) continue;
                    foreach (var e in pg.RunsCsv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = e.Split(',');
                        if (f.Length != 4 || !int.TryParse(f[0], out int id) || !int.TryParse(f[1], out int x) || !int.TryParse(f[2], out int z)) continue;
                        var cell = new IntVec3(x, 0, z);
                        if (!cell.InBounds(map)) continue;
                        desired[id] = new KeyValuePair<ThingDef, KeyValuePair<IntVec3, float>>(def, new KeyValuePair<IntVec3, float>(cell, ParseF(f[3])));
                        byCell[cell] = id;
                    }
                }

                // Las plantas que el espejo generó por su cuenta: si coinciden con una real se "adoptan" (así los trabajos pueden apuntarles), si no se borran.
                var trackedSet = new HashSet<Thing>(known.Values);
                foreach (Plant p in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>().ToList())
                {
                    if (trackedSet.Contains(p)) continue;
                    if (byCell.TryGetValue(p.Position, out int id) && desired[id].Key == p.def && !known.ContainsKey(id)) known[id] = p;
                    else p.Destroy(DestroyMode.Vanish);
                }

                foreach (int id in known.Keys.Where(id => !desired.ContainsKey(id)).ToList())
                {
                    if (known[id] != null && !known[id].Destroyed) known[id].Destroy(DestroyMode.Vanish);
                    known.Remove(id);
                }

                foreach (var kv in desired)
                {
                    var def = kv.Value.Key;
                    var cell = kv.Value.Value.Key;
                    float growth = kv.Value.Value.Value;

                    if (known.TryGetValue(kv.Key, out var existing) && existing != null && existing.Spawned)
                    {
                        if (Mathf.Abs(existing.Growth - growth) > 0.02f) existing.Growth = growth;
                        continue;
                    }

                    try
                    {
                        var plant = (Plant)ThingMaker.MakeThing(def);
                        plant.Growth = growth;
                        GenSpawn.Spawn(plant, cell, map);
                        known[kv.Key] = plant;
                    }
                    catch (Exception e)
                    {
                        CoopLog.Warning($"[RimCoop] No se pudo crear la planta {def.defName}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudieron copiar las plantas: {e.Message}");
            }
        }
    }
}
