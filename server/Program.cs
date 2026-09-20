using System;
using RimCoopMod.Networking;

namespace RimCoopServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== RimCoop Server ===");

            var config = ServerConfig.LoadOrCreate();

            CoopLog.OnMessage = msg => Console.WriteLine("[INFO] " + msg);
            CoopLog.OnWarning = msg => Console.WriteLine("[WARN] " + msg);
            CoopLog.OnError = msg => Console.WriteLine("[ERROR] " + msg);

            var server = new CoopServer();
            server.Start(config.Port, config.Seed, config.PlanetCoverage,
                config.OverallRainfall, config.OverallTemperature, config.OverallPopulation);

            Console.WriteLine($"Servidor escuchando en el puerto {config.Port}");
            Console.WriteLine($"Seed del mundo: {config.Seed}");
            Console.WriteLine("La configuración quedó guardada en la carpeta ServerData, junto al .exe.");
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
    }
}