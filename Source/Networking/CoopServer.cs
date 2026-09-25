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
            _port = port;
            if (dataFolder != null) SaveName = new DirectoryInfo(dataFolder).Name;

            LoadPlayerIds();
            LoadBans();

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

            CoopLog.Message($"[RimCoop] Servidor iniciado en puerto {port}. Seed del mundo: {seed}");
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
                    CoopLog.Warning($"[RimCoop] Un cliente con versión de protocolo {hs.ProtocolVersion} (el servidor usa {ProtocolInfo.Version}) intentó conectarse: se lo rechaza. Tiene que usar la misma versión del mod.");
                    NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.Chat, new ChatPayload
                    {
                        PlayerId = 0, PlayerName = "Servidor",
                        Message = $"Versión del mod incompatible (tu protocolo: {hs.ProtocolVersion}, servidor: {ProtocolInfo.Version}). Actualizá el mod y el servidor a la misma versión."
                    }));
                    handle.TcpClient.Close();
                    return;
                }
                string playerName = string.IsNullOrEmpty(hs.PlayerName) ? $"Jugador{_nextPlayerId}" : hs.PlayerName;

                if (IsBanned(playerName))
                {
                    CoopLog.Warning($"[RimCoop] {playerName} está baneado: se rechaza su conexión.");
                    NetIO.SendPacket(handle.Stream, Packet.Create(PacketType.Chat, new ChatPayload
                    {
                        PlayerId = 0, PlayerName = "Servidor",
                        Message = "Estás baneado de este servidor."
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

                CoopLog.Message($"[RimCoop] {handle.PlayerName} (id {handle.PlayerId}) se conectó.");

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
                CoopLog.Message($"[RimCoop] Error con cliente {handle.PlayerName}: {e.Message}");
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
                    CoopLog.Message($"[RimCoop] {handle.PlayerName} se desconectó.");
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

                case PacketType.JoinRequest:
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
            DisconnectWithMessage(target, string.IsNullOrWhiteSpace(reason) ? "Te sacaron del servidor." : "Te sacaron del servidor: " + reason);
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
                DisconnectWithMessage(target, string.IsNullOrWhiteSpace(reason) ? "Te banearon del servidor." : "Te banearon del servidor: " + reason);
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
                if (_bannedNames.Count > 0) CoopLog.Message($"[RimCoop] Se cargaron {_bannedNames.Count} jugador(es) baneado(s) desde {_bansPath}");
            }
            catch (Exception e)
            {
                CoopLog.Warning("[RimCoop] No se pudo cargar la lista de baneados: " + e.Message);
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
                CoopLog.Warning("[RimCoop] No se pudo guardar la lista de baneados: " + e.Message);
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

                CoopLog.Message($"[RimCoop] Se cargaron {_nameToId.Count} id(s) de jugador persistidos desde {_playerIdsPath}");
            }
            catch (Exception e)
            {
                CoopLog.Warning("[RimCoop] No se pudieron cargar los ids persistidos de jugador: " + e.Message);
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
                CoopLog.Warning("[RimCoop] No se pudo guardar el id de jugador: " + e.Message);
            }
        }

        private void RouteTo(int toPlayerId, Packet packet)
        {
            if (_clients.TryGetValue(toPlayerId, out var target))
            {
                try { NetIO.SendPacket(target.Stream, packet); }
                catch (Exception e) { CoopLog.Warning($"[RimCoop] Error reenviando {packet.Type} a jugador {toPlayerId}: {e.Message}"); }
            }
            else
            {
                CoopLog.Warning($"[RimCoop] No pude rutear {packet.Type}: no hay ningún jugador conectado con id {toPlayerId}.");
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
