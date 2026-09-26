using System;
using RimCoopMod;
using System.Linq;
using RimCoopMod.Networking;

namespace RimCoopServer
{
    class Program
    {
        static void Main(string[] args)
        {
            InitLanguage(args);
            Console.WriteLine(Loc.T("Program.01"));
            Console.WriteLine(Loc.T("Program.02", ProtocolInfo.Version));

            ServerConfig.MigrateLegacyIfNeeded();
            var config = ConfigFromArgs(args) ?? PickOrCreateSave();

            CoopLog.OnMessage = msg => Console.WriteLine("[INFO] " + msg);
            CoopLog.OnWarning = msg => Console.WriteLine("[WARN] " + msg);
            CoopLog.OnError = msg => Console.WriteLine("[ERROR] " + msg);

            var server = new CoopServer();
            server.Start(config.Port, config.Seed, config.PlanetCoverage,
                config.OverallRainfall, config.OverallTemperature, config.OverallPopulation,
                config.DataFolder);

            Console.WriteLine(Loc.T("Program.03", config.SaveName));
            Console.WriteLine(Loc.T("Program.04", ProtocolInfo.Version));
            Console.WriteLine(Loc.T("Program.05", config.Port));
            Console.WriteLine(Loc.T("Program.06", config.Seed));
            Console.WriteLine(Loc.T("Program.07", config.DataFolder));
            Console.WriteLine(Loc.T("Program.08", LanDiscovery.DiscoveryPort));
            Console.WriteLine(Loc.T("Program.09"));

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

            Console.WriteLine(Loc.T("Program.10", name));
            return ServerConfig.LoadOrCreateNamed(name, port, seed);
        }

        // ---- Idioma del servidor: --idioma es|en (o --lang), si no lo último elegido con el comando "idioma", si no el del sistema ----

        private static string LanguageFile => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData", "language.txt");

        private static string LanguageName(string language) => language == Loc.Spanish ? "Español" : "English";

        private static void InitLanguage(string[] args)
        {
            string chosen = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--idioma" || a == "--lang" || a == "--language") chosen = args[i + 1];
            }
            if (chosen == null)
            {
                try { if (System.IO.File.Exists(LanguageFile)) chosen = System.IO.File.ReadAllText(LanguageFile).Trim(); } catch { }
            }
            Loc.SetLanguage(chosen != null ? Loc.Normalize(chosen) : Loc.SystemLanguage());
        }

        private static void SaveLanguage(string language)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LanguageFile));
                System.IO.File.WriteAllText(LanguageFile, language);
            }
            catch { }
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
                case "exit":
                case "quit":
                    server.Stop();
                    return false;

                case "ayuda":
                case "help":
                    Console.WriteLine(Loc.T("Program.11"));
                    Console.WriteLine(Loc.T("Program.12"));
                    Console.WriteLine(Loc.T("Program.13"));
                    Console.WriteLine(Loc.T("Program.14"));
                    Console.WriteLine(Loc.T("Program.15"));
                    Console.WriteLine(Loc.T("Program.16"));
                    Console.WriteLine(Loc.T("Program.17"));
                    Console.WriteLine(Loc.T("Program.18"));
                    Console.WriteLine(Loc.T("Program.19"));
                    Console.WriteLine(Loc.T("Program.20"));
                    Console.WriteLine(Loc.T("Program.52"));
                    return true;

                case "idioma":
                case "language":
                case "lang":
                    {
                        if (tokens.Count < 2) { Console.WriteLine(Loc.T("Program.53", LanguageName(Loc.Language))); return true; }
                        string wanted = tokens[1].ToLowerInvariant();
                        if (wanted != "en" && wanted != "es" && wanted != "english" && wanted != "espanol" && wanted != "español") { Console.WriteLine(Loc.T("Program.53", LanguageName(Loc.Language))); return true; }
                        Loc.SetLanguage(Loc.Normalize(wanted));
                        SaveLanguage(Loc.Language);
                        Console.WriteLine(Loc.T("Program.54", LanguageName(Loc.Language)));
                        return true;
                    }

                case "jugadores":
                case "players":
                    {
                        var players = server.GetConnectedPlayers();
                        if (players.Count == 0) { Console.WriteLine(Loc.T("Program.21")); return true; }
                        Console.WriteLine(Loc.T("Program.22", players.Count));
                        foreach (var p in players)
                            Console.WriteLine($"  [{p.Id}] {p.Name}" + (p.Tile >= 0 ? Loc.T("Program.23", p.Tile, p.Colonists) : Loc.T("Program.24")));
                        return true;
                    }

                case "kick":
                    {
                        if (tokens.Count < 2) { Console.WriteLine(Loc.T("Program.25")); return true; }
                        // Un nombre puede tener espacios sin comillas: se prueba primero solo con el primer término (+ motivo) y,
                        // si no hay nadie así, con todo el resto de la línea como nombre.
                        if (!server.Kick(tokens[1], string.Join(" ", tokens.Skip(2)), out string kicked)
                            && !server.Kick(RestOfLine(line, 1), null, out kicked))
                        {
                            Console.WriteLine(Loc.T("Program.26", tokens[1]));
                            return true;
                        }
                        Console.WriteLine(Loc.T("Program.27", kicked));
                        return true;
                    }

                case "ban":
                    {
                        if (tokens.Count < 2) { Console.WriteLine(Loc.T("Program.28")); return true; }
                        if (!server.Ban(tokens[1], string.Join(" ", tokens.Skip(2)), out string banned))
                        {
                            Console.WriteLine(Loc.T("Program.29"));
                            return true;
                        }
                        Console.WriteLine(Loc.T("Program.30", banned, banned));
                        return true;
                    }

                case "unban":
                    {
                        if (tokens.Count < 2) { Console.WriteLine(Loc.T("Program.31")); return true; }
                        string name = RestOfLine(line, 1);
                        Console.WriteLine(server.Unban(tokens[1]) || server.Unban(name) ? Loc.T("Program.32", name) : Loc.T("Program.33", name));
                        return true;
                    }

                case "colonos":
                case "colonists":
                    {
                        var list = server.GetRegisteredColonists();
                        if (list.Count == 0) { Console.WriteLine(Loc.T("Program.34")); return true; }
                        Console.WriteLine(Loc.T("Program.35", list.Count));
                        foreach (var c in list)
                            Console.WriteLine(Loc.T("Program.36", c.Uid, c.Owner, c.Holder, c.BlobBytes / 1024));
                        return true;
                    }

                case "colono":
                case "colonist":
                    {
                        if (tokens.Count < 3 || !(string.Equals(tokens[1], "borrar", StringComparison.OrdinalIgnoreCase) || string.Equals(tokens[1], "delete", StringComparison.OrdinalIgnoreCase))) { Console.WriteLine(Loc.T("Program.37")); return true; }
                        Console.WriteLine(server.ForgetColonist(tokens[2]) ? Loc.T("Program.38") : Loc.T("Program.39"));
                        return true;
                    }

                case "bans":
                    {
                        var bans = server.GetBans();
                        Console.WriteLine(bans.Count == 0 ? Loc.T("Program.40") : Loc.T("Program.51", string.Join(", ", bans)));
                        return true;
                    }

                default:
                    Console.WriteLine(Loc.T("Program.41", cmd));
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
                Console.WriteLine(Loc.T("Program.42"));
                for (int i = 0; i < saves.Count; i++)
                {
                    var preview = ServerConfig.LoadOrCreateNamed(saves[i]);
                    Console.WriteLine(Loc.T("Program.43", i + 1, saves[i], preview.Seed, preview.Port));
                }
                Console.WriteLine();
                Console.WriteLine(Loc.T("Program.44"));
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(Loc.T("Program.45"));
            }

            while (true)
            {
                string input = ReadLineClean()?.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine(Loc.T("Program.46"));
                    continue;
                }

                if (int.TryParse(input, out int idx))
                {
                    if (idx >= 1 && idx <= saves.Count) return ServerConfig.LoadOrCreateNamed(saves[idx - 1]);
                    Console.WriteLine(Loc.T("Program.47"));
                    continue;
                }

                if (saves.Contains(input, StringComparer.OrdinalIgnoreCase))
                {
                    return ServerConfig.LoadOrCreateNamed(input);
                }

                // Nombre nuevo: pedir puerto y seed para esta partida (por si querés correr
                // varios servidores a la vez, cada uno en su propio puerto).
                Console.WriteLine(Loc.T("Program.48", input));
                Console.Write(Loc.T("Program.49"));
                string portInput = ReadLineClean()?.Trim();
                int? port = int.TryParse(portInput, out int p) ? p : (int?)null;

                Console.Write(Loc.T("Program.50"));
                string seedInput = ReadLineClean()?.Trim();

                return ServerConfig.LoadOrCreateNamed(input, port, string.IsNullOrEmpty(seedInput) ? null : seedInput);
            }
        }
    }
}
