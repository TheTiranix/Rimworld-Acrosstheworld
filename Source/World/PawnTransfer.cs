using System;
using System.IO;
using RimCoopMod.Networking;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Convierte un Pawn real a un blob de texto (y de vuelta) para poder mandarlo por la red
    /// como si fuera un mensaje más, usando el propio sistema de guardado de RimWorld (Scribe)
    /// contra un archivo temporal — la misma técnica que usan mods de "copiar/pegar colono".
    /// Es la parte más frágil de todo el mod: si un colono llega roto del otro lado, es acá
    /// donde hay que mirar primero (referencias cruzadas sin resolver, relaciones, etc.).
    /// </summary>
    public static class PawnTransfer
    {
        // Copiar un pawn con el sistema de guardado del juego deja mensajes de error inofensivos (edad, ideología,
        // adicciones, referencias a otros pawns que no viajan). Durante unos segundos después de cada copia se
        // filtran SOLO esos mensajes conocidos; cualquier otro error se sigue viendo.
        private static float _quietUntil;
        public static void MarkTransferNoise() { _quietUntil = UnityEngine.Time.realtimeSinceStartup + 3f; }

        private static readonly string[] KnownNoise =
        {
            "Error while determining if",
            "should have Need",
            "Could not do PostLoadInit on RimWorld.Pawn_IdeoTracker",
            "is referenced (xml node name",
            "SaveableFromNode exception",
            "Could not resolve reference to object",
            "Notify_LifeStageStarted",
        };

        public static bool ShouldSuppressLog(string text)
        {
            if (string.IsNullOrEmpty(text) || text.StartsWith("[RimCoop]") || UnityEngine.Time.realtimeSinceStartup > _quietUntil) return false; // los errores propios del mod nunca se filtran
            foreach (var pattern in KnownNoise)
                if (text.Contains(pattern)) return true;
            return false;
        }

        public static string SerializePawn(Pawn pawn)
        {
            string path = Path.Combine(Path.GetTempPath(), $"rimcoop_pawn_{Guid.NewGuid():N}.xml");
            MarkTransferNoise();
            try
            {
                Scribe.saver.InitSaving(path, "RimCoopPawn");
                Scribe_Deep.Look(ref pawn, "pawn");
                Scribe.saver.FinalizeSaving();
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                CoopLog.Error("[RimCoop] Error al serializar un colono para enviarlo: " + e);
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch { }
                Scribe.mode = LoadSaveMode.Inactive;
            }
        }

        public static Pawn DeserializePawn(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;

            string path = Path.Combine(Path.GetTempPath(), $"rimcoop_pawn_{Guid.NewGuid():N}.xml");
            MarkTransferNoise();
            try
            {
                File.WriteAllText(path, xml);
                Pawn pawn = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref pawn, "pawn");
                Scribe.loader.FinalizeLoading();
                MarkTransferNoise(); // el spawn que viene justo después también dispara algunos de esos mensajes
                return pawn;
            }
            catch (Exception e)
            {
                CoopLog.Error("[RimCoop] Error al recibir un colono: " + e);
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch { }
                Scribe.mode = LoadSaveMode.Inactive;
            }
        }
    }
}
