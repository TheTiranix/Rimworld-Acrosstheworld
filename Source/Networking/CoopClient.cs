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
        private volatile NetworkStream _stream; // lo leen el hilo de red y el principal
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
            // "Conectar" también está en el menú de pausa: una conexión anterior se cierra antes. Si no, su hilo de lectura
            // seguía vivo y terminaba leyendo del stream nuevo (frames partidos y la sesión nueva marcada como caída).
            Disconnect();
            LocalPlayerName = playerName;
            try
            {
                var tcp = new TcpClient();
                tcp.Connect(ip, port); // bloqueante; llamar desde un hilo aparte si querés no trabar la UI
                tcp.SendTimeout = SendTimeoutMs; // un servidor que no lee no congela el juego para siempre
                var stream = tcp.GetStream();
                _tcpClient = tcp;
                _stream = stream;

                NetIO.SendPacket(stream, Packet.Create(PacketType.Handshake, new HandshakePayload { PlayerName = playerName, ProtocolVersion = ProtocolInfo.Version }));

                IsConnected = true; // antes de arrancar el hilo: si no, ReadLoop podía ver "false" y salir sin leer nada
                _readThread = new Thread(() => ReadLoop(tcp, stream)) { IsBackground = true };
                _readThread.Start();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Disconnect();
                CoopLog.Error(Loc.T("Client.01", ip, port, e.Message));
            }
        }

        private const int SendTimeoutMs = 15000;

        /// <summary>
        /// Todos los envíos pasan por acá: si el socket se rompió, se corta la conexión (y se avisa en el log) en vez de tirar
        /// una excepción en el medio del tick del juego en cada envío siguiente.
        /// </summary>
        private bool Send(Packet packet)
        {
            var stream = _stream;
            if (!IsConnected || stream == null) return false;
            try
            {
                NetIO.SendPacket(stream, packet);
                return true;
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("Client.03", e.Message));
                if (ReferenceEquals(stream, _stream)) Disconnect();
                return false;
            }
        }

        private void ReadLoop(TcpClient tcp, NetworkStream stream)
        {
            bool gotServerInfo = false;
            try
            {
                while (IsConnected && ReferenceEquals(stream, _stream))
                {
                    Packet p = NetIO.ReadPacket(stream);
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
                if (ReferenceEquals(stream, _stream))
                {
                    CoopLog.Warning(Loc.T("Client.03", e.Message));
                    if (!gotServerInfo)
                        VersionError = Loc.T("Client.04");
                }
            }
            finally
            {
                // Solo si sigue siendo la conexión actual: el hilo de una conexión vieja no puede marcar como caída a la nueva.
                if (ReferenceEquals(stream, _stream)) IsConnected = false;
                try { tcp.Close(); } catch { }
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
            Send(Packet.Create(PacketType.PlayerUpdate, payload));
        }

        public void SendChat(string message)
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(message)) return;
            Send(Packet.Create(PacketType.Chat, new ChatPayload
            {
                PlayerId = LocalPlayerId,
                PlayerName = LocalPlayerName,
                Message = message
            }));
        }

        public void SendTradeRequest(int toPlayerId)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.TradeRequest, new TradeOrAttackPayload
            {
                FromPlayerId = LocalPlayerId,
                FromPlayerName = LocalPlayerName,
                ToPlayerId = toPlayerId
            }));
        }

        public void SendAttackRequest(int toPlayerId, int points = 300)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.AttackRequest, new TradeOrAttackPayload
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
            Send(Packet.Create(PacketType.TradeMessage, new TradeMessagePayload
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
            Send(Packet.Create(PacketType.WatchRequest, new WatchRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId
            }));
            CoopLog.Message(Loc.T("Client.06", hostPlayerId));
        }

        public void SendUnwatchRequest(int hostPlayerId)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.UnwatchRequest, new WatchRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId
            }));
        }

        public void SendMapSnapshot(MapSnapshotPayload payload)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.MapSnapshot, payload));
        }

        public void SendPawnOrder(int hostPlayerId, int pawnId, PawnOrderPayload payload)
        {
            if (!IsConnected) { CoopLog.Warning(Loc.T("Client.07")); return; }
            payload.FromPlayerId = LocalPlayerId;
            payload.ToPlayerId = hostPlayerId;
            payload.PawnId = pawnId;
            Send(Packet.Create(PacketType.PawnOrder, payload));
        }

        public void SendColonistGone(System.Collections.Generic.List<string> uids)
        {
            if (!IsConnected || uids == null || uids.Count == 0) return;
            Send(Packet.Create(PacketType.ColonistGone, new ColonistGonePayload { Uids = uids }));
        }

        public bool SendJoinRequest(int hostPlayerId, System.Collections.Generic.List<string> serializedPawns, System.Collections.Generic.List<string> ownerNames = null, System.Collections.Generic.List<string> uids = null)
        {
            if (!IsConnected)
            {
                CoopLog.Warning(Loc.T("Client.08", LocalPlayerId));
                return false;
            }

            bool sent = Send(Packet.Create(PacketType.JoinRequest, new JoinRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                SerializedPawns = serializedPawns,
                OwnerNames = ownerNames ?? new System.Collections.Generic.List<string>(),
                Uids = uids ?? new System.Collections.Generic.List<string>()
            }));
            if (sent) CoopLog.Message(Loc.T("Client.09", hostPlayerId, serializedPawns.Count, LocalPlayerId));
            return sent;
        }

        public void SendJoinResult(int toPlayerId, bool success, string message)
        {
            if (!IsConnected) { CoopLog.Warning(Loc.T("Client.10")); return; }
            Send(Packet.Create(PacketType.JoinResult, new JoinResultPayload
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
            Send(Packet.Create(PacketType.BaseSnapshotRequest, new BaseSnapshotRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId
            }));
        }

        public void SendBaseSnapshot(BaseSnapshotPayload payload)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.BaseSnapshot, payload));
        }

        public void SendPawnAppearanceRequest(int hostPlayerId, int pawnId)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.PawnAppearanceRequest, new PawnAppearanceRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                PawnId = pawnId
            }));
        }

        public void SendPawnAppearance(PawnAppearancePayload payload)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.PawnAppearance, payload));
        }

        // ---- Votación para pausar/despausar ----

        public void SendPauseVoteRequest(bool proposePause)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.PauseVoteRequest, new PauseVoteRequestPayload
            {
                FromPlayerId = LocalPlayerId,
                FromPlayerName = LocalPlayerName,
                ProposePause = proposePause
            }));
        }

        public void SendPauseVoteResponse(int toPlayerId, bool accept)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.PauseVoteResponse, new PauseVoteResponsePayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                Accept = accept
            }));
        }

        public void SendPauseVoteResult(bool approved, bool proposePause)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.PauseVoteResult, new PauseVoteResultPayload
            {
                Approved = approved,
                ProposePause = proposePause
            }));
        }

        public void SendBuildRequest(int hostPlayerId, string defName, string stuffDefName, int x, int z, int rotation)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.BuildRequest, new BuildRequestPayload
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
            Send(Packet.Create(PacketType.EventNotice, new EventNoticePayload
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
            Send(Packet.Create(PacketType.PawnSettingRequest, new PawnSettingPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = hostPlayerId,
                PawnId = pawnId,
                Kind = kind,
                Key = key,
                Value = value
            }));
        }

        public void SendPlayersRequest(bool fullResync = false)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.PlayersRequest, new WatchRequestPayload { FromPlayerId = LocalPlayerId, ToPlayerId = fullResync ? CoopServer.PlayersRequestFullResync : 0 }));
        }

        public void SendResearchSync(int toPlayerId, string kind, string data)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.ResearchSync, new ResearchSyncPayload
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
            Send(Packet.Create(PacketType.WorldEvent, new WorldEventPayload
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
            Send(Packet.Create(PacketType.QuestMessage, new QuestMessagePayload
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
            Send(Packet.Create(PacketType.ShipMessage, payload));
        }

        public void SendSaveAll(string saveName)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.SaveAll, new SaveAllPayload { FromPlayerId = LocalPlayerId, FromPlayerName = LocalPlayerName, SaveName = saveName }));
        }

        public void SendModList(int toPlayerId, string modIds)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.ModList, new ModListPayload
            {
                FromPlayerId = LocalPlayerId,
                ToPlayerId = toPlayerId,
                FromPlayerName = LocalPlayerName,
                ModIds = modIds
            }));
        }

        public void SendDiplomacy(int toPlayerId, string kind)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.DiplomacyMessage, new DiplomacyMessagePayload { FromPlayerId = LocalPlayerId, FromPlayerName = LocalPlayerName, ToPlayerId = toPlayerId, Kind = kind }));
        }

        public void SendSpeedChange(int speed)
        {
            if (!IsConnected) return;
            Send(Packet.Create(PacketType.SpeedChange, new SpeedChangePayload { FromPlayerId = LocalPlayerId, Speed = speed }));
        }

        public void Disconnect()
        {
            IsConnected = false;
            var stream = _stream;
            var tcp = _tcpClient;
            _stream = null;
            _tcpClient = null;
            try { stream?.Close(); } catch { }
            try { tcp?.Close(); } catch { }
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
