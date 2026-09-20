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
        private static readonly string PlayerIdsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData");
        private static readonly string PlayerIdsPath = Path.Combine(PlayerIdsFolder, "player_ids.txt");

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

        public void Start(int port, string seed, float coverage, string rainfall, string temperature, string population)
        {
            WorldSeed = seed;
            PlanetCoverage = coverage;
            OverallRainfall = rainfall;
            OverallTemperature = temperature;
            OverallPopulation = population;

            LoadPlayerIds();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsRunning = true;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();

            CoopLog.Message($"[RimCoop] Servidor iniciado en puerto {port}. Seed del mundo: {seed}");
        }

        public void Stop()
        {
            IsRunning = false;
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
            try
            {
                // 1. Esperar handshake
                Packet first = NetIO.ReadPacket(handle.Stream);
                if (first == null || first.Type != PacketType.Handshake)
                {
                    handle.TcpClient.Close();
                    return;
                }

                var hs = first.GetPayload<HandshakePayload>();
                string playerName = string.IsNullOrEmpty(hs.PlayerName) ? $"Jugador{_nextPlayerId}" : hs.PlayerName;

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
                _clients.TryRemove(handle.PlayerId, out _);
                lock (_players) { _players.Remove(handle.PlayerId); }
                Broadcast(Packet.Create(PacketType.PlayerLeft, new PlayerBaseInfo { PlayerId = handle.PlayerId, PlayerName = handle.PlayerName }));
                try { handle.TcpClient.Close(); } catch { }
                CoopLog.Message($"[RimCoop] {handle.PlayerName} se desconectó.");
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
                                ColonistCount = info.ColonistCount
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

        private void LoadPlayerIds()
        {
            try
            {
                if (!File.Exists(PlayerIdsPath)) return;

                foreach (var line in File.ReadAllLines(PlayerIdsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2 || !int.TryParse(parts[1], out int id)) continue;

                    _nameToId[parts[0]] = id;
                    if (id >= _nextPlayerId) _nextPlayerId = id + 1;
                }

                CoopLog.Message($"[RimCoop] Se cargaron {_nameToId.Count} id(s) de jugador persistidos desde {PlayerIdsPath}");
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
                Directory.CreateDirectory(PlayerIdsFolder);
                File.WriteAllLines(PlayerIdsPath, _nameToId.Select(kv => $"{kv.Key}={kv.Value}"));
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
