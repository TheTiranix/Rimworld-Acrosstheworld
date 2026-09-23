using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RimCoopServer
{
    /// <summary>
    /// Carga/guarda la configuración del servidor en su propia carpeta,
    /// para que la seed y el puerto persistan entre reinicios.
    /// Cada "partida" (seed + jugadores propios) vive en su propia subcarpeta de ServerData/,
    /// así se puede tener una para 3 amigos y otra completamente distinta para otro grupo,
    /// sin que abrir el server siempre te tire al mismo mundo.
    /// </summary>
    public class ServerConfig
    {
        public int Port = 34500;
        public string Seed = "";
        public float PlanetCoverage = 0.3f;
        public string OverallRainfall = "Normal";
        public string OverallTemperature = "Normal";
        public string OverallPopulation = "Normal";

        public string SaveName;
        public string DataFolder;

        private static readonly string RootFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerData");

        /// <summary>Nombres de las partidas guardadas (subcarpetas de ServerData/ con su propio server_config.txt).</summary>
        public static List<string> ListSaves()
        {
            Directory.CreateDirectory(RootFolder);
            return Directory.GetDirectories(RootFolder)
                .Where(d => File.Exists(Path.Combine(d, "server_config.txt")))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Si existe la carpeta vieja (ServerData/server_config.txt directo, de antes de que hubiera
        /// partidas separadas) y todavía no hay ninguna partida creada, la migra a ServerData/default/
        /// para no perder el mundo que ya tenía la gente.
        /// </summary>
        public static void MigrateLegacyIfNeeded()
        {
            string legacyConfig = Path.Combine(RootFolder, "server_config.txt");
            string legacyPlayerIds = Path.Combine(RootFolder, "player_ids.txt");
            if (!File.Exists(legacyConfig)) return;

            string defaultFolder = Path.Combine(RootFolder, "default");
            if (Directory.Exists(defaultFolder)) return;

            Directory.CreateDirectory(defaultFolder);
            File.Move(legacyConfig, Path.Combine(defaultFolder, "server_config.txt"));
            if (File.Exists(legacyPlayerIds)) File.Move(legacyPlayerIds, Path.Combine(defaultFolder, "player_ids.txt"));
            Console.WriteLine("[RimCoopServer] Se migró la partida existente a ServerData/default/.");
        }

        public static ServerConfig LoadOrCreateNamed(string saveName, int? portOverride = null, string seedOverride = null)
        {
            string folder = Path.Combine(RootFolder, SanitizeName(saveName));
            Directory.CreateDirectory(folder);
            string filePath = Path.Combine(folder, "server_config.txt");

            ServerConfig config;
            if (!File.Exists(filePath))
            {
                config = new ServerConfig
                {
                    SaveName = saveName,
                    DataFolder = folder,
                    Seed = string.IsNullOrWhiteSpace(seedOverride) ? GenerateRandomSeed() : seedOverride,
                    Port = portOverride ?? 34500
                };
                config.Save();
                Console.WriteLine($"[RimCoopServer] Partida nueva '{saveName}' creada en {folder} (seed {config.Seed}, puerto {config.Port}).");
                return config;
            }

            config = new ServerConfig { SaveName = saveName, DataFolder = folder };
            foreach (var line in File.ReadAllLines(filePath))
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

            Console.WriteLine($"[RimCoopServer] Partida '{saveName}' cargada desde {filePath}");
            return config;
        }

        public void Save()
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllLines(Path.Combine(DataFolder, "server_config.txt"), new[]
            {
                $"Port={Port}",
                $"Seed={Seed}",
                $"PlanetCoverage={PlanetCoverage.ToString(CultureInfo.InvariantCulture)}",
                $"OverallRainfall={OverallRainfall}",
                $"OverallTemperature={OverallTemperature}",
                $"OverallPopulation={OverallPopulation}"
            });
        }

        private static string SanitizeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return string.IsNullOrEmpty(clean) ? "default" : clean;
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
