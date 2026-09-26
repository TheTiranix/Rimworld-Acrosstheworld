using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;


namespace RimCoopMod.Networking
{
    /// <summary>
    /// Corre en el proceso que actúa como servidor (puede ser el mismo juego del host,
    /// jugando en paralelo, o un proceso dedicado que solo hostea).
    /// Es dueño de la "verdad" del mundo: la seed y la lista de jugadores/bases.
    /// </summary>
    public class CoopServer
    {
        public static CoopServer Instance;

        private TcpListener _listener;
        private Thread _acceptThread;
        private readonly ConcurrentDictionary<int, ClientHandle> _clients = new ConcurrentDictionary<int, ClientHandle>();
        private int _nextPlayerId = 1;

        // Nombre -> id asignado. Se persiste en disco para que el id de cada jugador sea
        // estable entre reinicios del servidor (si no, un CoopPlayerBase guardado en una
        // partida vieja queda apuntando a un id que ya no existe).
        private readonly Dictionary<string, int> _nameToId = new Dictionary<string, int>();
        private string _playerIdsPath;

        // Jugadores baneados por nombre (los ids también son por nombre, así que es lo estable). Se persiste
        // junto a player_ids.txt, en la carpeta de la partida.
        private readonly HashSet<string> _bannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _bansPath;

        // Registro de colonos enviados entre jugadores: quién tiene cada uno ahora y una copia de cuando se mandó. Es lo único que
        // sobrevive a que un jugador cargue una partida vieja (ver ColonistManifest): con esto se detecta el colono que quedó
        // duplicado (lo tiene en casa y ya estaba en otro lado) o perdido (el otro cargó antes de recibirlo) y se corrige solo.
        private class ColonistRecord
        {
            public string Uid;
            public string OwnerName;
            public string HolderName;
            public string Blob;
        }

        private readonly Dictionary<string, ColonistRecord> _colonists = new Dictionary<string, ColonistRecord>();
        private string _colonistsPath;

        private LanDiscovery.Responder _lanResponder;
        private int _port;
        public string SaveName { get; private set; } = "default";

        // Datos del mundo que el servidor decide y reparte a todos.
        public string WorldSeed;
        public float PlanetCoverage;
        public string OverallRainfall;
        public string OverallTemperature;
        public string OverallPopulation;

        private readonly Dictionary<int, PlayerBaseInfo> _players = new Dictionary<int, PlayerBaseInfo>();

        public bool IsRunning { get; private set; }

        private class ClientHandle
        {
            public int PlayerId;
            public string PlayerName;
            public TcpClient TcpClient;
            public NetworkStream Stream;
            public Thread ReadThread;
        }

        /// <summary>
        /// dataFolder: dónde persistir player_ids.txt (id estable de cada jugador). Cada "partida"
        /// del servidor dedicado usa su propia carpeta (ver ServerConfig) para no mezclar ids entre
        /// mundos distintos; si no se especifica, se usa la carpeta de siempre junto al ejecutable
        /// (así el host-desde-el-juego, que no elige partida, sigue funcionando igual que antes).
        /// </summary>
        public void Start(int port, string seed, float coverage, string rainfall, string temperature, string population, string dataFolder = null)
        {
            WorldSeed = seed;
            PlanetCoverage = coverage;
            OverallRainfall = rainfall;
            OverallTemperature = temperature;
            OverallPopulation = population;

            string folder = dataFolder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData");
            _playerIdsPath = Path.Combine(folder, "player_ids.txt");
            _bansPath = Path.Combine(folder, "banned_players.txt");
            _colonistsPath = Path.Combine(folder, "colonists.bin");
            _port = port;
            if (dataFolder != null) SaveName = new DirectoryInfo(dataFolder).Name;

            LoadPlayerIds();
            LoadBans();
            LoadColonists();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsRunning = true;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();

            // Responde a "¿hay servidores en la red local?" (lista LAN del mod, ver LanDiscovery).
            _lanResponder = new LanDiscovery.Responder(() => new LanServerInfo
            {
                ProtocolVersion = ProtocolInfo.Version,
                Port = _port,
                Players = _clients.Count,
                SaveName = SaveName,
                Seed = WorldSeed
            });
            _lanResponder.Start();

            CoopLog.Message(Loc.T("Server.01", port, seed));
        }

        public void Stop()
        {
            IsRunning = false;
            try { _lanResponder?.Stop(); } catch { }
            try { _listener?.Stop(); } catch { }
            foreach (var c in _clients.Values)
            {
                try { c.TcpClient.Close(); } catch { }
            }
            _clients.Clear();
        }

        private void AcceptLoop()
        {
            while (IsRunning)
            {
                TcpClient tcpClient;
                try
                {
                    tcpClient = _listener.AcceptTcpClient();
                }
                catch
                {
                    break; // listener cerrado
                }

                var handle = new ClientHandle
                {
                    TcpClient = tcpClient,
                    Stream = tcpClient.GetStream()
                };

                var thread = new Thread(() => ClientLoop(handle)) { IsBackground = true };
                handle.ReadThread = thread;
                thread.Start();
            }
        }

        private void ClientLoop(ClientHandle handle)
        {
            bool registered = false; // solo un jugador que llegó a entrar de verdad se anota y se avisa cuando se va
            try
            {
                // 1. Esperar handshake
                Packet first = NetIO.ReadPacket(handle.Stream);
                if (first == null)
                {
                    handle.TcpClient.Close();
                    return;
                }

                // Consulta corta para la lista de servidores (estilo Half-Life/CS): no es un jugador
                // uniéndose, solo quiere saber si el server responde y cuánta gente hay conectada.
                if (first.Type == PacketType.PingRequest)
                {
                    NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.PingResponse, new PingResponsePayload
                    {
                        ProtocolVersion = ProtocolInfo.Version,
                        ConnectedPlayers = _clients.Count,
                        WorldSeed = WorldSeed,
                        SaveName = SaveName
                    }));
                    handle.TcpClient.Close();
                    return;
                }

                if (first.Type != PacketType.Handshake)
                {
                    handle.TcpClient.Close();
                    return;
                }

                var hs = first.GetPayload<HandshakePayload>();

                // Lo primero: mi versión de protocolo. Un cliente nuevo la usa para saber si somos compatibles;
                // un servidor viejo nunca manda esto, y el cliente lo detecta justamente por eso.
                NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.ServerInfo, new ServerInfoPayload { ProtocolVersion = ProtocolInfo.Version }));
                if (hs.ProtocolVersion != ProtocolInfo.Version)
                {
                    CoopLog.Warning(Loc.T("Server.02", hs.ProtocolVersion, ProtocolInfo.Version));
                    NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.Chat, new ChatPayload
                    {
                        PlayerId = 0, PlayerName = "Servidor",
                        Message = Loc.Both("Server.03", hs.ProtocolVersion, ProtocolInfo.Version)
                    }));
                    handle.TcpClient.Close();
                    return;
                }
                string playerName = string.IsNullOrEmpty(hs.PlayerName) ? Loc.T("Common.Player") + _nextPlayerId : hs.PlayerName;

                if (IsBanned(playerName))
                {
                    CoopLog.Warning(Loc.T("Server.04", playerName));
                    NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.Chat, new ChatPayload
                    {
                        PlayerId = 0, PlayerName = "Servidor",
                        Message = Loc.Wire("Server.05")
                    }));
                    handle.TcpClient.Close();
                    return;
                }

                lock (_nameToId)
                {
                    if (!_nameToId.TryGetValue(playerName, out int playerId))
                    {
                        playerId = _nextPlayerId++;
                        _nameToId[playerName] = playerId;
                        SavePlayerIds();
                    }
                    handle.PlayerId = playerId;
                }
                handle.PlayerName = playerName;

                _clients[handle.PlayerId] = handle;
                registered = true;

                lock (_players)
                {
                    // Si este jugador ya se había conectado antes (mismo id, ahora estable por
                    // nombre), NO pisamos su tile/colonos conocidos con un registro en blanco —
                    // si no, cada reconexión (aunque sea un corte de red breve) borra su base del
                    // mapa mundial hasta que él vuelva a mandar una actualización por su cuenta.
                    if (_players.TryGetValue(handle.PlayerId, out var existing))
                    {
                        existing.PlayerName = handle.PlayerName;
                    }
                    else
                    {
                        _players[handle.PlayerId] = new PlayerBaseInfo
                        {
                            PlayerId = handle.PlayerId,
                            PlayerName = handle.PlayerName,
                            Tile = -1,
                            ColonistCount = 0
                        };
                    }
                }

                // 2. Enviar WorldData con el estado actual (seed + jugadores existentes)
                var worldData = new WorldDataPayload
                {
                    Seed = WorldSeed,
                    PlanetCoverage = PlanetCoverage,
                    OverallRainfall = OverallRainfall,
                    OverallTemperature = OverallTemperature,
                    OverallPopulation = OverallPopulation,
                    AssignedPlayerId = handle.PlayerId
                };
                lock (_players)
                {
                    worldData.ExistingPlayers.AddRange(_players.Values);
                }
                NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.WorldData, worldData));

                // 3. Avisar a los demás que se unió
                Broadcast(Packet.Create(PacketType.PlayerJoined, _players[handle.PlayerId]), excludePlayerId: handle.PlayerId);

                // Qué colonos le tocan y cuáles están en otro lado: con esto corrige duplicados o pérdidas de partidas viejas.
                SendColonistManifest(handle);

                CoopLog.Message(Loc.T("Server.06", handle.PlayerName, handle.PlayerId));

                // 4. Loop de recepción normal
                while (IsRunning)
                {
                    Packet p = NetIO.ReadPacket(handle.Stream);
                    if (p == null) break; // se desconectó

                    HandleClientPacket(handle, p);
                }
            }
            catch (Exception e)
            {
                CoopLog.Message(Loc.T("Server.07", handle.PlayerName, e.Message));
            }
            finally
            {
                try { handle.TcpClient.Close(); } catch { }

                // Antes esto corría también para pings y conexiones rechazadas (id 0, nombre nulo) y le mandaba a todos un
                // "PlayerLeft" fantasma. Y si el mismo jugador reconectaba con la conexión vieja todavía viva, la vieja
                // borraba a la NUEVA al cerrarse: solo se limpia si este handle sigue siendo el vigente.
                if (registered && _clients.TryGetValue(handle.PlayerId, out var current) && ReferenceEquals(current, handle))
                {
                    _clients.TryRemove(handle.PlayerId, out _);
                    lock (_players) { _players.Remove(handle.PlayerId); }
                    Broadcast(Packet.Create(PacketType.PlayerLeft, new PlayerBaseInfo { PlayerId = handle.PlayerId, PlayerName = handle.PlayerName }));
                    CoopLog.Message(Loc.T("Server.08", handle.PlayerName));
                }
            }
        }

        private void HandleClientPacket(ClientHandle from, Packet p)
        {
            switch (p.Type)
            {
                case PacketType.PlayerUpdate:
                    var update = p.GetPayload<PlayerUpdatePayload>();
                    lock (_players)
                    {
                        if (_players.TryGetValue(from.PlayerId, out var info))
                        {
                            info.Tile = update.Tile;
                            info.ColonistCount = update.ColonistCount;
                            info.Wealth = update.Wealth;
                        }
                    }
                    // Retransmitir a todos los demás
                    Broadcast(p, excludePlayerId: from.PlayerId);
                    break;

                case PacketType.TradeRequest:
                case PacketType.AttackRequest:
                    var tradeOrAttack = p.GetPayload<TradeOrAttackPayload>();
                    // Reenviar solo al jugador destino
                    if (_clients.TryGetValue(tradeOrAttack.ToPlayerId, out var target))
                    {
                        NetIO.SendPacket(target.Stream, p);
                    }
                    break;

                case PacketType.Chat:
                    Broadcast(p, excludePlayerId: from.PlayerId);
                    break;

                // ---- Colaborar / mapa compartido en vivo: siempre punto a punto, el server solo reenvía ----
                case PacketType.WatchRequest:
                case PacketType.UnwatchRequest:
                    RouteTo(p.GetPayload<WatchRequestPayload>().ToPlayerId, p);
                    break;

                case PacketType.MapSnapshot:
                    RouteTo(p.GetPayload<MapSnapshotPayload>().ToPlayerId, p);
                    break;

                case PacketType.PawnOrder:
                    RouteTo(p.GetPayload<PawnOrderPayload>().ToPlayerId, p);
                    break;

                case PacketType.ColonistGone:
                    RemoveColonists(from, p.GetPayload<ColonistGonePayload>());
                    break;

                case PacketType.JoinRequest:
                    RecordTransfers(from, p.GetPayload<JoinRequestPayload>());
                    RouteTo(p.GetPayload<JoinRequestPayload>().ToPlayerId, p);
                    break;

                case PacketType.JoinResult:
                    RouteTo(p.GetPayload<JoinResultPayload>().ToPlayerId, p);
                    break;

                case PacketType.BaseSnapshotRequest:
                    RouteTo(p.GetPayload<BaseSnapshotRequestPayload>().ToPlayerId, p);
                    break;

                case PacketType.BaseSnapshot:
                    RouteTo(p.GetPayload<BaseSnapshotPayload>().ToPlayerId, p);
                    break;

                case PacketType.PawnAppearanceRequest:
                    RouteTo(p.GetPayload<PawnAppearanceRequestPayload>().ToPlayerId, p);
                    break;

                case PacketType.PawnAppearance:
                    RouteTo(p.GetPayload<PawnAppearancePayload>().ToPlayerId, p);
                    break;

                case PacketType.PauseVoteRequest:
                case PacketType.PauseVoteResult:
                    Broadcast(p, excludePlayerId: from.PlayerId);
                    break;

                case PacketType.PauseVoteResponse:
                    RouteTo(p.GetPayload<PauseVoteResponsePayload>().ToPlayerId, p);
                    break;

                case PacketType.BuildRequest:
                    RouteTo(p.GetPayload<BuildRequestPayload>().ToPlayerId, p);
                    break;

                case PacketType.EventNotice:
                    RouteTo(p.GetPayload<EventNoticePayload>().ToPlayerId, p);
                    break;

                case PacketType.ResearchSync:
                    RouteTo(p.GetPayload<ResearchSyncPayload>().ToPlayerId, p);
                    break;

                case PacketType.WorldEvent:
                    RouteTo(p.GetPayload<WorldEventPayload>().ToPlayerId, p);
                    break;

                case PacketType.ShipMessage:
                    RouteTo(p.GetPayload<ShipMessagePayload>().ToPlayerId, p);
                    break;

                case PacketType.SaveAll:
                    Broadcast(p, excludePlayerId: from.PlayerId);
                    break;

                case PacketType.QuestMessage:
                    RouteTo(p.GetPayload<QuestMessagePayload>().ToPlayerId, p);
                    break;

                case PacketType.ModList:
                    {
                        var ml = p.GetPayload<ModListPayload>();
                        if (ml.ToPlayerId > 0) RouteTo(ml.ToPlayerId, p);
                        else Broadcast(p, excludePlayerId: from.PlayerId);
                        break;
                    }

                case PacketType.TradeMessage:
                    RouteTo(p.GetPayload<TradeMessagePayload>().ToPlayerId, p);
                    break;

                case PacketType.PlayersRequest:
                    lock (_players)
                    {
                        foreach (var info in _players.Values)
                        {
                            if (info.PlayerId == from.PlayerId || info.Tile < 0) continue;
                            NetIO.SendPacket(from.Stream, Packet.Create(PacketType.PlayerUpdate, new PlayerUpdatePayload
                            {
                                PlayerId = info.PlayerId,
                                PlayerName = info.PlayerName,
                                Tile = info.Tile,
                                ColonistCount = info.ColonistCount,
                                Wealth = info.Wealth
                            }));
                        }
                    }
                    break;

                case PacketType.SpeedChange:
                    Broadcast(p, excludePlayerId: from.PlayerId);
                    break;

                case PacketType.PawnSettingRequest:
                    RouteTo(p.GetPayload<PawnSettingPayload>().ToPlayerId, p);
                    break;
            }
        }

        // ---- Registro de colonos ----

        private string NameOfPlayer(int playerId)
        {
            if (_clients.TryGetValue(playerId, out var c)) return c.PlayerName;
            lock (_nameToId)
            {
                foreach (var kv in _nameToId) if (kv.Value == playerId) return kv.Key;
            }
            return null;
        }

        // Un colono viajó: queda anotado quién lo tiene ahora (aunque el destino esté desconectado, lo recibe al entrar) y su copia.
        private void RecordTransfers(ClientHandle from, JoinRequestPayload req)
        {
            if (req?.Uids == null || req.Uids.Count == 0) return;
            string holder = NameOfPlayer(req.ToPlayerId);
            if (string.IsNullOrEmpty(holder)) return;

            lock (_colonists)
            {
                for (int i = 0; i < req.Uids.Count && i < req.SerializedPawns.Count; i++)
                {
                    string uid = req.Uids[i];
                    if (string.IsNullOrEmpty(uid)) continue;
                    string owner = (req.OwnerNames != null && i < req.OwnerNames.Count && !string.IsNullOrEmpty(req.OwnerNames[i])) ? req.OwnerNames[i] : from.PlayerName;
                    _colonists[uid] = new ColonistRecord { Uid = uid, OwnerName = owner, HolderName = holder, Blob = req.SerializedPawns[i] };
                }
                SaveColonists();
            }
        }

        // Solo lo da de baja quien lo tiene: si un jugador con una copia vieja avisara que "murió", no puede borrar el registro del que sigue vivo en otro lado.
        private void RemoveColonists(ClientHandle from, ColonistGonePayload gone)
        {
            if (gone?.Uids == null || gone.Uids.Count == 0) return;
            lock (_colonists)
            {
                bool changed = false;
                foreach (var uid in gone.Uids)
                {
                    if (_colonists.TryGetValue(uid, out var rec) && string.Equals(rec.HolderName, from.PlayerName, StringComparison.OrdinalIgnoreCase))
                    {
                        _colonists.Remove(uid);
                        changed = true;
                    }
                }
                if (changed) SaveColonists();
            }
        }

        private void SendColonistManifest(ClientHandle handle)
        {
            var manifest = new ColonistManifestPayload();
            lock (_colonists)
            {
                foreach (var rec in _colonists.Values)
                {
                    if (string.Equals(rec.HolderName, handle.PlayerName, StringComparison.OrdinalIgnoreCase))
                        manifest.Hold.Add(new ColonistManifestEntry { Uid = rec.Uid, OwnerName = rec.OwnerName, Blob = rec.Blob });
                    else
                        manifest.Elsewhere.Add(rec.Uid);
                }
            }
            if (manifest.Hold.Count == 0 && manifest.Elsewhere.Count == 0) return;
            try { NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.ColonistManifest, manifest)); }
            catch (Exception e) { CoopLog.Warning(Loc.T("Server.24", handle.PlayerName, e.Message)); }
        }

        public class ColonistListEntry
        {
            public string Uid;
            public string Owner;
            public string Holder;
            public int BlobBytes;
        }

        public List<ColonistListEntry> GetRegisteredColonists()
        {
            lock (_colonists)
            {
                return _colonists.Values.OrderBy(r => r.OwnerName).ThenBy(r => r.Uid)
                    .Select(r => new ColonistListEntry { Uid = r.Uid, Owner = r.OwnerName, Holder = r.HolderName, BlobBytes = (r.Blob ?? "").Length }).ToList();
            }
        }

        /// <summary>Borra un colono del registro (por si hace falta destrabar algo a mano desde la consola).</summary>
        public bool ForgetColonist(string uid)
        {
            lock (_colonists)
            {
                bool removed = _colonists.Remove(uid);
                if (removed) SaveColonists();
                return removed;
            }
        }

        private void LoadColonists()
        {
            try
            {
                if (!File.Exists(_colonistsPath)) return;
                using (var br = new BinaryReader(File.OpenRead(_colonistsPath)))
                {
                    int n = br.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        var rec = new ColonistRecord { Uid = br.ReadString(), OwnerName = br.ReadString(), HolderName = br.ReadString(), Blob = br.ReadString() };
                        _colonists[rec.Uid] = rec;
                    }
                }
                if (_colonists.Count > 0) CoopLog.Message(Loc.T("Server.09", _colonists.Count, _colonistsPath));
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("Server.10", e.Message));
            }
        }

        // Llamar siempre adentro de un lock(_colonists). Se escribe a un archivo temporal y se reemplaza: un corte a mitad no lo deja roto.
        private void SaveColonists()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_colonistsPath));
                string tmp = _colonistsPath + ".tmp";
                using (var bw = new BinaryWriter(File.Create(tmp)))
                {
                    bw.Write(_colonists.Count);
                    foreach (var rec in _colonists.Values)
                    {
                        bw.Write(rec.Uid ?? "");
                        bw.Write(rec.OwnerName ?? "");
                        bw.Write(rec.HolderName ?? "");
                        bw.Write(rec.Blob ?? "");
                    }
                }
                if (File.Exists(_colonistsPath)) File.Delete(_colonistsPath);
                File.Move(tmp, _colonistsPath);
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("Server.11", e.Message));
            }
        }

        // ---- Administración desde la consola del servidor ----

        public class PlayerListEntry
        {
            public int Id;
            public string Name;
            public int Tile;
            public int Colonists;
        }

        public List<PlayerListEntry> GetConnectedPlayers()
        {
            var list = new List<PlayerListEntry>();
            foreach (var c in _clients.Values)
            {
                int tile = -1, colonists = 0;
                lock (_players)
                {
                    if (_players.TryGetValue(c.PlayerId, out var info)) { tile = info.Tile; colonists = info.ColonistCount; }
                }
                list.Add(new PlayerListEntry { Id = c.PlayerId, Name = c.PlayerName, Tile = tile, Colonists = colonists });
            }
            return list.OrderBy(e => e.Id).ToList();
        }

        private ClientHandle FindClient(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;
            if (int.TryParse(nameOrId, out int id) && _clients.TryGetValue(id, out var byId)) return byId;
            return _clients.Values.FirstOrDefault(c => string.Equals(c.PlayerName, nameOrId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Saca a un jugador conectado (por nombre o id). Puede volver a entrar cuando quiera.</summary>
        public bool Kick(string nameOrId, string reason, out string kickedName)
        {
            kickedName = null;
            var target = FindClient(nameOrId);
            if (target == null) return false;

            kickedName = target.PlayerName;
            DisconnectWithMessage(target, string.IsNullOrWhiteSpace(reason) ? Loc.Wire("Server.12") : Loc.Wire("Server.13", reason));
            return true;
        }

        /// <summary>Banea por nombre: no puede volver a entrar hasta que se lo desbanee. Si está conectado, se lo saca.</summary>
        public bool Ban(string nameOrId, string reason, out string bannedName)
        {
            var target = FindClient(nameOrId);
            bannedName = target?.PlayerName ?? nameOrId?.Trim();
            if (string.IsNullOrEmpty(bannedName)) return false;

            lock (_bannedNames) { _bannedNames.Add(bannedName); SaveBans(); }
            if (target != null)
                DisconnectWithMessage(target, string.IsNullOrWhiteSpace(reason) ? Loc.Wire("Server.14") : Loc.Wire("Server.15", reason));
            return true;
        }

        public bool Unban(string name)
        {
            lock (_bannedNames)
            {
                bool removed = _bannedNames.Remove(name?.Trim() ?? "");
                if (removed) SaveBans();
                return removed;
            }
        }

        public bool IsBanned(string name)
        {
            lock (_bannedNames) { return _bannedNames.Contains(name ?? ""); }
        }

        public List<string> GetBans()
        {
            lock (_bannedNames) { return _bannedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
        }

        private void DisconnectWithMessage(ClientHandle target, string message)
        {
            try
            {
                NetIO.SendPacket(target.Stream, Packet.Create(PacketType.Chat, new ChatPayload { PlayerId = 0, PlayerName = "Servidor", Message = message }));
            }
            catch { }
            // Cerrar el socket hace fallar su ReadPacket: el finally de ClientLoop lo saca de la lista y avisa a los demás.
            try { target.TcpClient.Close(); } catch { }
        }

        private void LoadBans()
        {
            try
            {
                if (!File.Exists(_bansPath)) return;
                foreach (var line in File.ReadAllLines(_bansPath))
                    if (!string.IsNullOrWhiteSpace(line)) _bannedNames.Add(line.Trim());
                if (_bannedNames.Count > 0) CoopLog.Message(Loc.T("Server.16", _bannedNames.Count, _bansPath));
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("Server.17", e.Message));
            }
        }

        // Llamar siempre adentro de un lock(_bannedNames).
        private void SaveBans()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_bansPath));
                File.WriteAllLines(_bansPath, _bannedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("Server.18", e.Message));
            }
        }

        private void LoadPlayerIds()
        {
            try
            {
                if (!File.Exists(_playerIdsPath)) return;

                foreach (var line in File.ReadAllLines(_playerIdsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2 || !int.TryParse(parts[1], out int id)) continue;

                    _nameToId[parts[0]] = id;
                    if (id >= _nextPlayerId) _nextPlayerId = id + 1;
                }

                CoopLog.Message(Loc.T("Server.19", _nameToId.Count, _playerIdsPath));
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("Server.20", e.Message));
            }
        }

        // Llamar siempre adentro de un lock(_nameToId).
        private void SavePlayerIds()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_playerIdsPath));
                File.WriteAllLines(_playerIdsPath, _nameToId.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("Server.21", e.Message));
            }
        }

        private void RouteTo(int toPlayerId, Packet packet)
        {
            if (_clients.TryGetValue(toPlayerId, out var target))
            {
                try { NetIO.SendPacket(target.Stream, packet); }
                catch (Exception e) { CoopLog.Warning(Loc.T("Server.22", packet.Type, toPlayerId, e.Message)); }
            }
            else
            {
                CoopLog.Warning(Loc.T("Server.23", packet.Type, toPlayerId));
            }
        }

        private void Broadcast(Packet packet, int excludePlayerId = -1)
        {
            foreach (var kv in _clients)
            {
                if (kv.Key == excludePlayerId) continue;
                try { NetIO.SendPacket(kv.Value.Stream, packet); } catch { }
            }
        }
    }
}
