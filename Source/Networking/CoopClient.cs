using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace RimCoopMod.Networking
{
    /// <summary>
    /// Vive en cada instancia de juego (incluso la del host, si juega). Se conecta
    /// al CoopServer, recibe el mundo y encola los paquetes entrantes para que
    /// CoopSessionManager los procese en el hilo principal (Tick/Update).
    /// </summary>
    public class CoopClient
    {
        public static CoopClient Instance = new CoopClient();

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private Thread _readThread;

        public bool IsConnected { get; private set; }
        public int LocalPlayerId { get; private set; }
        public string LocalPlayerName { get; private set; }
        public string LastError { get; private set; }

        // Datos del mundo recibidos del servidor en el WorldData packet.
        public string LastKnownSeed { get; private set; }
        public float LastKnownCoverage { get; private set; } = 0.3f;

        // Cola thread-safe: el hilo de red escribe, el hilo principal del juego lee.
        public readonly ConcurrentQueue<Packet> IncomingPackets = new ConcurrentQueue<Packet>();

        public void Connect(string ip, int port, string playerName)
        {
            LocalPlayerName = playerName;
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.Connect(ip, port); // bloqueante; llamar desde un hilo aparte si querés no trabar la UI
                _stream = _tcpClient.GetStream();

                NetIO.SendPacket(_stream, Packet.Create(PacketType.Handshake, new HandshakePayload { PlayerName = playerName }));

                _readThread = new Thread(ReadLoop) { IsBackground = true };
                _readThread.Start();

                IsConnected = true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                IsConnected = false;
                CoopLog.Error($"[RimCoop] Error al conectar a {ip}:{port} -> {e.Message}");
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (IsConnected)
                {
                    Packet p = NetIO.ReadPacket(_stream);
                    if (p == null) break;

                    if (p.Type == PacketType.WorldData)
                    {
                        var wd = p.GetPayload<WorldDataPayload>();
                        LocalPlayerId = wd.AssignedPlayerId;
                        LastKnownSeed = wd.Seed;
                        LastKnownCoverage = wd.PlanetCoverage;
                    }

                    IncomingPackets.Enqueue(p);
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Conexión perdida: {e.Message}");
            }
            finally
            {
                IsConnected = false;
            }
        }

        public void SendUpdate(int tile, int colonistCount)
        {
            if (!IsConnected) return;
            var payload = new PlayerUpdatePayload
            {
                PlayerId = LocalPlayerId,
                PlayerName = LocalPlayerName,
                Tile = tile,
                ColonistCount = colonistCount
            };
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PlayerUpdate, payload));
        }

        public void SendChat(string message)
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(message)) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.Chat, new ChatPayload
            {
                PlayerId = LocalPlayerId,
                PlayerName = LocalPlayerName,
                Message = message
            }));
        }

        public void SendTradeRequest(int toPlayerId)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.TradeRequest, new TradeOrAttackPayload
            {
                FromPlayerId = LocalPlayerId,
                FromPlayerName = LocalPlayerName,
                ToPlayerId = toPlayerId
            }));
        }

        public void SendAttackRequest(int toPlayerId, int points = 300)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.AttackRequest, new TradeOrAttackPayload
            {
                FromPlayerId = LocalPlayerId,
                FromPlayerName = LocalPlayerName,
                ToPlayerId = toPlayerId,
                Points = points
            }));
        }

        public void SendTradeMessage(int toPlayerId, string kind, string data, int offerId = 0)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.TradeMessage, new TradeMessagePayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                FromPlayerName = LocalPlayerName,
                Kind = kind,
                OfferId = offerId,
                Data = data
            }));
        }

        // ---- Colaborar / mapa compartido en vivo ----

        public void SendWatchRequest(int hostPlayerId)
        {
            if (!IsConnected) { CoopLog.Warning("[RimCoop] SendWatchRequest ignorado: no conectado."); return; }
            NetIO.SendPacket(_stream, Packet.Create(PacketType.WatchRequest, new WatchRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId
            }));
            CoopLog.Message($"[RimCoop] WatchRequest enviado a jugador {hostPlayerId}.");
        }

        public void SendUnwatchRequest(int hostPlayerId)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.UnwatchRequest, new WatchRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId
            }));
        }

        public void SendMapSnapshot(MapSnapshotPayload payload)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.MapSnapshot, payload));
        }

        public void SendPawnOrder(int hostPlayerId, int pawnId, PawnOrderPayload payload)
        {
            if (!IsConnected) { CoopLog.Warning("[RimCoop] SendPawnOrder ignorado: no conectado."); return; }
            payload.FromPlayerId = LocalPlayerId;
            payload.ToPlayerId = hostPlayerId;
            payload.PawnId = pawnId;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PawnOrder, payload));
        }

        public void SendJoinRequest(int hostPlayerId, System.Collections.Generic.List<string> serializedPawns)
        {
            if (!IsConnected)
            {
                CoopLog.Warning($"[RimCoop] SendJoinRequest ignorado: no conectado (LocalPlayerId={LocalPlayerId}).");
                return;
            }

            NetIO.SendPacket(_stream, Packet.Create(PacketType.JoinRequest, new JoinRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                SerializedPawns = serializedPawns
            }));
            CoopLog.Message($"[RimCoop] JoinRequest enviado a jugador {hostPlayerId} con {serializedPawns.Count} colono(s) (yo soy el jugador {LocalPlayerId}).");
        }

        public void SendJoinResult(int toPlayerId, bool success, string message)
        {
            if (!IsConnected) { CoopLog.Warning("[RimCoop] SendJoinResult ignorado: no conectado."); return; }
            NetIO.SendPacket(_stream, Packet.Create(PacketType.JoinResult, new JoinResultPayload
            {
                ToPlayerId = toPlayerId,
                Success = success,
                Message = message
            }));
            CoopLog.Message($"[RimCoop] JoinResult enviado a jugador {toPlayerId}: éxito={success} - {message}");
        }

        public void SendBaseSnapshotRequest(int hostPlayerId)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.BaseSnapshotRequest, new BaseSnapshotRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId
            }));
        }

        public void SendBaseSnapshot(BaseSnapshotPayload payload)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.BaseSnapshot, payload));
        }

        public void SendPawnAppearanceRequest(int hostPlayerId, int pawnId)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PawnAppearanceRequest, new PawnAppearanceRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                PawnId = pawnId
            }));
        }

        public void SendPawnAppearance(PawnAppearancePayload payload)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PawnAppearance, payload));
        }

        // ---- Votación para pausar/despausar ----

        public void SendPauseVoteRequest(bool proposePause)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PauseVoteRequest, new PauseVoteRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                FromPlayerName = LocalPlayerName,
                ProposePause = proposePause
            }));
        }

        public void SendPauseVoteResponse(int toPlayerId, bool accept)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PauseVoteResponse, new PauseVoteResponsePayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                Accept = accept
            }));
        }

        public void SendPauseVoteResult(bool approved, bool proposePause)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PauseVoteResult, new PauseVoteResultPayload
            {
                Approved = approved,
                ProposePause = proposePause
            }));
        }

        public void SendBuildRequest(int hostPlayerId, string defName, string stuffDefName, int x, int z, int rotation)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.BuildRequest, new BuildRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                DefName = defName,
                StuffDefName = stuffDefName,
                X = x,
                Z = z,
                Rotation = rotation
            }));
        }

        public void SendEventNotice(int toPlayerId, string letterDefName, string label, string text)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.EventNotice, new EventNoticePayload
            {
                ToPlayerId = toPlayerId,
                FromPlayerId = LocalPlayerId,
                LetterDefName = letterDefName,
                Label = label,
                Text = text
            }));
        }

        public void SendPawnSetting(int hostPlayerId, int pawnId, string kind, string key, string value)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PawnSettingRequest, new PawnSettingPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                PawnId = pawnId,
                Kind = kind,
                Key = key,
                Value = value
            }));
        }

        public void SendPlayersRequest()
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PlayersRequest, new WatchRequestPayload { FromPlayerId = LocalPlayerId, ToPlayerId = 0 }));
        }

        public void SendSpeedChange(int speed)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.SpeedChange, new SpeedChangePayload { FromPlayerId = LocalPlayerId, Speed = speed }));
        }

        public void Disconnect()
        {
            IsConnected = false;
            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }
        }
    }
}
