using System;
using System.Linq;
using RimCoopMod.Networking;

namespace RimCoopServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== RimCoop Server ===");
            Console.WriteLine($"Versión de protocolo: {ProtocolInfo.Version} (tiene que coincidir con la del mod de todos los jugadores)");

            ServerConfig.MigrateLegacyIfNeeded();
            var config = ConfigFromArgs(args) ?? PickOrCreateSave();

            CoopLog.OnMessage = msg => Console.WriteLine("[INFO] " + msg);
            CoopLog.OnWarning = msg => Console.WriteLine("[WARN] " + msg);
            CoopLog.OnError = msg => Console.WriteLine("[ERROR] " + msg);

            var server = new CoopServer();
            server.Start(config.Port, config.Seed, config.PlanetCoverage,
                config.OverallRainfall, config.OverallTemperature, config.OverallPopulation,
                config.DataFolder);

            Console.WriteLine($"Partida: {config.SaveName}");
            Console.WriteLine($"Versión de protocolo: {ProtocolInfo.Version}");
            Console.WriteLine($"Servidor escuchando en el puerto {config.Port}");
            Console.WriteLine($"Seed del mundo: {config.Seed}");
            Console.WriteLine($"La configuración quedó guardada en {config.DataFolder}");
            Console.WriteLine($"Búsqueda en LAN: los jugadores de la red local te ven solos (UDP {LanDiscovery.DiscoveryPort}).");
            Console.WriteLine("Escribí 'ayuda' para ver los comandos ('salir' apaga el servidor).");

            while (true)
            {
                string input = ReadLineClean();
                if (input == null) break; // la consola se cerró
                if (!HandleCommand(server, input)) break;
            }
        }

        /// <summary>
        /// Abrir una partida sin el menú, para scripts / .bat / varios servidores a la vez:
        ///   RimCoopServer.exe --partida amigos3 [--puerto 34501] [--seed ABC123]
        /// Si la partida no existe se crea (con ese puerto y esa seed, o los de siempre); si existe se continúa
        /// y --puerto/--seed se ignoran (quedan los de la partida guardada).
        /// </summary>
        private static ServerConfig ConfigFromArgs(string[] args)
        {
            string name = null, seed = null;
            int? port = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                bool hasValue = i + 1 < args.Length;
                if ((a == "--partida" || a == "-p" || a == "--save") && hasValue) name = args[++i];
                else if ((a == "--puerto" || a == "--port") && hasValue && int.TryParse(args[i + 1], out int p)) { port = p; i++; }
                else if (a == "--seed" && hasValue) seed = args[++i];
            }
            if (string.IsNullOrWhiteSpace(name)) return null;

            Console.WriteLine($"Partida elegida por argumento: {name}");
            return ServerConfig.LoadOrCreateNamed(name, port, seed);
        }

        // Si la consola recibe la entrada redirigida (un script, otro programa), puede venir con un BOM UTF-8 delante
        // del primer renglón y se colaba en el nombre de la partida.
        private static string ReadLineClean() => Console.ReadLine()?.TrimStart('\uFEFF');

        /// <summary>Devuelve false cuando hay que apagar el servidor.</summary>
        private static bool HandleCommand(CoopServer server, string line)
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0) return true;

            string cmd = tokens[0].ToLowerInvariant();
            switch (cmd)
            {
                case "salir":
                    server.Stop();
                    return false;

                case "ayuda":
                case "help":
                    Console.WriteLine("Comandos:");
                    Console.WriteLine("  jugadores                  lista los jugadores conectados (id y nombre)");
                    Console.WriteLine("  kick <nombre|id> [motivo]  saca a un jugador (puede volver a entrar)");
                    Console.WriteLine("  ban <nombre|id> [motivo]   lo saca y no lo deja volver a entrar (por nombre)");
                    Console.WriteLine("  unban <nombre>             le permite volver a entrar");
                    Console.WriteLine("  bans                       lista los baneados");
                    Console.WriteLine("  colonos                    registro de colonos enviados entre jugadores (quién los tiene)");
                    Console.WriteLine("  colono borrar <id>         saca un colono del registro (para destrabar algo a mano)");
                    Console.WriteLine("  salir                      apaga el servidor");
                    Console.WriteLine("  Los nombres con espacios van entre comillas: kick \"Juan Perez\" spam");
                    return true;

                case "jugadores":
                case "players":
                    {
                        var players = server.GetConnectedPlayers();
                        if (players.Count == 0) { Console.WriteLine("No hay jugadores conectados."); return true; }
                        Console.WriteLine($"{players.Count} jugador(es) conectado(s):");
                        foreach (var p in players)
                            Console.WriteLine($"  [{p.Id}] {p.Name}" + (p.Tile >= 0 ? $" (base en tile {p.Tile}, {p.Colonists} colono(s))" : " (todavía sin asentarse)"));
                        return true;
                    }

                case "kick":
                    {
                        if (tokens.Count < 2) { Console.WriteLine("Uso: kick <nombre|id> [motivo]"); return true; }
                        // Un nombre puede tener espacios sin comillas: se prueba primero solo con el primer término (+ motivo) y,
                        // si no hay nadie así, con todo el resto de la línea como nombre.
                        if (!server.Kick(tokens[1], string.Join(" ", tokens.Skip(2)), out string kicked)
                            && !server.Kick(RestOfLine(line, 1), null, out kicked))
                        {
                            Console.WriteLine($"No hay ningún jugador conectado que se llame o tenga id '{tokens[1]}'. Usá 'jugadores'.");
                            return true;
                        }
                        Console.WriteLine($"Se sacó a {kicked}.");
                        return true;
                    }

                case "ban":
                    {
                        if (tokens.Count < 2) { Console.WriteLine("Uso: ban <nombre|id> [motivo]"); return true; }
                        if (!server.Ban(tokens[1], string.Join(" ", tokens.Skip(2)), out string banned))
                        {
                            Console.WriteLine("Nombre inválido.");
                            return true;
                        }
                        Console.WriteLine($"{banned} quedó baneado (no puede volver a entrar hasta 'unban {banned}').");
                        return true;
                    }

                case "unban":
                    {
                        if (tokens.Count < 2) { Console.WriteLine("Uso: unban <nombre>"); return true; }
                        string name = RestOfLine(line, 1);
                        Console.WriteLine(server.Unban(tokens[1]) || server.Unban(name) ? $"Se desbaneó a {name}." : $"'{name}' no estaba baneado.");
                        return true;
                    }

                case "colonos":
                    {
                        var list = server.GetRegisteredColonists();
                        if (list.Count == 0) { Console.WriteLine("El registro de colonos está vacío."); return true; }
                        Console.WriteLine($"{list.Count} colono(s) registrado(s):");
                        foreach (var c in list)
                            Console.WriteLine($"  {c.Uid}  dueño: {c.Owner}  lo tiene: {c.Holder}  (copia de {c.BlobBytes / 1024} KB)");
                        return true;
                    }

                case "colono":
                    {
                        if (tokens.Count < 3 || !string.Equals(tokens[1], "borrar", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Uso: colono borrar <id>"); return true; }
                        Console.WriteLine(server.ForgetColonist(tokens[2]) ? "Se sacó del registro." : "No hay un colono con ese id en el registro (mirá 'colonos').");
                        return true;
                    }

                case "bans":
                    {
                        var bans = server.GetBans();
                        Console.WriteLine(bans.Count == 0 ? "No hay jugadores baneados." : "Baneados: " + string.Join(", ", bans));
                        return true;
                    }

                default:
                    Console.WriteLine($"Comando desconocido: '{cmd}'. Escribí 'ayuda'.");
                    return true;
            }
        }

        private static string RestOfLine(string line, int skipTokens)
        {
            var rest = line.Trim();
            for (int i = 0; i < skipTokens; i++)
            {
                int space = rest.IndexOf(' ');
                if (space < 0) return "";
                rest = rest.Substring(space + 1).Trim();
            }
            return rest.Trim('"');
        }

        // Separa por espacios respetando comillas: kick "Juan Perez" motivo -> [kick, Juan Perez, motivo]
        private static System.Collections.Generic.List<string> Tokenize(string line)
        {
            var tokens = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;
            foreach (char c in line ?? "")
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        /// <summary>
        /// Cada partida (seed + jugadores propios) queda en su propia carpeta, así se puede tener
        /// una para un grupo de amigos y otra totalmente distinta para otro grupo, sin que abrir el
        /// server siempre cargue el mismo mundo de siempre.
        /// </summary>
        private static ServerConfig PickOrCreateSave()
        {
            var saves = ServerConfig.ListSaves();

            if (saves.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Partidas guardadas:");
                for (int i = 0; i < saves.Count; i++)
                {
                    var preview = ServerConfig.LoadOrCreateNamed(saves[i]);
                    Console.WriteLine($"  {i + 1}) {saves[i]}  (seed {preview.Seed}, puerto {preview.Port})");
                }
                Console.WriteLine();
                Console.WriteLine("Escribí el número para continuar esa partida, o un nombre nuevo para crear una:");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("No hay partidas guardadas todavía. Escribí un nombre para crear la primera (por ejemplo 'amigos'):");
            }

            while (true)
            {
                string input = ReadLineClean()?.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine("Escribí un número de la lista o un nombre nuevo.");
                    continue;
                }

                if (int.TryParse(input, out int idx))
                {
                    if (idx >= 1 && idx <= saves.Count) return ServerConfig.LoadOrCreateNamed(saves[idx - 1]);
                    Console.WriteLine("Ese número no corresponde a ninguna partida de la lista.");
                    continue;
                }

                if (saves.Contains(input, StringComparer.OrdinalIgnoreCase))
                {
                    return ServerConfig.LoadOrCreateNamed(input);
                }

                // Nombre nuevo: pedir puerto y seed para esta partida (por si querés correr
                // varios servidores a la vez, cada uno en su propio puerto).
                Console.WriteLine($"Creando la partida nueva '{input}'.");
                Console.Write("Puerto (Enter para 34500): ");
                string portInput = ReadLineClean()?.Trim();
                int? port = int.TryParse(portInput, out int p) ? p : (int?)null;

                Console.Write("Seed del mundo (Enter para una aleatoria): ");
                string seedInput = ReadLineClean()?.Trim();

                return ServerConfig.LoadOrCreateNamed(input, port, string.IsNullOrEmpty(seedInput) ? null : seedInput);
            }
        }
    }
}
