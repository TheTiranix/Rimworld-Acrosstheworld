using System;
using System.Collections.Generic;
using System.Linq;
using RimCoopMod.Networking;
using RimCoopMod.UI;
using RimCoopMod.World;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Vive dentro de Game (Current.Game). Cada tick revisa la cola de paquetes
    /// que llegó por red (en otro hilo) y aplica los cambios sobre el juego,
    /// que solo puede tocarse desde el hilo principal.
    /// </summary>
    public partial class CoopSessionManager : GameComponent
    {
        // Se activa desde Dialog_ConnectToServer justo después de conectar,
        // para que el flujo de "crear mundo nuevo" use la seed del servidor
        // en vez de una generada al azar.
        public static bool PendingWorldGeneration;

        // Cada cuántos ticks se revisa si cambió el conteo de colonos (250 ticks ~= 4s de juego a velocidad normal).
        // Se revisa, pero solo se manda el paquete si el número realmente cambió, para no saturar la red.
        private const int ColonistCheckIntervalTicks = 250;

        // Clave por NOMBRE, no por id: el id que asigna el servidor puede cambiar entre
        // reinicios (ver EnsureRemoteBase), y el nombre es lo único estable entre sesiones.
        private readonly Dictionary<string, CoopPlayerBase> _remoteBases = new Dictionary<string, CoopPlayerBase>();

        private int _localTile = -1;
        private int _lastSentColonistCount = -1;

        // Historial del chat global, en memoria (se pierde al cerrar el juego). Cap simple
        // para que no crezca sin límite en partidas largas.
        public static readonly List<string> ChatLog = new List<string>();
        private const int MaxChatLogEntries = 300;

        // ---- Votación para pausar/despausar ----

        // Otros jugadores conectados ahora mismo (sin contarme a mí), para saber cuántos votos hacen falta.
        private readonly HashSet<int> _connectedPlayerIds = new HashSet<int>();

        // Solo tiene sentido si YO propuse el cambio: quién votó qué, hasta juntar a todos o que se cumpla el plazo.
        private bool? _pendingProposePause;
        private readonly HashSet<int> _votesYes = new HashSet<int>();
        private readonly HashSet<int> _votesNo = new HashSet<int>();
        private float _voteDeadlineTick = -1f; // en Time.realtimeSinceStartup, no en ticks de juego
        private const float VoteTimeoutSeconds = 12f;

        // Para que el propio parche de Harmony no bloquee el cambio que decidimos aplicar nosotros mismos.
        [ThreadStatic] private static bool _applyingVotedPauseChange;
        public static bool IsApplyingVotedPauseChange => _applyingVotedPauseChange;

        // ---- Colaborar / mapa compartido en vivo ----

        // Jugadores que están mirando MI base ahora mismo (soy el dueño/host de ese mapa).
        private readonly HashSet<int> _watchers = new HashSet<int>();

        // thingIDNumber -> dueño real, solo para pawns que llegaron de otro jugador vía "Colaborar".
        // Si un pawn de mi mapa no está acá, se asume que es mío.
        private readonly Dictionary<int, int> _pawnOwners = new Dictionary<int, int>();

        // hostPlayerId -> última foto recibida de esa base (soy espectador). La UI la lee directo.
        private readonly Dictionary<int, MapSnapshotPayload> _remoteSnapshots = new Dictionary<int, MapSnapshotPayload>();

        private const int SnapshotIntervalTicks = 8; // ~0.25s de juego a velocidad normal (antes 30: se veía muy a los saltos)
        private const int ThingsDeltaIntervalTicks = 30;
        private const int BaseSnapshotIntervalTicks = 300; // ~5s: construcciones/ítems cambian menos seguido que la posición

        // ---- Mapa real de otra base (soy espectador) ----

        // hostPlayerId -> el Map real que generé localmente para esa base al "Entrar".
        private readonly Dictionary<int, Map> _coopMaps = new Dictionary<int, Map>();

        // hostPlayerId -> (thingId del dueño real -> Thing que yo generé localmente). Para poder
        // actualizar/borrar sin duplicar cuando llega una foto nueva de construcciones/ítems.
        private readonly Dictionary<int, Dictionary<int, Thing>> _syncedThings = new Dictionary<int, Dictionary<int, Thing>>();

        // hostPlayerId -> (pawnId del dueño real -> el pawn títere que spawneé localmente).
        private readonly Dictionary<int, Dictionary<int, Pawn>> _syncedPawns = new Dictionary<int, Dictionary<int, Pawn>>();

        // hostPlayerId -> ids de pawn cuya apariencia ya pedí, para no pedirla de nuevo cada 0.5s
        // mientras el dueño todavía no contestó (esto es pesado, se pide una sola vez por pawn).
        private readonly Dictionary<int, HashSet<int>> _requestedAppearances = new Dictionary<int, HashSet<int>>();

        // Última "huella" de trabajo que le forzamos a cada títere, para no reiniciar la animación
        // desde cero en cada foto si en realidad sigue haciendo lo mismo.
        private readonly Dictionary<Pawn, string> _puppetJobFingerprints = new Dictionary<Pawn, string>();

        // (soy host) watcherId -> ids de pawn cuya apariencia ya le mandé, para no repetir el
        // envío pesado si me la vuelve a pedir por las dudas.
        private readonly Dictionary<int, HashSet<int>> _sentAppearances = new Dictionary<int, HashSet<int>>();

        public CoopSessionManager(Game game) { }

        public override void GameComponentOnGUI()
        {
            base.GameComponentOnGUI();

            if (!CoopClient.Instance.IsConnected) return;

            if (RimCoopKeyBindingDefOf.RimCoop_ToggleChat.KeyDownEvent)
            {
                Dialog_GlobalChat.Toggle();
            }

            DrawCoopMapOverlayIfNeeded();
        }

        /// <summary>
        /// Si el mapa que estás mirando ahora mismo es la base de otro jugador que "entraste"
        /// con MapParent real, los pawns que YA tienen títere (ver SyncPuppetPawns) se ven y se
        /// clickean solos, como pawns normales del juego — no hace falta dibujar nada para esos.
        /// Acá solo dibujamos un cuadrado de reserva para los que todavía no tienen títere
        /// (recién detectados, esperando que llegue su apariencia por red).
        /// </summary>
        private void DrawCoopMapOverlayIfNeeded()
        {
            var currentMap = Find.CurrentMap;
            if (currentMap == null) return;

            int hostPlayerId = GetHostPlayerIdForMap(currentMap);
            if (hostPlayerId < 0) return;

            _remoteSnapshots.TryGetValue(hostPlayerId, out var snapshot);
            if (snapshot == null) return;

            _syncedPawns.TryGetValue(hostPlayerId, out var puppets);

            foreach (var pawn in snapshot.Pawns)
            {
                if (pawn.Dead) continue;
                if (puppets != null && puppets.ContainsKey(pawn.PawnId)) continue; // ya es un pawn real, se dibuja solo

                Vector3 worldPos = new IntVec3(pawn.X, 0, pawn.Z).ToVector3Shifted();
                Vector2 screenPos = Verse.UI.MapToUIPosition(worldPos);
                var dotRect = new Rect(screenPos.x - 5f, screenPos.y - 5f, 10f, 10f);

                bool mine = pawn.OwnerPlayerId == CoopClient.Instance.LocalPlayerId;
                Color color = pawn.Hostile ? Color.red
                    : pawn.Animal ? Color.yellow
                    : mine ? Color.cyan
                    : Color.green;

                var prevColor = GUI.color;
                GUI.color = color;
                GUI.DrawTexture(dotRect, BaseContent.WhiteTex);
                GUI.color = prevColor;

                if (Mouse.IsOver(dotRect))
                {
                    TooltipHandler.TipRegion(dotRect, $"{pawn.Label} (cargando apariencia...)");
                }
            }
        }

        /// <summary>
        /// Genera (la primera vez) o reabre el mapa real de la base de otro jugador y salta
        /// la cámara ahí, tal cual como visitar el campamento de una caravana. Además arranca
        /// (o mantiene) la sincronización de pawns y construcciones para ese mapa.
        /// </summary>
        public static void EnterCoopMap(CoopPlayerBase target)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;

            if (!target.HasMap)
            {
                CoopLog.Message($"[RimCoop] Generando mapa local para la base de {target.RemotePlayerName}...");
            }

            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(target.Tile,
                DefDatabase<WorldObjectDef>.GetNamed("RimCoop_CoopPlayerBase"));

            instance._coopMaps[target.RemotePlayerId] = map;

            RequestWatch(target.RemotePlayerId);
            CoopClient.Instance.SendBaseSnapshotRequest(target.RemotePlayerId);

            CameraJumper.TryJump(new GlobalTargetInfo(map.Center, map));
        }

        /// <summary>
        /// Llamado por Dialog_GlobalChat cuando el jugador local envía un mensaje.
        /// Lo manda al servidor y lo agrega al log local (el servidor no nos lo reenvía a nosotros mismos).
        /// </summary>
        public static void SendChatMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            CoopClient.Instance.SendChat(message);
            AppendChatLog($"{CoopClient.Instance.LocalPlayerName}: {message}");
        }

        private static void AppendChatLog(string line)
        {
            ChatLog.Add(line);
            if (ChatLog.Count > MaxChatLogEntries)
            {
                ChatLog.RemoveAt(0);
            }
        }

        /// <summary>
        /// A diferencia de GameComponentTick, esto corre en cada frame REAL, incluso con el juego
        /// en pausa (Paused detiene los ticks simulados por completo). Hace falta que sea así para
        /// poder recibir/mandar los votos de pausa mientras el juego ya está pausado — si el
        /// procesamiento de red viviera solo en el tick, votar para despausar sería imposible.
        /// </summary>
        private bool _wasConnectedLastUpdate;

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();

            bool isConnected = CoopClient.Instance.IsConnected;

            // Apenas se (re)establece la conexión, avisamos nuestra posición YA, sin esperar a que
            // "cambie algo": si el server nos había perdido (corte de red breve, no un reinicio del
            // juego), esto evita quedar invisibles en el mapa mundial de los demás indefinidamente.
            if (isConnected && !_wasConnectedLastUpdate && _localTile >= 0)
            {
                _lastSentColonistCount = -1;
                UpdateLocalWealth(force: true);
                CoopClient.Instance.SendUpdate(_localTile, CountLocalColonists());
                SendResearchFull();
            }
            _wasConnectedLastUpdate = isConnected;

            if (!isConnected) return;

            while (CoopClient.Instance.IncomingPackets.TryDequeue(out Packet p))
            {
                HandlePacket(p);
            }

            FlushAreaEdits();

            if (_pendingProposePause.HasValue && Time.realtimeSinceStartup >= _voteDeadlineTick)
            {
                FinalizeVote();
            }
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            if (!CoopClient.Instance.IsConnected) return;

            if (_localTile >= 0 && Find.TickManager.TicksGame % ColonistCheckIntervalTicks == 0)
            {
                int currentCount = CountLocalColonists();
                if (currentCount != _lastSentColonistCount)
                {
                    _lastSentColonistCount = currentCount;
                    CoopClient.Instance.SendUpdate(_localTile, currentCount);
                }
            }

            if (Find.TickManager.TicksGame % 30 == 0) WatchMirrorSettingsEdits();
            TickShared();

            if (_watchers.Count > 0 && Find.TickManager.TicksGame % SnapshotIntervalTicks == 0)
            {
                BroadcastMapSnapshot();
            }

            if (_watchers.Count > 0)
            {
                int t = Find.TickManager.TicksGame;
                if (t % BaseSnapshotIntervalTicks == 0) BroadcastBaseSnapshot();          // foto completa (con zonas, áreas, techos...)
                else if (t % ThingsDeltaIntervalTicks == 0) BroadcastThingsDelta();       // solo lo que cambió: ~cada 0.5 s
            }
        }

        // ---- API pública que usa CoopPlayerBase y el overlay de arriba ----

        public static void RequestWatch(int hostPlayerId) => CoopClient.Instance.SendWatchRequest(hostPlayerId);

        public static void RequestUnwatch(int hostPlayerId) => CoopClient.Instance.SendUnwatchRequest(hostPlayerId);

        /// <summary>Si `map` es un mapa espejo que entraste con EnterCoopMap, devuelve de quién es. Si no, -1.</summary>
        public static int GetHostPlayerIdForMap(Map map)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || map == null) return -1;

            foreach (var kv in instance._coopMaps)
            {
                if (kv.Value == map) return kv.Key;
            }
            return -1;
        }

        public static MapSnapshotPayload GetRemoteSnapshot(int hostPlayerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return null;
            instance._remoteSnapshots.TryGetValue(hostPlayerId, out var snap);
            return snap;
        }

        /// <summary>
        /// Llamado por los parches de Harmony cuando el jugador local intenta pausar/despausar.
        /// Si hay más gente conectada, en vez de aplicarlo ya mismo se propone por votación.
        /// </summary>
        public static void RequestPauseToggle()
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;

            if (instance._pendingProposePause.HasValue)
            {
                Messages.Message("Ya hay una votación de pausa en curso.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            bool proposePause = !Find.TickManager.Paused;

            if (instance._connectedPlayerIds.Count == 0)
            {
                // No hay nadie más conectado todavía: no tiene sentido votar solo.
                ApplyPauseChange(proposePause);
                return;
            }

            instance._pendingProposePause = proposePause;
            instance._votesYes.Clear();
            instance._votesNo.Clear();
            instance._voteDeadlineTick = Time.realtimeSinceStartup + VoteTimeoutSeconds;

            CoopClient.Instance.SendPauseVoteRequest(proposePause);
            Messages.Message(
                $"Se propuso {(proposePause ? "pausar" : "despausar")} el juego. Esperando que los demás respondan...",
                MessageTypeDefOf.NeutralEvent, false);
        }

        private void HandlePauseVoteResponse(PauseVoteResponsePayload resp)
        {
            if (!_pendingProposePause.HasValue) return; // llegó tarde, o no somos quien propuso

            if (resp.Accept) _votesYes.Add(resp.FromPlayerId);
            else _votesNo.Add(resp.FromPlayerId);

            if (_votesYes.Count + _votesNo.Count >= _connectedPlayerIds.Count)
            {
                FinalizeVote();
            }
        }

        /// <summary>Cierra la votación en curso: por unanimidad de los que respondieron, o por plazo vencido.</summary>
        private void FinalizeVote()
        {
            if (!_pendingProposePause.HasValue) return;

            bool approved = _votesNo.Count == 0 && _votesYes.Count > 0;
            bool proposePause = _pendingProposePause.Value;

            _pendingProposePause = null;
            _voteDeadlineTick = -1f;

            CoopClient.Instance.SendPauseVoteResult(approved, proposePause);

            if (approved)
            {
                ApplyPauseChange(proposePause);
                Messages.Message($"Votación aprobada: el juego {(proposePause ? "se pausó" : "se reanudó")}.", MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                Messages.Message("La votación de pausa fue rechazada (o nadie respondió a tiempo).", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void ApplyPauseChange(bool pause)
        {
            _applyingVotedPauseChange = true;
            try { Find.TickManager.CurTimeSpeed = pause ? TimeSpeed.Paused : TimeSpeed.Normal; }
            finally { _applyingVotedPauseChange = false; }
        }

        /// <summary>
        /// Reenvía una orden real (mover, atacar, cosechar, lo que sea) para UN pawn propio dentro
        /// del mapa de otro jugador. El dueño real (host) valida y la ejecuta en su pawn de verdad.
        /// </summary>
        public static void SendOrder(int hostPlayerId, int pawnId, Job job)
        {
            var targetA = job.targetA;
            int targetAId = -1;
            if (targetA.HasThing)
            {
                targetAId = ResolveHostThingId(targetA.Thing);
                if (targetAId < 0)
                {
                    // No sabemos a qué Thing del lado real corresponde esto (todavía no se
                    // sincronizó, o no es un Thing que sigamos). Si igual mandáramos la orden con
                    // solo la celda, un trabajo como "Equipar" no haría nada en silencio — mejor
                    // avisar y no mandar nada.
                    CoopLog.Warning($"[RimCoop] No pude traducir el target ({targetA.Thing.LabelShortCap}) de la orden {job.def.defName} al id real: se descarta.");
                    Messages.Message($"No se pudo mandar la orden sobre {targetA.Thing.LabelShortCap} todavía (no está sincronizado).", MessageTypeDefOf.RejectInput, false);
                    return;
                }
            }

            var payload = new PawnOrderPayload
            {
                JobDefName = job.def.defName,
                TargetAThingId = targetAId,
                TargetAX = targetA.Cell.x,
                TargetAZ = targetA.Cell.z,
                HasTargetB = job.targetB.IsValid
            };

            if (payload.HasTargetB)
            {
                var targetB = job.targetB;
                if (targetB.HasThing)
                {
                    int targetBId = ResolveHostThingId(targetB.Thing);
                    if (targetBId < 0)
                    {
                        CoopLog.Warning($"[RimCoop] No pude traducir el target B ({targetB.Thing.LabelShortCap}) de la orden {job.def.defName}: se descarta.");
                        return;
                    }
                    payload.TargetBThingId = targetBId;
                }
                payload.TargetBX = targetB.Cell.x;
                payload.TargetBZ = targetB.Cell.z;
            }

            CoopLog.Message($"[RimCoop] SendOrder: {job.def.defName} targetA={(targetA.HasThing ? $"Thing#{targetAId}" : targetA.Cell.ToString())} -> jugador {hostPlayerId}.");
            CoopClient.Instance.SendPawnOrder(hostPlayerId, pawnId, payload);
        }

        /// <summary>
        /// Un Thing que ves en el mapa espejo (pawn títere, construcción sincronizada) tiene un id
        /// LOCAL que no significa nada del lado del dueño real. Esto lo traduce de vuelta al id
        /// real, para poder decirle al host "atacá/cosechá ESTO" y que sepa de qué Thing hablamos.
        /// </summary>
        public static int ResolveHostThingId(Thing localThing)
        {
            if (localThing is Pawn pawn && PuppetPawnRegistry.TryGetInfo(pawn, out var info))
            {
                return info.HostPawnId;
            }

            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance != null)
            {
                foreach (var plantDict in instance._syncedPlants.Values)
                    foreach (var pair in plantDict)
                        if (pair.Value == localThing) return pair.Key;

                // Algo que un títere lleva encima (mochila/manos): su id real es la clave del mapeo.
                foreach (var items in instance._puppetItems.Values)
                    foreach (var pair in items)
                        if (pair.Value == localThing) return pair.Key;

                foreach (var kv in instance._syncedThings)
                {
                    foreach (var pair in kv.Value)
                    {
                        if (pair.Value == localThing) return pair.Key;
                    }
                }
            }

            return -1;
        }

        private void BroadcastMapSnapshot()
        {
            var map = Find.AnyPlayerHomeMap;
            if (map == null) return;

            var payload = new MapSnapshotPayload
            {
                HostPlayerId = CoopClient.Instance.LocalPlayerId,
                MapWidth = map.Size.x,
                MapHeight = map.Size.z,
                WeatherDefName = map.weatherManager.CurWeatherLerped?.defName ?? "",
                SkyGlow = map.skyManager.CurSkyGlow
            };

            _slowCounter++;
            bool sendSlow = (_slowCounter % 8) == 0;   // datos que casi no cambian: ~cada 0.5 s
            bool sendMedium = (_slowCounter % 4) == 0; // arma, ropa, inventario, necesidades: ~cada 0.25 s
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                int owner = -1;
                if (pawn.Faction == Faction.OfPlayer)
                {
                    owner = _pawnOwners.TryGetValue(pawn.thingIDNumber, out var tagged)
                        ? tagged
                        : CoopClient.Instance.LocalPlayerId;
                }

                var snapshot = new PawnSnapshot
                {
                    PawnId = pawn.thingIDNumber,
                    Label = pawn.LabelShortCap,
                    OwnerPlayerId = owner,
                    X = pawn.Position.x,
                    Z = pawn.Position.z,
                    JobLabel = pawn.CurJob?.def?.reportString ?? "",
                    Downed = pawn.Downed,
                    Dead = pawn.Dead,
                    Hostile = pawn.HostileTo(Faction.OfPlayer),
                    Animal = pawn.RaceProps?.Animal ?? false,
                    Rot = pawn.Rotation.AsInt,
                    Moving = pawn.pather != null && pawn.pather.Moving,
                    HasMedium = sendMedium
                };

                if (sendMedium)
                {
                    snapshot.EquippedWeaponDefName = pawn.equipment?.Primary?.def?.defName;
                    snapshot.EquippedWeaponStuffDefName = pawn.equipment?.Primary?.Stuff?.defName;
                    snapshot.InventoryCsv = pawn.inventory != null ? ItemsToCsv(pawn.inventory.innerContainer) : "";
                    snapshot.CarriedCsv = pawn.carryTracker?.CarriedThing != null ? ItemsToCsv(new[] { pawn.carryTracker.CarriedThing }) : "";
                    snapshot.ApparelCsv = pawn.apparel != null ? string.Join(";", pawn.apparel.WornApparel.Select(a => ApparelEntry(a))) : "";
                    snapshot.EquippedWeaponQuality = QualityOf(pawn.equipment?.Primary);
                    snapshot.EquippedWeaponHitPoints = pawn.equipment?.Primary?.HitPoints ?? 0;
                }

                // El trabajo actual (no solo el ordenado por un jugador): así el títere puede
                // ejecutarlo con su propio Tick() y animarse de verdad al otro lado.
                Job curJob = pawn.CurJob;
                if (curJob?.def != null)
                {
                    var jobTargetA = curJob.targetA;
                    if (!jobTargetA.HasThing || jobTargetA.Thing != null)
                    {
                        snapshot.CurJobDefName = curJob.def.defName;
                        snapshot.CurJobTargetAThingId = jobTargetA.HasThing ? jobTargetA.Thing.thingIDNumber : -1;
                        snapshot.CurJobTargetAX = jobTargetA.Cell.x;
                        snapshot.CurJobTargetAZ = jobTargetA.Cell.z;
                        snapshot.CurJobHasTargetB = curJob.targetB.IsValid;
                        if (snapshot.CurJobHasTargetB)
                        {
                            var jobTargetB = curJob.targetB;
                            snapshot.CurJobTargetBThingId = jobTargetB.HasThing ? jobTargetB.Thing.thingIDNumber : -1;
                            snapshot.CurJobTargetBX = jobTargetB.Cell.x;
                            snapshot.CurJobTargetBZ = jobTargetB.Cell.z;
                        }
                    }
                }

                FillPawnExtras(pawn, snapshot, sendSlow);
                payload.Pawns.Add(snapshot);
            }
            foreach (var corpse in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Take(60))
            {
                var dead = corpse.InnerPawn;
                if (dead == null || !corpse.Spawned) continue;
                if (!dead.RaceProps.Humanlike && dead.Faction != Faction.OfPlayer) continue;
                payload.Pawns.Add(new PawnSnapshot
                {
                    PawnId = dead.thingIDNumber,
                    Label = dead.LabelShortCap,
                    OwnerPlayerId = -1,
                    X = corpse.Position.x,
                    Z = corpse.Position.z,
                    Dead = true,
                    Animal = dead.RaceProps.Animal
                });
            }
            payload.Interactions.AddRange(DrainInteractions());

            foreach (int watcherId in _watchers)
            {
                payload.ToPlayerId = watcherId;
                CoopClient.Instance.SendMapSnapshot(payload);
            }
        }

        /// <summary>
        /// Junta construcciones e ítems de mi mapa (no pawns, esos van por MapSnapshot) y se los
        /// manda a todos los que me están mirando. El terreno no se manda: sale igual del otro
        /// lado porque comparte seed+tile, así que solo hace falta lo que YO construí/dejé tirado.
        /// </summary>
        private readonly Dictionary<int, Dictionary<int, string>> _sentThingSigs = new Dictionary<int, Dictionary<int, string>>();

        private static string ThingSig(ThingSnapshot t) =>
            t.DefName + "|" + t.X + "|" + t.Z + "|" + t.Rotation + "|" + t.StackCount + "|" + t.HitPoints + "|" + t.StateStr;

        /// <summary>Solo lo que cambió (o apareció / desapareció) desde la última vez que se le mandó a cada watcher: mucho más liviano y rápido que la foto completa.</summary>
        private void BroadcastThingsDelta()
        {
            var map = Find.AnyPlayerHomeMap;
            if (map == null) return;

            var current = CollectThingSnapshots(map);
            var currentSigs = current.ToDictionary(t => t.ThingId, ThingSig);

            foreach (int watcherId in _watchers.ToList())
            {
                if (!_sentThingSigs.TryGetValue(watcherId, out var sent)) continue; // todavía no recibió una foto completa: se le manda en la próxima

                var payload = new BaseSnapshotPayload { HostPlayerId = CoopClient.Instance.LocalPlayerId, ToPlayerId = watcherId, IsDelta = true, HasLayers = false };
                foreach (var t in current)
                    if (!sent.TryGetValue(t.ThingId, out var old) || old != currentSigs[t.ThingId]) payload.Things.Add(t);
                foreach (int id in sent.Keys)
                    if (!currentSigs.ContainsKey(id)) payload.RemovedThingIds.Add(id);

                if (payload.Things.Count == 0 && payload.RemovedThingIds.Count == 0) continue;
                _sentThingSigs[watcherId] = new Dictionary<int, string>(currentSigs);
                CoopClient.Instance.SendBaseSnapshot(payload);
            }
        }

        private List<ThingSnapshot> CollectThingSnapshots(Map map)
        {
            var result = new List<ThingSnapshot>();
            foreach (var thing in map.listerThings.AllThings)
            {
                bool isConstructionSite = thing is Blueprint || thing is Frame; // plano o "en obra", todavía no son Building
                bool isFilthOrFire = thing is Filth || thing is Fire;
                if (thing.def.category != ThingCategory.Building && thing.def.category != ThingCategory.Item && !isConstructionSite && !isFilthOrFire) continue;
                if (!thing.Spawned) continue;

                // Un cadáver no es un ítem simple: envuelve al pawn muerto adentro (nombre, causa
                // de muerte, etc.). Recrearlo con ThingMaker da un cadáver "vacío" que el juego
                // rechaza al spawnear. Sincronizar cadáveres de verdad queda para otra pasada.
                if (thing is Corpse) continue;

                result.Add(new ThingSnapshot
                {
                    ThingId = thing.thingIDNumber,
                    DefName = thing.def.defName,
                    StuffDefName = thing.Stuff?.defName,
                    X = thing.Position.x,
                    Z = thing.Position.z,
                    Rotation = thing.Rotation.AsInt,
                    StackCount = thing is Filth filth ? filth.thickness : thing is Fire fire ? (int)(fire.fireSize * 100f) : thing.stackCount,
                    HitPoints = thing.HitPoints,
                    StateStr = thing is Building ? BuildStateString(thing) : ""
                });
            }
            return result;
        }

        private void BroadcastBaseSnapshot()
        {
            var map = Find.AnyPlayerHomeMap;
            if (map == null) return;

            var payload = new BaseSnapshotPayload { HostPlayerId = CoopClient.Instance.LocalPlayerId };
            payload.Things.AddRange(CollectThingSnapshots(map));
            var fullSigs = payload.Things.ToDictionary(t => t.ThingId, ThingSig);
            foreach (int w in _watchers) _sentThingSigs[w] = new Dictionary<int, string>(fullSigs);

            foreach (var zone in map.zoneManager.AllZones)
            {
                string kind = zone is Zone_Growing ? "growing" : zone is Zone_Stockpile ? "stockpile" : null;
                if (kind == null) continue;
                payload.Zones.Add(new ZoneSnapshot
                {
                    Kind = kind,
                    Label = zone.label,
                    PlantDefName = (zone as Zone_Growing)?.GetPlantDefToGrow()?.defName,
                    CellsCsv = CellsToCsv(zone.Cells),
                    ZoneId = zone.ID,
                    SettingsStr = ZoneSettingsString(zone)
                });
            }

            FillAreasAndStores(payload, map);
            FillWorldLayers(payload, map);

            foreach (int watcherId in _watchers)
            {
                payload.ToPlayerId = watcherId;
                CoopClient.Instance.SendBaseSnapshot(payload);
            }
        }

        /// <summary>
        /// Recibido del lado del espectador: reconstruye construcciones/ítems sobre el Map local
        /// que generamos para esa base. Identifica cada cosa por el thingId del DUEÑO real, así
        /// que si ya la teníamos no la duplica, y si desapareció del lado del dueño la borra acá.
        /// </summary>
        private void ApplyBaseSnapshot(BaseSnapshotPayload snapshot)
        {
            if (!_coopMaps.TryGetValue(snapshot.HostPlayerId, out var map) || map == null) return;

            if (!_syncedThings.TryGetValue(snapshot.HostPlayerId, out var known))
            {
                known = new Dictionary<int, Thing>();
                _syncedThings[snapshot.HostPlayerId] = known;
            }

            var seenIds = new HashSet<int>();

            foreach (var ts in snapshot.Things)
            {
                seenIds.Add(ts.ThingId);

                if (known.TryGetValue(ts.ThingId, out var existing) && existing != null && existing.Spawned)
                {
                    ApplyThingState(existing, ts.StateStr);
                    if (existing is Filth || existing is Fire)
                    {
                        ApplySpecialCount(existing, ts.StackCount);
                        continue;
                    }

                    // Ya lo teníamos: solo actualizamos lo que cambia seguido (cuánto queda de la
                    // pila, vida) — así se ve cuando alguien come una ración o gasta munición.
                    if (existing.stackCount != ts.StackCount)
                    {
                        existing.stackCount = Math.Max(0, ts.StackCount);
                        if (existing.stackCount <= 0) { existing.Destroy(DestroyMode.Vanish); known.Remove(ts.ThingId); continue; }
                    }
                    if (ts.HitPoints > 0 && existing.HitPoints != ts.HitPoints)
                    {
                        existing.HitPoints = Math.Min(ts.HitPoints, existing.MaxHitPoints);
                    }
                    var newCell = new IntVec3(ts.X, 0, ts.Z);
                    if (existing.Position != newCell) existing.Position = newCell;
                    continue;
                }

                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(ts.DefName);
                if (def == null) continue;

                ThingDef stuff = string.IsNullOrEmpty(ts.StuffDefName)
                    ? null
                    : DefDatabase<ThingDef>.GetNamedSilentFail(ts.StuffDefName);

                try
                {
                    Thing thing = ThingMaker.MakeThing(def, stuff);
                    if (!ApplySpecialCount(thing, ts.StackCount)) thing.stackCount = Math.Max(1, ts.StackCount);
                    if (ts.HitPoints > 0) thing.HitPoints = Math.Min(ts.HitPoints, thing.MaxHitPoints);

                    var cell = new IntVec3(ts.X, 0, ts.Z);
                    GenSpawn.Spawn(thing, cell, map, new Rot4(ts.Rotation), WipeMode.Vanish);
                    known[ts.ThingId] = thing;
                    ApplyThingState(thing, ts.StateStr);
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo reconstruir {ts.DefName} en la base espejo: {e.Message}");
                }
            }

            if (!snapshot.IsDelta) ReconcileLocalItems(map, known);
            if (snapshot.HasLayers)
            {
                ApplyWorldLayers(snapshot, map);
                ApplyZones(snapshot, map);
                ApplyAreasAndStores(snapshot, map);
            }

            // Lo que ya no viene es porque el dueño lo perdió/destruyó: lo sacamos acá también.
            // En una foto completa se deduce por lo que falta; en un delta, el dueño lo dice explícitamente.
            var toRemove = snapshot.IsDelta ? snapshot.RemovedThingIds : known.Keys.Where(id => !seenIds.Contains(id)).ToList();
            foreach (var oldId in toRemove)
            {
                if (!known.TryGetValue(oldId, out var gone)) continue;
                if (gone?.Spawned == true) gone.Destroy(DestroyMode.Vanish);
                known.Remove(oldId);
            }
        }

        /// <summary>
        /// Recibido cada ~0.5s: para cada pawn de la foto, si ya tengo su "look" (títere spawneado)
        /// le actualizo la posición con un teletransporte prolijo; si no, pido esa apariencia UNA
        /// sola vez (es pesada, no se puede pedir a cada foto). Si un títere ya no aparece en la
        /// foto o murió, se lo destruye acá.
        /// </summary>
        private void SyncPuppetPawns(MapSnapshotPayload snapshot)
        {
            if (!_coopMaps.TryGetValue(snapshot.HostPlayerId, out var map) || map == null) return;

            // Clima y luz del mapa espejo: se fuerzan a lo que hay REALMENTE del otro lado. El
            // reloj/calendario de la partida no se toca (es global y de B, no se puede "prestar"
            // el de A sin romper todo lo demás) — esto solo iguala cómo se ve, no qué día es.
            if (!string.IsNullOrEmpty(snapshot.WeatherDefName))
            {
                var weatherDef = DefDatabase<WeatherDef>.GetNamedSilentFail(snapshot.WeatherDefName);
                if (weatherDef != null && map.weatherManager.curWeather != weatherDef)
                {
                    map.weatherManager.TransitionTo(weatherDef);
                }
            }
            map.skyManager.ForceSetCurSkyGlow(snapshot.SkyGlow);

            if (!_syncedPawns.TryGetValue(snapshot.HostPlayerId, out var known))
            {
                known = new Dictionary<int, Pawn>();
                _syncedPawns[snapshot.HostPlayerId] = known;
            }

            if (!_requestedAppearances.TryGetValue(snapshot.HostPlayerId, out var requested))
            {
                requested = new HashSet<int>();
                _requestedAppearances[snapshot.HostPlayerId] = requested;
            }

            var seenIds = new HashSet<int>();

            foreach (var ps in snapshot.Pawns)
            {
                seenIds.Add(ps.PawnId);

                if (ps.Dead)
                {
                    HandleDeadPuppet(known, ps, map);
                    continue;
                }

                if (known.TryGetValue(ps.PawnId, out var puppet) && puppet != null && puppet.Spawned)
                {
                    if (ps.Dead)
                    {
                        PuppetPawnRegistry.Unregister(puppet);
                        _puppetJobFingerprints.Remove(puppet);
                        _puppetItems.Remove(puppet);
                        _puppetCarriedKey.Remove(puppet);
                        puppet.Destroy(DestroyMode.Vanish);
                        known.Remove(ps.PawnId);
                        continue;
                    }

                    FollowHostPosition(puppet, ps, map);

                    if (ps.HasMedium)
                    {
                        SyncPuppetEquipment(puppet, ps);
                        SyncPuppetInventory(puppet, ps);
                        SyncPuppetApparel(puppet, ps);
                    }
                    ApplyPuppetExtras(puppet, ps, map);
                    SyncPuppetJob(map, snapshot.HostPlayerId, puppet, ps);
                    continue;
                }

                if (ps.Dead) continue; // no tiene sentido pedir el look de alguien que ya murió

                if (requested.Add(ps.PawnId))
                {
                    CoopClient.Instance.SendPawnAppearanceRequest(snapshot.HostPlayerId, ps.PawnId);
                }
            }

            ApplyInteractions(snapshot);

            foreach (var oldId in known.Keys.Where(id => !seenIds.Contains(id)).ToList())
            {
                if (known[oldId] != null)
                {
                    PuppetPawnRegistry.Unregister(known[oldId]);
                    _puppetJobFingerprints.Remove(known[oldId]);
                    _puppetItems.Remove(known[oldId]);
                    _puppetCarriedKey.Remove(known[oldId]);
                    if (known[oldId].Spawned) known[oldId].Destroy(DestroyMode.Vanish);
                    else if (known[oldId].Corpse != null && !known[oldId].Corpse.Destroyed) known[oldId].Corpse.Destroy(DestroyMode.Vanish);
                }
                known.Remove(oldId);
            }
        }

        /// <summary>
        /// Si el trabajo que está haciendo el pawn real cambió (no solo cuando vos le das una
        /// orden — también sus decisiones propias: comer, cosechar, atacar algo), se lo forzamos
        /// al títere para que lo ejecute con su propio Tick() y se anime de verdad al caminar,
        /// trabajar, etc. No repetimos el forzado si sigue siendo el mismo trabajo (si no, la
        /// animación arrancaría de cero en cada foto y nunca se vería terminar nada).
        /// </summary>
        private static readonly HashSet<string> MovementOnlyJobs = new HashSet<string>
        {
            "Goto", "GotoWander", "Wait_Wander", "GotoSafeTemperature", "Follow", "FollowClose", "Flee", "FleeAndCower", "Wait_MaintainPosture"
        };

        [ThreadStatic] public static bool MirrorPathing;

        /// <summary>
        /// Posición del títere = posición del pawn real. Si está cerca, camina hasta esa celda (mismo
        /// paso, animación de caminar de verdad, sin saltos); solo si se atrasó de más se teletransporta.
        /// </summary>
        private void FollowHostPosition(Pawn puppet, PawnSnapshot ps, Map map)
        {
            var cell = new IntVec3(ps.X, 0, ps.Z);
            if (!cell.InBounds(map)) return;

            int d = Math.Max(Math.Abs(puppet.Position.x - cell.x), Math.Abs(puppet.Position.z - cell.z));
            if (d == 0)
            {
                if (!ps.Moving && puppet.pather != null && !puppet.pather.Moving) puppet.Rotation = new Rot4(ps.Rot);
                return;
            }

            bool mustTeleport = d > 5 || ps.Downed || puppet.pather == null || puppet.Downed;
            if (!mustTeleport)
            {
                var pather = puppet.pather;
                if (!pather.Moving || pather.Destination.Cell != cell)
                {
                    MirrorPathing = true;
                    try { pather.StartPath(cell, Verse.AI.PathEndMode.OnCell); }
                    catch { mustTeleport = true; }
                    finally { MirrorPathing = false; }
                    if (!pather.Moving) mustTeleport = true; // no encontró camino (puerta cerrada para su facción, etc.)
                }
            }

            if (mustTeleport)
            {
                puppet.Position = cell;
                puppet.Notify_Teleported(endCurrentJob: false, resetTweenedPos: d > 2);
            }
        }

        private void SyncPuppetJob(Map map, int hostPlayerId, Pawn puppet, PawnSnapshot ps)
        {
            string fingerprint = string.IsNullOrEmpty(ps.CurJobDefName)
                ? ""
                : $"{ps.CurJobDefName}|{(ps.CurJobTargetAThingId >= 0 ? "t" + ps.CurJobTargetAThingId : "c" + ps.CurJobTargetAX + "," + ps.CurJobTargetAZ)}|{(ps.CurJobTargetBThingId >= 0 ? "t" + ps.CurJobTargetBThingId : "")}";

            if (_puppetJobFingerprints.TryGetValue(puppet, out var last) && last == fingerprint) return;
            _puppetJobFingerprints[puppet] = fingerprint;

            if (string.IsNullOrEmpty(ps.CurJobDefName))
            {
                // El pawn real no está haciendo nada: el títere tampoco (no decide por su cuenta, ver Pawn_JobTracker_TryFindAndStartJob_Patch).
                try { if (puppet.CurJob != null) puppet.jobs.EndCurrentJob(JobCondition.InterruptForced, false); } catch { }
                return;
            }

            // Caminar de un lado a otro lo resuelve FollowHostPosition (el títere sigue al real a pie);
            // mandarle también el trabajo de caminar haría que se peleen dos destinos distintos.
            if (MovementOnlyJobs.Contains(ps.CurJobDefName))
            {
                try { if (puppet.CurJob != null) puppet.jobs.EndCurrentJob(JobCondition.InterruptForced, false); } catch { }
                return;
            }

            Job job = BuildMirrorJob(hostPlayerId, ps);
            if (job == null)
            {
                _puppetJobFingerprints.Remove(puppet); // todavía no se puede reconstruir (target sin sincronizar): reintentar en la próxima foto
                return;
            }

            try { puppet.jobs.StartJob(job, JobCondition.InterruptForced, resumeCurJobAfterwards: false); }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo replicar el trabajo {ps.CurJobDefName} en el títere {puppet.LabelShortCap}: {e.Message}");
            }
        }

        private Job BuildMirrorJob(int hostPlayerId, PawnSnapshot ps)
        {
            JobDef jobDef = DefDatabase<JobDef>.GetNamedSilentFail(ps.CurJobDefName);
            if (jobDef == null) return null;

            LocalTargetInfo targetA = ps.CurJobTargetAThingId >= 0
                ? (LocalTargetInfo)FindLocalMirrorThing(hostPlayerId, ps.CurJobTargetAThingId)
                : (LocalTargetInfo)new IntVec3(ps.CurJobTargetAX, 0, ps.CurJobTargetAZ);

            if (!targetA.IsValid) return null; // el target (ítem/pawn) todavía no se sincronizó acá

            if (!ps.CurJobHasTargetB) return new Job(jobDef, targetA);

            LocalTargetInfo targetB = ps.CurJobTargetBThingId >= 0
                ? (LocalTargetInfo)FindLocalMirrorThing(hostPlayerId, ps.CurJobTargetBThingId)
                : (LocalTargetInfo)new IntVec3(ps.CurJobTargetBX, 0, ps.CurJobTargetBZ);

            if (!targetB.IsValid) return null;

            return new Job(jobDef, targetA, targetB);
        }

        /// <summary>El reverso de ResolveHostThingId: dado el id del DUEÑO real, busca la copia local (títere o construcción/ítem espejo).</summary>
        private Thing FindLocalMirrorThing(int hostPlayerId, int hostThingId)
        {
            if (_syncedPawns.TryGetValue(hostPlayerId, out var pawns) && pawns.TryGetValue(hostThingId, out var pawn)) return pawn;
            if (_syncedThings.TryGetValue(hostPlayerId, out var things) && things.TryGetValue(hostThingId, out var thing)) return thing;
            if (_syncedPlants.TryGetValue(hostPlayerId, out var plants) && plants.TryGetValue(hostThingId, out var plant) && plant != null && !plant.Destroyed) return plant;
            // Cosas que el pawn real lleva encima (mochila/manos): viven dentro del títere, no en el mapa.
            foreach (var items in _puppetItems.Values)
                if (items.TryGetValue(hostThingId, out var carried) && carried != null && !carried.Destroyed) return carried;
            return null;
        }

        // Cosas del inventario/manos de cada títere, indexadas por el id del DUEÑO real.
        private readonly Dictionary<Pawn, Dictionary<int, Thing>> _puppetItems = new Dictionary<Pawn, Dictionary<int, Thing>>();

        private readonly Dictionary<Pawn, int> _puppetCarriedKey = new Dictionary<Pawn, int>();

        // Lo que el pawn real lleva en las manos se pone en el carryTracker del títere (se ve cargado de verdad).
        private void SyncPuppetCarried(Pawn puppet, PawnSnapshot ps, Dictionary<int, Thing> mine)
        {
            if (puppet.carryTracker == null) return;

            string[] f = null;
            if (!string.IsNullOrEmpty(ps.CarriedCsv))
            {
                var parts = ps.CarriedCsv.Split(';')[0].Split(',');
                if (parts.Length == 6) f = parts;
            }

            Thing cur = puppet.carryTracker.CarriedThing;
            if (f == null)
            {
                if (cur != null && !cur.Destroyed) cur.Destroy(DestroyMode.Vanish);
                if (_puppetCarriedKey.TryGetValue(puppet, out int old)) { mine.Remove(old); _puppetCarriedKey.Remove(puppet); }
                return;
            }

            int.TryParse(f[0], out int hostId);
            int count = int.TryParse(f[3], out int c) ? Math.Max(1, c) : 1;

            if (cur != null && cur.def.defName == f[1])
            {
                cur.stackCount = count;
                if (_puppetCarriedKey.TryGetValue(puppet, out int oldKey) && oldKey != hostId) mine.Remove(oldKey);
                mine[hostId] = cur;
                _puppetCarriedKey[puppet] = hostId;
                return;
            }

            if (cur != null && !cur.Destroyed) cur.Destroy(DestroyMode.Vanish);
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(f[1]);
            if (def == null) return;
            try
            {
                var stuff = string.IsNullOrEmpty(f[2]) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(f[2]);
                Thing item = ThingMaker.MakeThing(def, stuff);
                item.stackCount = count;
                ApplyQualityAndHp(item, int.TryParse(f[4], out int q) ? q : -1, int.TryParse(f[5], out int hp) ? hp : 0);
                if (puppet.carryTracker.innerContainer.TryAdd(item, false))
                {
                    mine[hostId] = item;
                    _puppetCarriedKey[puppet] = hostId;
                }
                else item.Destroy(DestroyMode.Vanish);
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo poner {f[1]} en las manos del títere: {e.Message}");
            }
        }

        private static string ItemsToCsv(IEnumerable<Thing> items)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in items)
            {
                if (t == null || t.Destroyed) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(t.thingIDNumber).Append(',').Append(t.def.defName).Append(',').Append(t.Stuff?.defName ?? "").Append(',').Append(t.stackCount)
                  .Append(',').Append(QualityOf(t)).Append(',').Append(t.HitPoints);
            }
            return sb.ToString();
        }

        private static int QualityOf(Thing t)
        {
            var q = t?.TryGetComp<CompQuality>();
            return q != null ? (int)q.Quality : -1;
        }

        private static void ApplyQualityAndHp(Thing t, int quality, int hp)
        {
            try
            {
                if (quality >= 0) t.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)quality, ArtGenerationContext.Colony);
                if (hp > 0) t.HitPoints = Math.Min(hp, t.MaxHitPoints);
            }
            catch { }
        }

        private static string ApparelEntry(Apparel a) =>
            a.def.defName + "," + (a.Stuff?.defName ?? "") + "," + QualityOf(a) + "," + a.HitPoints;

        // Iguala la ropa puesta del títere con la del pawn real (si difiere, la reemplaza entera).
        private static void SyncPuppetApparel(Pawn puppet, PawnSnapshot ps)
        {
            if (puppet.apparel == null || ps.ApparelCsv == null) return;

            var wantedDefs = string.IsNullOrEmpty(ps.ApparelCsv)
                ? new List<string>()
                : ps.ApparelCsv.Split(';').Select(e => e.Split(',')[0] + "|" + e.Split(',')[1]).OrderBy(x => x).ToList();
            var currentDefs = puppet.apparel.WornApparel.Select(a => a.def.defName + "|" + (a.Stuff?.defName ?? "")).OrderBy(x => x).ToList();
            if (wantedDefs.SequenceEqual(currentDefs)) return;

            try
            {
                foreach (var old in puppet.apparel.WornApparel.ToList())
                {
                    puppet.apparel.Remove(old);
                    old.Destroy(DestroyMode.Vanish);
                }
                if (string.IsNullOrEmpty(ps.ApparelCsv)) return;

                foreach (var entry in ps.ApparelCsv.Split(';'))
                {
                    var f = entry.Split(',');
                    if (f.Length < 4) continue;
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(f[0]);
                    if (def == null) continue;
                    var stuff = string.IsNullOrEmpty(f[1]) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(f[1]);
                    var apparel = (Apparel)ThingMaker.MakeThing(def, stuff);
                    ApplyQualityAndHp(apparel, int.TryParse(f[2], out int q) ? q : -1, int.TryParse(f[3], out int hp) ? hp : 0);
                    puppet.apparel.Wear(apparel, false, false);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo copiar la ropa al títere {puppet.LabelShortCap}: {e.Message}");
            }
        }

        /// <summary>
        /// Iguala la mochila y lo que lleva en las manos el títere con el pawn real, ítem por ítem
        /// (por id real) para no destruir un ítem que un trabajo en curso (ej. comer) esté usando.
        /// </summary>
        private void SyncPuppetInventory(Pawn puppet, PawnSnapshot ps)
        {
            if (puppet.inventory == null) return;
            SyncPuppetInventoryCore(puppet, ps);

            // Lo que el títere metió en su mochila con su propio trabajo (ej. "tomar madera") no existe en el
            // pawn real: se borra, si no queda un ítem "fantasma" al que no se le puede dar órdenes.
            if (_puppetItems.TryGetValue(puppet, out var mine))
            {
                var tracked = new HashSet<Thing>(mine.Values);
                foreach (var t in puppet.inventory.innerContainer.ToList())
                    if (!tracked.Contains(t)) { try { t.Destroy(DestroyMode.Vanish); } catch { } }
            }
        }

        private void SyncPuppetInventoryCore(Pawn puppet, PawnSnapshot ps)
        {
            if (!_puppetItems.TryGetValue(puppet, out var mine))
            {
                mine = new Dictionary<int, Thing>();
                _puppetItems[puppet] = mine;
            }

            var wanted = new Dictionary<int, string[]>();
            if (!string.IsNullOrEmpty(ps.InventoryCsv))
            {
                foreach (var entry in ps.InventoryCsv.Split(';'))
                {
                    var f = entry.Split(',');
                    if (f.Length == 6 && int.TryParse(f[0], out int id)) wanted[id] = f;
                }
            }

            SyncPuppetCarried(puppet, ps, mine);

            int carriedKey = _puppetCarriedKey.TryGetValue(puppet, out int ck) ? ck : int.MinValue;
            foreach (int id in mine.Keys.Where(id => !wanted.ContainsKey(id) && id != carriedKey).ToList())
            {
                var gone = mine[id];
                if (gone != null && !gone.Destroyed) gone.Destroy(DestroyMode.Vanish);
                mine.Remove(id);
            }

            foreach (var kv in wanted)
            {
                int count = int.TryParse(kv.Value[3], out int c) ? c : 1;
                if (mine.TryGetValue(kv.Key, out var existing) && existing != null && !existing.Destroyed)
                {
                    if (existing.stackCount != count && count > 0) existing.stackCount = count;
                    ApplyQualityAndHp(existing, -1, int.TryParse(kv.Value[5], out int ehp) ? ehp : 0);
                    continue;
                }

                var def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Value[1]);
                if (def == null) continue;
                var stuff = string.IsNullOrEmpty(kv.Value[2]) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(kv.Value[2]);
                try
                {
                    Thing item = ThingMaker.MakeThing(def, stuff);
                    item.stackCount = Math.Max(1, count);
                    ApplyQualityAndHp(item, int.TryParse(kv.Value[4], out int q) ? q : -1, int.TryParse(kv.Value[5], out int hp) ? hp : 0);
                    if (puppet.inventory.innerContainer.TryAdd(item, false)) mine[kv.Key] = item;
                    else item.Destroy(DestroyMode.Vanish);
                }
                catch (Exception e)
                {
                    CoopLog.Warning($"[RimCoop] No se pudo copiar {kv.Value[1]} al inventario del títere: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Si lo que tiene equipado el pawn real cambió (se equipó/desequipó un arma), replica eso
        /// en el títere. Sin esto, un arma que el dueño real levantó del piso simplemente
        /// "desaparecía" en el mapa espejo: dejaba de existir como ítem tirado (porque ya no está
        /// en el piso) pero tampoco se veía en la mano del títere.
        /// </summary>
        private static void SyncPuppetEquipment(Pawn puppet, PawnSnapshot ps)
        {
            string curDefName = puppet.equipment?.Primary?.def?.defName;
            if (curDefName == ps.EquippedWeaponDefName) return; // sin cambios

            if (puppet.equipment == null) return; // este tipo de pawn no puede tener equipo (ej. animales)

            if (puppet.equipment.Primary != null)
            {
                puppet.equipment.Primary.Destroy(DestroyMode.Vanish);
            }

            if (string.IsNullOrEmpty(ps.EquippedWeaponDefName)) return; // se desequipó, ya está

            ThingDef weaponDef = DefDatabase<ThingDef>.GetNamedSilentFail(ps.EquippedWeaponDefName);
            if (weaponDef == null) return;

            ThingDef stuff = string.IsNullOrEmpty(ps.EquippedWeaponStuffDefName)
                ? null
                : DefDatabase<ThingDef>.GetNamedSilentFail(ps.EquippedWeaponStuffDefName);

            try
            {
                var weapon = (ThingWithComps)ThingMaker.MakeThing(weaponDef, stuff);
                ApplyQualityAndHp(weapon, ps.EquippedWeaponQuality, ps.EquippedWeaponHitPoints);
                puppet.equipment.AddEquipment(weapon);
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo equipar {ps.EquippedWeaponDefName} en el títere {puppet.LabelShortCap}: {e.Message}");
            }
        }

        /// <summary>
        /// Llega la apariencia completa de un pawn que pedimos: lo spawneamos como un pawn real
        /// (títere, sin IA propia — ver PuppetPawnRegistry/Pawn_Tick_Patch) en el lugar que decía
        /// la última foto que tengamos de esa base.
        /// </summary>
        private void ApplyPawnAppearance(PawnAppearancePayload payload)
        {
            if (!_coopMaps.TryGetValue(payload.HostPlayerId, out var map) || map == null) return;

            if (_syncedPawns.TryGetValue(payload.HostPlayerId, out var known) && known.ContainsKey(payload.PawnId))
            {
                return; // ya lo teníamos (puede pasar si se pidió dos veces por una carrera de red)
            }

            Pawn puppet = PawnTransfer.DeserializePawn(payload.SerializedPawn);
            if (puppet == null)
            {
                CoopLog.Warning($"[RimCoop] No se pudo reconstruir la apariencia del pawn {payload.PawnId}.");
                return;
            }

            IntVec3 cell = map.Center;
            if (_remoteSnapshots.TryGetValue(payload.HostPlayerId, out var snap))
            {
                var ps = snap.Pawns.FirstOrDefault(p => p.PawnId == payload.PawnId);
                if (ps != null) cell = new IntVec3(ps.X, 0, ps.Z);
            }

            try
            {
                GenSpawn.Spawn(puppet, cell, map, WipeMode.Vanish);
                PuppetPawnRegistry.Register(puppet, payload.HostPlayerId, payload.PawnId);

                if (!_syncedPawns.TryGetValue(payload.HostPlayerId, out known))
                {
                    known = new Dictionary<int, Pawn>();
                    _syncedPawns[payload.HostPlayerId] = known;
                }
                known[payload.PawnId] = puppet;
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo spawnear el títere del pawn {payload.PawnId}: {e.Message}");
            }
        }

        private void HandlePawnOrder(PawnOrderPayload order)
        {
            var map = Find.AnyPlayerHomeMap;
            if (map == null) return;

            Pawn pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == order.PawnId);
            if (pawn == null) return;

            int owner = _pawnOwners.TryGetValue(pawn.thingIDNumber, out var tagged)
                ? tagged
                : CoopClient.Instance.LocalPlayerId;

            // No es su colono: se ignora en silencio. El que ordena ya se avisó solo del lado suyo (UI).
            if (owner != order.FromPlayerId) return;

            JobDef jobDef = DefDatabase<JobDef>.GetNamedSilentFail(order.JobDefName) ?? JobDefOf.Goto;

            LocalTargetInfo targetA = order.TargetAThingId >= 0
                ? (LocalTargetInfo)FindThingById(map, order.TargetAThingId)
                : (LocalTargetInfo)new IntVec3(order.TargetAX, 0, order.TargetAZ);

            if (!targetA.IsValid)
            {
                CoopLog.Warning($"[RimCoop] Orden {order.JobDefName} descartada: no encontré el Thing id {order.TargetAThingId} en mi mapa (¿ya no existe?).");
                return;
            }

            Job job;
            if (order.HasTargetB)
            {
                LocalTargetInfo targetB = order.TargetBThingId >= 0
                    ? (LocalTargetInfo)FindThingById(map, order.TargetBThingId)
                    : (LocalTargetInfo)new IntVec3(order.TargetBX, 0, order.TargetBZ);
                job = new Job(jobDef, targetA, targetB);
            }
            else
            {
                job = new Job(jobDef, targetA);
            }

            // Esta orden ya se validó arriba; Pawn_JobTracker_TryTakeOrderedJob_Patch usa esta
            // bandera para no volver a filtrarla (si no, el propio host nunca podría ejecutar
            // las órdenes que da el dueño remoto sobre SU pawn).
            _processingRemoteOrder = true;
            try
            {
                bool started = pawn.jobs.TryTakeOrderedJob(job);
                CoopLog.Message($"[RimCoop] Orden remota {order.JobDefName} sobre {pawn.LabelShortCap}: TryTakeOrderedJob devolvió {started}.");
            }
            catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo ejecutar la orden remota ({order.JobDefName}): {e.Message}"); }
            finally { _processingRemoteOrder = false; }
        }

        private static Thing FindThingById(Map map, int thingId)
        {
            return map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
        }

        [ThreadStatic] private static bool _processingRemoteOrder;

        /// <summary>
        /// Usado por Pawn_JobTracker_TryTakeOrderedJob_Patch para decidir si el juego LOCAL
        /// (este proceso) tiene permitido dar órdenes directas a un pawn. Si el pawn no está en
        /// el diccionario de dueños es porque es un colono propio de toda la vida: siempre se puede.
        /// </summary>
        public static bool CanLocalPlayerCommand(Pawn pawn)
        {
            if (_processingRemoteOrder) return true;
            if (pawn?.Faction == null || pawn.Faction != Faction.OfPlayer) return true;

            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || !CoopClient.Instance.IsConnected) return true;

            int owner = instance._pawnOwners.TryGetValue(pawn.thingIDNumber, out var tagged)
                ? tagged
                : CoopClient.Instance.LocalPlayerId;

            return owner == CoopClient.Instance.LocalPlayerId;
        }

        private void HandleJoinRequest(JoinRequestPayload req)
        {
            var map = Find.AnyPlayerHomeMap;
            if (map == null)
            {
                CoopLog.Warning("[RimCoop] Llegaron colonos pero no tengo Find.AnyPlayerHomeMap (¿no tenés colonia activa?).");
                CoopClient.Instance.SendJoinResult(req.FromPlayerId, false, "No tengo una colonia activa para recibir colonos.");
                return;
            }

            int added = 0;
            foreach (var xml in req.SerializedPawns)
            {
                try
                {
                    Pawn pawn = PawnTransfer.DeserializePawn(xml);
                    if (pawn == null)
                    {
                        CoopLog.Warning("[RimCoop] DeserializePawn devolvió null (ver log anterior para el error real).");
                        continue;
                    }

                    pawn.SetFaction(Faction.OfPlayer);

                    // Las políticas de ropa/drogas/ideología del colono venían de la otra partida y
                    // no significan nada acá (se limpiaron antes de mandarlo); le asignamos las de esta.
                    if (pawn.outfits != null) pawn.outfits.CurrentApparelPolicy = Current.Game.outfitDatabase.DefaultOutfit();
                    if (pawn.drugs != null) pawn.drugs.CurrentPolicy = Current.Game.drugPolicyDatabase.DefaultDrugPolicy();
                    if (ModsConfig.IdeologyActive && pawn.ideo != null)
                    {
                        try { pawn.ideo.SetIdeo(Faction.OfPlayer.ideos.PrimaryIdeo); } catch { /* no crítico */ }
                    }

                    IntVec3 cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 10);
                    GenSpawn.Spawn(pawn, cell, map);
                    _pawnOwners[pawn.thingIDNumber] = req.FromPlayerId;
                    added++;
                    CoopLog.Message($"[RimCoop] Colono {pawn.LabelShortCap} apareció en {cell} y quedó asignado al jugador {req.FromPlayerId}.");
                }
                catch (System.Exception e)
                {
                    CoopLog.Error("[RimCoop] Excepción al recibir un colono: " + e);
                }
            }

            CoopClient.Instance.SendJoinResult(req.FromPlayerId, added > 0,
                added > 0 ? $"{added} colono(s) se unieron a la base." : "No se pudo recibir a los colonos.");
        }

        private static int CountLocalColonists()
        {
            int count = 0;
            foreach (var map in Find.Maps)
            {
                if (map.IsPlayerHome) count += map.mapPawns.FreeColonistsCount;
            }
            return count;
        }

        private void HandlePacket(Packet p)
        {
            switch (p.Type)
            {
                case PacketType.WorldData:
                    {
                        var wd = p.GetPayload<WorldDataPayload>();
                        foreach (var existing in wd.ExistingPlayers) _connectedPlayerIds.Add(existing.PlayerId);
                        break;
                    }

                case PacketType.PlayerJoined:
                    {
                        var info = p.GetPayload<PlayerBaseInfo>();
                        _connectedPlayerIds.Add(info.PlayerId);
                        EnsureRemoteBase(info);
                        SendResearchFull(); // el que recién entra recibe mi investigación
                        Messages.Message($"{info.PlayerName} se unió a la partida.", MessageTypeDefOf.NeutralEvent, false);
                        break;
                    }

                case PacketType.PlayerLeft:
                    {
                        var info = p.GetPayload<PlayerBaseInfo>();
                        _connectedPlayerIds.Remove(info.PlayerId);
                        if (_remoteBases.TryGetValue(info.PlayerName, out var wobj))
                        {
                            Find.WorldObjects.Remove(wobj);
                            _remoteBases.Remove(info.PlayerName);
                        }
                        Messages.Message($"{info.PlayerName} se desconectó.", MessageTypeDefOf.NeutralEvent, false);
                        break;
                    }

                case PacketType.PlayerUpdate:
                    {
                        var update = p.GetPayload<PlayerUpdatePayload>();
                        EnsureRemoteBase(new PlayerBaseInfo
                        {
                            PlayerId = update.PlayerId,
                            PlayerName = update.PlayerName,
                            Tile = update.Tile,
                            ColonistCount = update.ColonistCount,
                            Wealth = update.Wealth
                        });
                        break;
                    }

                case PacketType.TradeRequest:
                    {
                        var t = p.GetPayload<TradeOrAttackPayload>();
                        Messages.Message($"{t.FromPlayerName} quiere comerciar con vos.", MessageTypeDefOf.PositiveEvent, false);
                        break;
                    }

                case PacketType.AttackRequest:
                    HandleAttack(p.GetPayload<TradeOrAttackPayload>());
                    break;

                case PacketType.TradeMessage:
                    HandleTradeMessage(p.GetPayload<TradeMessagePayload>());
                    break;

                case PacketType.Chat:
                    {
                        var c = p.GetPayload<ChatPayload>();
                        AppendChatLog($"{c.PlayerName}: {c.Message}");
                        if (!Dialog_GlobalChat.IsChatOpen)
                        {
                            Messages.Message($"[Chat] {c.PlayerName}: {c.Message} (F7 para abrir el chat)", MessageTypeDefOf.SilentInput, false);
                        }
                        break;
                    }

                case PacketType.WatchRequest:
                    {
                        var w = p.GetPayload<WatchRequestPayload>();
                        _watchers.Add(w.FromPlayerId);
                        CoopLog.Message($"[RimCoop] Jugador {w.FromPlayerId} empezó a mirar tu base en vivo.");
                        Messages.Message("Otro jugador está mirando tu base en vivo.", MessageTypeDefOf.NeutralEvent, false);
                        break;
                    }

                case PacketType.UnwatchRequest:
                    {
                        var w = p.GetPayload<WatchRequestPayload>();
                        _watchers.Remove(w.FromPlayerId);
                        _sentThingSigs.Remove(w.FromPlayerId);
                        break;
                    }

                case PacketType.MapSnapshot:
                    {
                        var snap = p.GetPayload<MapSnapshotPayload>();
                        _remoteSnapshots[snap.HostPlayerId] = snap;
                        SyncPuppetPawns(snap);
                        break;
                    }

                case PacketType.PawnOrder:
                    HandlePawnOrder(p.GetPayload<PawnOrderPayload>());
                    break;

                case PacketType.JoinRequest:
                    {
                        var req = p.GetPayload<JoinRequestPayload>();
                        CoopLog.Message($"[RimCoop] Llegó JoinRequest de jugador {req.FromPlayerId} con {req.SerializedPawns.Count} colono(s).");
                        HandleJoinRequest(req);
                        break;
                    }

                case PacketType.JoinResult:
                    {
                        var r = p.GetPayload<JoinResultPayload>();
                        CoopLog.Message($"[RimCoop] JoinResult recibido: éxito={r.Success} - {r.Message}");
                        Messages.Message(r.Message, r.Success ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.RejectInput, false);
                        break;
                    }

                case PacketType.BaseSnapshotRequest:
                    {
                        var req = p.GetPayload<BaseSnapshotRequestPayload>();
                        _watchers.Add(req.FromPlayerId); // por si acaso no llegó el WatchRequest antes
                        _forceFullPlants = true;
                        BroadcastBaseSnapshot();
                        break;
                    }

                case PacketType.BaseSnapshot:
                    ApplyBaseSnapshot(p.GetPayload<BaseSnapshotPayload>());
                    break;

                case PacketType.PawnAppearanceRequest:
                    HandlePawnAppearanceRequest(p.GetPayload<PawnAppearanceRequestPayload>());
                    break;

                case PacketType.PawnAppearance:
                    ApplyPawnAppearance(p.GetPayload<PawnAppearancePayload>());
                    break;

                case PacketType.PauseVoteRequest:
                    {
                        var req = p.GetPayload<PauseVoteRequestPayload>();
                        Find.WindowStack.Add(new Dialog_PauseVotePrompt(req));
                        break;
                    }

                case PacketType.PauseVoteResponse:
                    HandlePauseVoteResponse(p.GetPayload<PauseVoteResponsePayload>());
                    break;

                case PacketType.PauseVoteResult:
                    {
                        var result = p.GetPayload<PauseVoteResultPayload>();
                        if (result.Approved)
                        {
                            ApplyPauseChange(result.ProposePause);
                            Messages.Message($"Votación aprobada: el juego {(result.ProposePause ? "se pausó" : "se reanudó")}.", MessageTypeDefOf.NeutralEvent, false);
                        }
                        else
                        {
                            Messages.Message("La votación de pausa fue rechazada.", MessageTypeDefOf.RejectInput, false);
                        }
                        break;
                    }

                case PacketType.BuildRequest:
                    HandleBuildRequest(p.GetPayload<BuildRequestPayload>());
                    break;

                case PacketType.EventNotice:
                    ReceiveEventNotice(p.GetPayload<EventNoticePayload>());
                    break;

                case PacketType.PawnSettingRequest:
                    HandlePawnSetting(p.GetPayload<PawnSettingPayload>());
                    break;

                case PacketType.SpeedChange:
                    ReceiveSpeedChange(p.GetPayload<SpeedChangePayload>());
                    break;

                case PacketType.ResearchSync:
                    ReceiveResearchSync(p.GetPayload<ResearchSyncPayload>());
                    break;

                case PacketType.WorldEvent:
                    ReceiveWorldEvent(p.GetPayload<WorldEventPayload>());
                    break;
            }
        }

        /// <summary>
        /// Alguien mirando mi base pidió colocar un plano de construcción. Se coloca de verdad en
        /// mi mapa real, con mi facción — de ahí en más, cualquier colono (mío o transferido) lo
        /// puede construir normalmente, y el progreso se ve reflejado solo por BroadcastBaseSnapshot.
        /// </summary>
        private void HandleBuildRequest(BuildRequestPayload req)
        {
            var map = Find.AnyPlayerHomeMap;
            if (map == null) return;

            if (HandleExtraRequest(req, map)) return;

            if (req.DefName.StartsWith("@zone"))
            {
                HandleRemoteZone(req, map);
                return;
            }

            if (req.DefName == BuildActionDeconstruct || req.DefName == BuildActionCancel)
            {
                HandleRemoteRemoval(req, map);
                return;
            }

            BuildableDef def = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(req.DefName)
                ?? DefDatabase<TerrainDef>.GetNamedSilentFail(req.DefName);
            if (def == null)
            {
                CoopLog.Warning($"[RimCoop] BuildRequest con def desconocida: {req.DefName}");
                CoopClient.Instance.SendJoinResult(req.FromPlayerId, false, $"El dueño de la base no conoce '{req.DefName}' (¿mods distintos?).");
                return;
            }

            ThingDef stuff = string.IsNullOrEmpty(req.StuffDefName)
                ? null
                : DefDatabase<ThingDef>.GetNamedSilentFail(req.StuffDefName);

            var cell = new IntVec3(req.X, 0, req.Z);
            var rot = new Rot4(req.Rotation);

            var report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, false, null, null, stuff);
            if (!report.Accepted)
            {
                CoopLog.Warning($"[RimCoop] No se pudo colocar {req.DefName} en {cell}: {report.Reason}");
                CoopClient.Instance.SendJoinResult(req.FromPlayerId, false, $"No se pudo construir {def.label} ahí: {report.Reason}");
                return;
            }

            try
            {
                GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff, null, null, true);
                CoopLog.Message($"[RimCoop] Jugador {req.FromPlayerId} colocó un plano de {req.DefName} en {cell}.");

                // El plano se coloca igual (como en el juego normal), pero se avisa a B si falta material.
                var missing = new List<string>();
                foreach (var cost in def.CostListAdjusted(stuff))
                {
                    int have = map.resourceCounter.GetCount(cost.thingDef);
                    if (have < cost.count) missing.Add($"{cost.thingDef.label} ({have}/{cost.count})");
                }
                if (missing.Count > 0)
                    CoopClient.Instance.SendJoinResult(req.FromPlayerId, false,
                        $"Plano de {def.label} colocado, pero a la base le falta material: {string.Join(", ", missing)}.");
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error al colocar el plano de {req.DefName}: {e.Message}");
                CoopClient.Instance.SendJoinResult(req.FromPlayerId, false, "Error al colocar el plano en la base del dueño.");
            }
        }

        // Acciones especiales que viajan como BuildRequest (DefName reservado, X/Z = celda).
        public const string BuildActionDeconstruct = "@deconstruct";
        public const string BuildActionCancel = "@cancel";

        private void HandleRemoteRemoval(BuildRequestPayload req, Map map)
        {
            var cell = new IntVec3(req.X, 0, req.Z);
            if (!cell.InBounds(map)) return;

            bool cancel = req.DefName == BuildActionCancel;
            try
            {
                foreach (Thing t in cell.GetThingList(map).ToList())
                {
                    if (cancel)
                    {
                        if (t is Blueprint || t is Frame)
                        {
                            t.Destroy(DestroyMode.Cancel);
                            CoopLog.Message($"[RimCoop] Jugador {req.FromPlayerId} canceló {t.def.defName} en {cell}.");
                        }
                    }
                    else if (t is Building b && b.Faction == Faction.OfPlayer && b.def.building != null && b.def.building.IsDeconstructible)
                    {
                        if (map.designationManager.DesignationOn(b, DesignationDefOf.Deconstruct) == null)
                            map.designationManager.AddDesignation(new Designation(b, DesignationDefOf.Deconstruct));
                        CoopLog.Message($"[RimCoop] Jugador {req.FromPlayerId} marcó {b.def.defName} para desmontar en {cell}.");
                    }
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error en {req.DefName} en {cell}: {e.Message}");
            }
        }

        public const string ZoneAddPrefix = "@zoneadd:";
        public const string ZoneDelete = "@zonedel";

        private void HandleRemoteZone(BuildRequestPayload req, Map map)
        {
            var cells = ParseCells(req.StuffDefName).Where(c => c.InBounds(map)).ToList();
            if (cells.Count == 0) return;

            var prevMap = Current.Game.CurrentMap;
            try
            {
                // Los designators trabajan sobre Find.CurrentMap, así que lo apuntamos un instante a mi mapa real.
                Current.Game.CurrentMap = map;
                if (req.DefName == ZoneDelete)
                {
                    var del = new Designator_ZoneDelete();
                    foreach (var c in cells) if (del.CanDesignateCell(c).Accepted) del.DesignateSingleCell(c);
                }
                else
                {
                    var type = typeof(Designator_ZoneAdd).Assembly.GetType(req.DefName.Substring(ZoneAddPrefix.Length));
                    if (type == null || !type.IsSubclassOf(typeof(Designator_ZoneAdd)) || type.IsAbstract) return;
                    var add = (Designator_ZoneAdd)Activator.CreateInstance(type);
                    add.DesignateMultiCell(cells);
                }
                CoopLog.Message($"[RimCoop] Jugador {req.FromPlayerId} pidió {req.DefName} sobre {cells.Count} celda(s).");
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error aplicando {req.DefName}: {e.Message}");
                CoopClient.Instance.SendJoinResult(req.FromPlayerId, false, "No se pudo aplicar la zona en la base del dueño.");
            }
            finally
            {
                Current.Game.CurrentMap = prevMap;
            }
        }

        public static string CellsToCsv(IEnumerable<IntVec3> cells) => string.Join(";", cells.Select(c => c.x + "," + c.z));

        public static List<IntVec3> ParseCells(string csv)
        {
            var list = new List<IntVec3>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (var part in csv.Split(';'))
            {
                var xz = part.Split(',');
                if (xz.Length == 2 && int.TryParse(xz[0], out int x) && int.TryParse(xz[1], out int z)) list.Add(new IntVec3(x, 0, z));
            }
            return list;
        }

        /// <summary>Lado espejo: pide al dueño desmontar/cancelar lo que hay en una celda.</summary>
        public static void SendRemovalRequest(int hostPlayerId, string action, IntVec3 cell)
        {
            CoopClient.Instance.SendBuildRequest(hostPlayerId, action, null, cell.x, cell.z, 0);
        }

        /// <summary>
        /// Alguien me pidió cómo se ve UNO de mis pawns (soy el dueño real). Se lo mando una sola
        /// vez por par (jugador, pawn) — es una operación pesada (serializar el pawn entero), así
        /// que no tiene sentido repetirla si ya se la mandamos.
        /// </summary>
        private void HandlePawnAppearanceRequest(PawnAppearanceRequestPayload req)
        {
            if (!_sentAppearances.TryGetValue(req.FromPlayerId, out var sent))
            {
                sent = new HashSet<int>();
                _sentAppearances[req.FromPlayerId] = sent;
            }
            if (!sent.Add(req.PawnId)) return;

            var map = Find.AnyPlayerHomeMap;
            Pawn pawn = map?.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == req.PawnId);
            if (pawn == null) return;

            string xml = PawnTransfer.SerializePawn(pawn);
            if (string.IsNullOrEmpty(xml)) return;

            CoopClient.Instance.SendPawnAppearance(new PawnAppearancePayload
            {
                HostPlayerId = CoopClient.Instance.LocalPlayerId,
                ToPlayerId = req.FromPlayerId,
                PawnId = req.PawnId,
                SerializedPawn = xml
            });
        }

        public static void CleanupSavedMirrorState()
        {
            try
            {
                foreach (var wo in Find.WorldObjects.AllWorldObjects.OfType<CoopPlayerBase>().ToList())
                {
                    if (wo.HasMap)
                    {
                        try { Current.Game.DeinitAndRemoveMap(wo.Map, false); }
                        catch (Exception e) { CoopLog.Warning($"[RimCoop] No se pudo quitar el mapa espejo guardado: {e.Message}"); }
                    }
                    Find.WorldObjects.Remove(wo);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error limpiando bases espejo guardadas: {e.Message}");
            }
        }

        // Fuera de una partida en curso (elegir dónde fundar la colonia) GameComponentUpdate no corre;
        // el CoopUpdater llama a esto para que igual se procese lo que hace falta para ver las bases de los demás.
        public static void ProcessPacketsOutsidePlay()
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || Current.Game.World == null || Current.CreatingWorld != null) return;

            while (CoopClient.Instance.IncomingPackets.TryPeek(out var peeked))
            {
                var t = peeked.Type;
                bool safe = t == PacketType.WorldData || t == PacketType.PlayerJoined || t == PacketType.PlayerLeft || t == PacketType.PlayerUpdate || t == PacketType.Chat;
                if (!safe || !CoopClient.Instance.IncomingPackets.TryDequeue(out var p)) break;

                try { instance.HandlePacket(p); }
                catch (Exception e) { CoopLog.Warning($"[RimCoop] Error procesando {p.Type} fuera de partida: {e.Message}"); }
            }
        }

        private void EnsureRemoteBase(PlayerBaseInfo info)
        {
            if (info.Tile < 0) return; // todavía no se asentó en el mapa mundial
            if (string.IsNullOrEmpty(info.PlayerName)) return;
            if (Current.Game?.World?.worldObjects == null) return; // el mundo todavía no existe: el próximo PlayersRequest lo trae de nuevo

            if (!_remoteBases.TryGetValue(info.PlayerName, out var wobj))
            {
                wobj = (CoopPlayerBase)WorldObjectMaker.MakeWorldObject(
                    DefDatabase<WorldObjectDef>.GetNamed("RimCoop_CoopPlayerBase"));
                wobj.Tile = info.Tile;
                wobj.RemotePlayerId = info.PlayerId;
                wobj.RemotePlayerName = info.PlayerName;
                wobj.ColonistCount = info.ColonistCount;
                wobj.Wealth = info.Wealth;

                Find.WorldObjects.Add(wobj);
                _remoteBases[info.PlayerName] = wobj;
            }
            else
            {
                wobj.Tile = info.Tile;
                wobj.ColonistCount = info.ColonistCount;
                wobj.Wealth = info.Wealth;

                // Si el server se reinició, el id de este jugador puede haber cambiado
                // desde la última vez que se guardó/generó este WorldObject. Nos autocorregimos.
                if (wobj.RemotePlayerId != info.PlayerId)
                {
                    CoopLog.Message($"[RimCoop] Corrigiendo id de {info.PlayerName}: {wobj.RemotePlayerId} -> {info.PlayerId}");
                    wobj.RemotePlayerId = info.PlayerId;
                }
            }
        }

        /// <summary>
        /// Llamar cuando el colono local funda su asentamiento, para avisarle al servidor
        /// (y de ahí a todos los demás clientes) en qué tile está. Guarda el tile localmente
        /// para que GameComponentTick pueda seguir avisando cuando cambie el conteo de colonos.
        /// </summary>
        public static void NotifyLocalSettlement(int tile, int colonistCount)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance != null)
            {
                instance._localTile = tile;
                instance._lastSentColonistCount = colonistCount;
            }

            instance?.UpdateLocalWealth(force: true);
            CoopClient.Instance.SendUpdate(tile, colonistCount);
        }
    }
}
