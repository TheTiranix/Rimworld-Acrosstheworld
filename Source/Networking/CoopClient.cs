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

        // Si el servidor y el mod no son de la misma versión, acá queda el motivo (lo muestra CoopUpdater en pantalla).
        public string VersionError;

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

                NetIO.SendPacket(_stream, Packet.Create(PacketType.Handshake, new HandshakePayload { PlayerName = playerName, ProtocolVersion = ProtocolInfo.Version }));

                _readThread = new Thread(ReadLoop) { IsBackground = true };
                _readThread.Start();

                IsConnected = true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                IsConnected = false;
                CoopLog.Error(Loc.T("Client.01", ip, port, e.Message));
            }
        }

        private void ReadLoop()
        {
            bool gotServerInfo = false;
            try
            {
                while (IsConnected)
                {
                    Packet p = NetIO.ReadPacket(_stream);
                    if (p == null) break;

                    if (p.Type == PacketType.ServerInfo)
                    {
                        int serverVersion = p.GetPayload<ServerInfoPayload>().ProtocolVersion;
                        gotServerInfo = true;
                        if (serverVersion != ProtocolInfo.Version)
                        {
                            VersionError = Loc.T("Client.02", serverVersion, ProtocolInfo.Version);
                            CoopLog.Warning("[RimCoop] " + VersionError);
                            break;
                        }
                        continue;
                    }

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
                CoopLog.Warning(Loc.T("Client.03", e.Message));
                if (!gotServerInfo)
                    VersionError = Loc.T("Client.04");
            }
            finally
            {
                IsConnected = false;
            }
        }

        // Riqueza de mi colonia (la actualiza CoopSessionManager); viaja junto con cada aviso de posición.
        public int LocalWealth;

        public void SendUpdate(int tile, int colonistCount)
        {
            if (!IsConnected) return;
            var payload = new PlayerUpdatePayload
            {
                PlayerId = LocalPlayerId,
                PlayerName = LocalPlayerName,
                Tile = tile,
                ColonistCount = colonistCount,
                Wealth = LocalWealth
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
            if (!IsConnected) { CoopLog.Warning(Loc.T("Client.05")); return; }
            NetIO.SendPacket(_stream, Packet.Create(PacketType.WatchRequest, new WatchRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId
            }));
            CoopLog.Message(Loc.T("Client.06", hostPlayerId));
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
            if (!IsConnected) { CoopLog.Warning(Loc.T("Client.07")); return; }
            payload.FromPlayerId = LocalPlayerId;
            payload.ToPlayerId = hostPlayerId;
            payload.PawnId = pawnId;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.PawnOrder, payload));
        }

        public void SendColonistGone(System.Collections.Generic.List<string> uids)
        {
            if (!IsConnected || uids == null || uids.Count == 0) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.ColonistGone, new ColonistGonePayload { Uids = uids }));
        }

        public void SendJoinRequest(int hostPlayerId, System.Collections.Generic.List<string> serializedPawns, System.Collections.Generic.List<string> ownerNames = null, System.Collections.Generic.List<string> uids = null)
        {
            if (!IsConnected)
            {
                CoopLog.Warning(Loc.T("Client.08", LocalPlayerId));
                return;
            }

            NetIO.SendPacket(_stream, Packet.Create(PacketType.JoinRequest, new JoinRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                SerializedPawns = serializedPawns,
                OwnerNames = ownerNames ?? new System.Collections.Generic.List<string>(),
                Uids = uids ?? new System.Collections.Generic.List<string>()
            }));
            CoopLog.Message(Loc.T("Client.09", hostPlayerId, serializedPawns.Count, LocalPlayerId));
        }

        public void SendJoinResult(int toPlayerId, bool success, string message)
        {
            if (!IsConnected) { CoopLog.Warning(Loc.T("Client.10")); return; }
            NetIO.SendPacket(_stream, Packet.Create(PacketType.JoinResult, new JoinResultPayload
            {
                ToPlayerId = toPlayerId,
                FromPlayerId = LocalPlayerId,
                Success = success,
                Message = message
            }));
            CoopLog.Message(Loc.T("Client.11", toPlayerId, success, message));
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

        public void SendResearchSync(int toPlayerId, string kind, string data)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.ResearchSync, new ResearchSyncPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                FromPlayerName = LocalPlayerName,
                Kind = kind,
                Data = data
            }));
        }

        public void SendWorldEvent(int toPlayerId, string defName, int duration)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.WorldEvent, new WorldEventPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                FromPlayerName = LocalPlayerName,
                DefName = defName,
                Duration = duration
            }));
        }

        public void SendQuestMessage(int toPlayerId, string kind, int questId, string name, string description, int state, int rating, int participants, int ownerTicks = 0)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.QuestMessage, new QuestMessagePayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                FromPlayerName = LocalPlayerName,
                Kind = kind,
                QuestId = questId,
                Name = name,
                Description = description,
                State = state,
                Rating = rating,
                Participants = participants,
                OwnerTicks = ownerTicks
            }));
        }

        public void SendShipMessage(ShipMessagePayload payload)
        {
            if (!IsConnected) return;
            payload.FromPlayerId = LocalPlayerId;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.ShipMessage, payload));
        }

        public void SendSaveAll(string saveName)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.SaveAll, new SaveAllPayload { FromPlayerId = LocalPlayerId, FromPlayerName = LocalPlayerName, SaveName = saveName }));
        }

        public void SendModList(int toPlayerId, string modIds)
        {
            if (!IsConnected) return;
            NetIO.SendPacket(_stream, Packet.Create(PacketType.ModList, new ModListPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                FromPlayerName = LocalPlayerName,
                ModIds = modIds
            }));
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

        public class ServerPingResult
        {
            public bool Success;
            public int ProtocolVersion;
            public int ConnectedPlayers;
            public string WorldSeed;
            public string SaveName;
            public long LatencyMs;
            public string Error;
        }

        /// <summary>
        /// Conexión corta y aparte (no toca _stream/IsConnected, que son de la conexión de juego):
        /// abre un socket propio nada más para preguntarle al servidor si está vivo y cuánta gente
        /// tiene conectada, para la lista de servidores guardados. Llama a callback en un hilo de
        /// fondo, nunca en el principal.
        /// </summary>
        public static void PingServer(string ip, int port, Action<ServerPingResult> callback, int timeoutMs = 2500)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = new ServerPingResult();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        var connectTask = tcp.ConnectAsync(ip, port);
                        if (!connectTask.Wait(timeoutMs))
                        {
                            result.Error = Loc.T("Client.12");
                            callback(result);
                            return;
                        }

                        using (var stream = tcp.GetStream())
                        {
                            stream.ReadTimeout = timeoutMs;
                            stream.WriteTimeout = timeoutMs;

                            NetIO.SendPacket(stream, Packet.Create(PacketType.PingRequest, new PingRequestPayload()));
                            Packet resp = NetIO.ReadPacket(stream);
                            sw.Stop();

                            if (resp != null && resp.Type == PacketType.PingResponse)
                            {
                                var p = resp.GetPayload<PingResponsePayload>();
                                result.Success = true;
                                result.ProtocolVersion = p.ProtocolVersion;
                                result.ConnectedPlayers = p.ConnectedPlayers;
                                result.WorldSeed = p.WorldSeed;
                                result.SaveName = p.SaveName;
                                result.LatencyMs = sw.ElapsedMilliseconds;
                            }
                            else
                            {
                                result.Error = Loc.T("Client.13");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    result.Error = e.Message;
                }
                callback(result);
            });
        }
    }
}
