using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RimCoopMod.Networking
{
    public class LanServerInfo
    {
        public string Ip;
        public int Port;
        public int ProtocolVersion;
        public int Players;
        public string SaveName;
        public string Seed;
    }

    /// <summary>
    /// Descubrimiento de servidores en la red local, al estilo de la pestaña LAN de Half-Life/CS:
    /// el cliente manda un broadcast UDP y cada servidor que lo escucha responde con sus datos, así
    /// se listan sin tipear la IP. Va aparte del protocolo de juego (texto plano por UDP, sin NetIO),
    /// para que siga funcionando y muestre "versión distinta" aunque mod y servidor no coincidan.
    /// </summary>
    public static class LanDiscovery
    {
        public const int DiscoveryPort = 34599;
        private const string RequestMagic = "RIMCOOP_DISCOVER";
        private const string ResponsePrefix = "RIMCOOP_SERVER";

        private static string Clean(string s) => (s ?? "").Replace("|", "/");

        // ---------------------------------------------------------------- servidor

        public class Responder
        {
            private readonly Func<LanServerInfo> _infoProvider;
            private Socket _socket;
            private Thread _thread;
            private volatile bool _running;

            public Responder(Func<LanServerInfo> infoProvider) { _infoProvider = infoProvider; }

            public bool Start()
            {
                try
                {
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    // Varios servidores en la misma PC (uno por partida, cada uno con su puerto) escuchan el mismo puerto de descubrimiento.
                    _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    _running = true;
                    _thread = new Thread(Loop) { IsBackground = true };
                    _thread.Start();
                    return true;
                }
                catch (Exception e)
                {
                    CoopLog.Warning(Loc.T("LanDiscovery.01", DiscoveryPort, e.Message));
                    try { _socket?.Close(); } catch { }
                    return false;
                }
            }

            private void Loop()
            {
                var buffer = new byte[512];
                while (_running)
                {
                    try
                    {
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int n = _socket.ReceiveFrom(buffer, ref remote);
                        if (Encoding.UTF8.GetString(buffer, 0, n) != RequestMagic) continue;

                        var info = _infoProvider();
                        if (info == null) continue;

                        string reply = string.Join("|", ResponsePrefix, info.ProtocolVersion, info.Port, info.Players, Clean(info.SaveName), Clean(info.Seed));
                        _socket.SendTo(Encoding.UTF8.GetBytes(reply), remote);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { if (!_running) break; } // en Windows un ICMP "puerto inalcanzable" tira 10054: no es grave
                    catch (Exception) { }
                }
            }

            public void Stop()
            {
                _running = false;
                try { _socket?.Close(); } catch { }
            }
        }

        // ---------------------------------------------------------------- cliente

        /// <summary>
        /// Busca servidores en la red local. Escucha respuestas unos instantes y llama a onDone (en un hilo
        /// de fondo, nunca el principal) con lo que encontró.
        /// </summary>
        public static void Scan(Action<List<LanServerInfo>> onDone, int listenMs = 1500)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var found = new Dictionary<string, LanServerInfo>();
                try
                {
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        socket.EnableBroadcast = true;
                        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                        byte[] request = Encoding.UTF8.GetBytes(RequestMagic);
                        foreach (var target in BroadcastTargets())
                        {
                            try { socket.SendTo(request, new IPEndPoint(target, DiscoveryPort)); } catch { }
                        }

                        var buffer = new byte[512];
                        var deadline = DateTime.UtcNow.AddMilliseconds(listenMs);
                        while (true)
                        {
                            int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                            if (remainingMs <= 0) break;
                            if (!socket.Poll(remainingMs * 1000, SelectMode.SelectRead)) break;

                            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                            int n;
                            try { n = socket.ReceiveFrom(buffer, ref remote); }
                            catch (SocketException) { continue; }

                            var info = Parse(Encoding.UTF8.GetString(buffer, 0, n), (remote as IPEndPoint)?.Address);
                            if (info == null) continue;

                            // Un servidor de esta misma PC contesta dos veces (por 127.0.0.1 y por la IP de la red): se queda con la de red.
                            string key = info.Port + "|" + info.SaveName + "|" + info.Seed;
                            if (!found.TryGetValue(key, out var existing) || (IPAddress.IsLoopback(IPAddress.Parse(existing.Ip)) && !IPAddress.IsLoopback(IPAddress.Parse(info.Ip))))
                                found[key] = info;
                        }
                    }
                }
                catch (Exception) { }

                onDone?.Invoke(found.Values.OrderBy(s => s.SaveName, StringComparer.OrdinalIgnoreCase).ToList());
            });
        }

        private static LanServerInfo Parse(string text, IPAddress from)
        {
            var parts = text.Split('|');
            if (parts.Length < 6 || parts[0] != ResponsePrefix || from == null) return null;
            if (!int.TryParse(parts[1], out int proto) || !int.TryParse(parts[2], out int port) || !int.TryParse(parts[3], out int players)) return null;
            return new LanServerInfo { Ip = from.ToString(), Port = port, ProtocolVersion = proto, Players = players, SaveName = parts[4], Seed = parts[5] };
        }

        // El broadcast "255.255.255.255" a veces sale solo por la placa por defecto: además se manda el broadcast dirigido de cada placa.
        private static IEnumerable<IPAddress> BroadcastTargets()
        {
            var result = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                        result.Add(new IPAddress(broadcast));
                    }
                }
            }
            catch { }
            result.Add(IPAddress.Broadcast);
            result.Add(IPAddress.Loopback);
            return result.Distinct();
        }
    }
}
