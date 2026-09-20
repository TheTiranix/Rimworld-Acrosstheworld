using System;
using System.Globalization;
using System.IO;

namespace RimCoopServer
{
    /// <summary>
    /// Carga/guarda la configuración del servidor en su propia carpeta,
    /// para que la seed y el puerto persistan entre reinicios.
    /// </summary>
    public class ServerConfig
    {
        public int Port = 34500;
        public string Seed = "";
        public float PlanetCoverage = 0.3f;
        public string OverallRainfall = "Normal";
        public string OverallTemperature = "Normal";
        public string OverallPopulation = "Normal";

        private static readonly string FolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData");
        private static readonly string FilePath = Path.Combine(FolderPath, "server_config.txt");

        public static ServerConfig LoadOrCreate()
        {
            Directory.CreateDirectory(FolderPath);

            if (!File.Exists(FilePath))
            {
                var fresh = new ServerConfig { Seed = GenerateRandomSeed() };
                fresh.Save();
                Console.WriteLine($"[RimCoopServer] No había configuración previa. Se creó una nueva en {FilePath}");
                return fresh;
            }

            var config = new ServerConfig();
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;

                string key = parts[0].Trim();
                string value = parts[1].Trim();

                switch (key)
                {
                    case "Port": config.Port = int.Parse(value); break;
                    case "Seed": config.Seed = value; break;
                    case "PlanetCoverage": config.PlanetCoverage = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "OverallRainfall": config.OverallRainfall = value; break;
                    case "OverallTemperature": config.OverallTemperature = value; break;
                    case "OverallPopulation": config.OverallPopulation = value; break;
                }
            }

            Console.WriteLine($"[RimCoopServer] Configuración cargada desde {FilePath}");
            return config;
        }

        public void Save()
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllLines(FilePath, new[]
            {
                $"Port={Port}",
                $"Seed={Seed}",
                $"PlanetCoverage={PlanetCoverage.ToString(CultureInfo.InvariantCulture)}",
                $"OverallRainfall={OverallRainfall}",
                $"OverallTemperature={OverallTemperature}",
                $"OverallPopulation={OverallPopulation}"
            });
        }

        private static string GenerateRandomSeed()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var rnd = new Random();
            var arr = new char[8];
            for (int i = 0; i < arr.Length; i++) arr[i] = chars[rnd.Next(chars.Length)];
            return new string(arr);
        }
    }
}