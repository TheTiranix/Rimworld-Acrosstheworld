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

            ServerConfig.MigrateLegacyIfNeeded();
            var config = PickOrCreateSave();

            CoopLog.OnMessage = msg => Console.WriteLine("[INFO] " + msg);
            CoopLog.OnWarning = msg => Console.WriteLine("[WARN] " + msg);
            CoopLog.OnError = msg => Console.WriteLine("[ERROR] " + msg);

            var server = new CoopServer();
            server.Start(config.Port, config.Seed, config.PlanetCoverage,
                config.OverallRainfall, config.OverallTemperature, config.OverallPopulation,
                config.DataFolder);

            Console.WriteLine($"Partida: {config.SaveName}");
            Console.WriteLine($"Servidor escuchando en el puerto {config.Port}");
            Console.WriteLine($"Seed del mundo: {config.Seed}");
            Console.WriteLine($"La configuración quedó guardada en {config.DataFolder}");
            Console.WriteLine("Escribí 'salir' y Enter para apagar el servidor.");

            while (true)
            {
                string input = Console.ReadLine();
                if (string.Equals(input, "salir", StringComparison.OrdinalIgnoreCase))
                {
                    server.Stop();
                    break;
                }
            }
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
                string input = Console.ReadLine()?.Trim();
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
                string portInput = Console.ReadLine()?.Trim();
                int? port = int.TryParse(portInput, out int p) ? p : (int?)null;

                Console.Write("Seed del mundo (Enter para una aleatoria): ");
                string seedInput = Console.ReadLine()?.Trim();

                return ServerConfig.LoadOrCreateNamed(input, port, string.IsNullOrEmpty(seedInput) ? null : seedInput);
            }
        }
    }
}
