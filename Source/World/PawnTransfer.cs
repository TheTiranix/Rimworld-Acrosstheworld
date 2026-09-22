using System;
using System.IO;
using System.Linq;
using RimCoopMod.Networking;
using RimWorld;
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
        // Mandar un colono a otro jugador lo saca del mapa con Destroy(Vanish). Para el juego eso es
        // indistinguible de un colono que muere: le corta las relaciones (familia, pareja, vínculos) y
        // les mete a los que quedan el pensamiento de "perdí a mi amigo/padre/etc." — sin sentido acá,
        // porque el colono no murió, solo se fue a ayudar a otra base. Mientras esta bandera está
        // prendida (ver Dialog_ChooseColonistsToSend) se lo salteamos, ver CoopSyncPatches.
        public static bool SuppressRelationLossOnDestroy;

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
            "with null pawn after loading",
            "Removed null ideos",
            "Used SetFaction to change",
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
                ReassignUniqueIds(pawn);
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

        /// <summary>
        /// El colono llega con los ids internos (thingIDNumber, hediffs, genes, habilidades, ítems que
        /// lleva encima) que tenía en la partida del que lo mandó — números chicos, iguales a los que
        /// cualquier otra partida recién empezada también está usando para SUS propios colonos. Si no
        /// se renumeran acá, mientras estás jugando no se nota nada raro, pero al guardar la partida
        /// dos objetos distintos (por ej. una herida del colono recibido y otra de uno propio) terminan
        /// con el mismo id, y el archivo queda corrupto: al volver a cargarlo, el juego tira
        /// "Cannot register ... Id already used by ..." y uno de los dos objetos se pierde o se mezcla
        /// con el otro. Le pedimos ids nuevos al mismo generador que usa el resto del juego para que
        /// nunca choquen con nada local.
        /// </summary>
        private static void ReassignUniqueIds(Pawn pawn)
        {
            if (pawn == null) return;
            try
            {
                pawn.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();

                foreach (var h in pawn.health?.hediffSet?.hediffs ?? Enumerable.Empty<Hediff>())
                    if (h != null) h.loadID = Find.UniqueIDsManager.GetNextHediffID();

                if (ModsConfig.BiotechActive && pawn.genes != null)
                    foreach (var g in pawn.genes.GenesListForReading)
                        if (g != null) g.loadID = Find.UniqueIDsManager.GetNextGeneID();

                if (ModsConfig.RoyaltyActive && pawn.abilities != null)
                    foreach (var a in pawn.abilities.abilities)
                        if (a != null) a.Id = Find.UniqueIDsManager.GetNextAbilityID();

                foreach (var apparel in pawn.apparel?.WornApparel ?? Enumerable.Empty<Apparel>())
                    if (apparel != null) apparel.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();

                foreach (var eq in pawn.equipment?.AllEquipmentListForReading ?? Enumerable.Empty<ThingWithComps>())
                    if (eq != null) eq.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();

                if (pawn.inventory?.innerContainer != null)
                    foreach (var item in pawn.inventory.innerContainer)
                        if (item != null) item.thingIDNumber = Find.UniqueIDsManager.GetNextThingID();
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudieron renumerar los ids internos del colono transferido: {e.Message}");
            }
        }
    }
}
