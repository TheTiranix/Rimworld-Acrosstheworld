using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using RimCoopMod.Networking;
using UnityEngine;
using Verse;

namespace RimCoopMod
{
    public class RimCoopMod : Mod
    {
        public static RimCoopMod Instance;
        public RimCoopModSettings Settings;

        public RimCoopMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<RimCoopModSettings>();

            // Verse.Log escribe siempre a Player.log, que Unity comparte por USUARIO, no por
            // proceso: si corrés dos instancias de RimWorld en la misma PC (host + cliente para
            // probar) se pisan y el archivo queda ilegible. Por eso, además de Verse.Log, cada
            // proceso escribe su propio log separado (nombrado con su PID) para poder debuggear.
            StreamWriter fileWriter = null;
            try
            {
                string logDir = Path.Combine(Application.persistentDataPath, "RimCoopLogs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, $"rimcoop_{Process.GetCurrentProcess().Id}.log");
                fileWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
                fileWriter.WriteLine($"=== RimCoop log — proceso {Process.GetCurrentProcess().Id} — {DateTime.Now} ===");
            }
            catch (Exception e)
            {
                Log.Warning("[RimCoop] No se pudo crear el archivo de log propio: " + e.Message);
            }

            CoopLog.OnMessage = msg =>
            {
                Log.Message(msg);
                fileWriter?.WriteLine("[MSG] " + msg);
            };
            CoopLog.OnWarning = msg =>
            {
                Log.Warning(msg);
                fileWriter?.WriteLine("[WARN] " + msg);
            };
            CoopLog.OnError = msg =>
            {
                Log.Error(msg);
                fileWriter?.WriteLine("[ERROR] " + msg);
            };

            var harmony = new Harmony("tunombre.rimcoop");
            harmony.PatchAll();

            CoopUpdater.Install();

            CoopLog.Message("[RimCoop] Mod cargado. Parches de Harmony aplicados.");
        }
    }
}