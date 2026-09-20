using System;
using System.IO;
using System.Reflection;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Misión entera (con todas sus partes: objetivos, señales, recompensas, estado) a texto y de vuelta, con el sistema de
    /// guardado del juego — la misma técnica que PawnTransfer. Es lo que hace que los que comparten una misión la vean idéntica.
    /// </summary>
    public static class QuestTransfer
    {
        public static string Serialize(Quest quest)
        {
            string path = Path.Combine(Path.GetTempPath(), $"rimcoop_quest_{Guid.NewGuid():N}.xml");
            PawnTransfer.MarkTransferNoise();
            try
            {
                Scribe.saver.InitSaving(path, "RimCoopQuest");
                Scribe_Deep.Look(ref quest, "quest");
                Scribe.saver.FinalizeSaving();
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                CoopLog.Warning("[RimCoop] Error al serializar una misión compartida: " + e.Message);
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch { }
                Scribe.mode = LoadSaveMode.Inactive;
            }
        }

        public static Quest Deserialize(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            string path = Path.Combine(Path.GetTempPath(), $"rimcoop_quest_{Guid.NewGuid():N}.xml");
            PawnTransfer.MarkTransferNoise();
            try
            {
                File.WriteAllText(path, xml);
                Quest quest = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref quest, "quest");
                Scribe.loader.FinalizeLoading();
                PawnTransfer.MarkTransferNoise();
                return quest;
            }
            catch (Exception e)
            {
                CoopLog.Warning("[RimCoop] Error al reconstruir una misión compartida: " + e.Message);
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch { }
                Scribe.mode = LoadSaveMode.Inactive;
            }
        }

        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Los relojes (TicksGame) de cada juego son independientes: los ticks ABSOLUTOS de la misión se corren para que los tiempos que se ven coincidan.</summary>
        public static void ShiftAbsoluteTicks(Quest quest, int offset)
        {
            if (offset == 0) return;
            Shift(quest, offset);
            foreach (var part in quest.PartsListForReading) Shift(part, offset);
        }

        private static void Shift(object obj, int offset)
        {
            if (obj == null) return;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(AllInstance | BindingFlags.DeclaredOnly))
                {
                    // "...Tick" (singular) = un instante absoluto (enableTick, acceptanceTick...); "...Ticks" son duraciones y no se tocan.
                    if (f.FieldType != typeof(int) || !f.Name.EndsWith("Tick", StringComparison.OrdinalIgnoreCase) || f.IsInitOnly) continue;
                    int v = (int)f.GetValue(obj);
                    if (v > 0) f.SetValue(obj, v + offset);
                }
            }
        }

        /// <summary>Copia todo el estado de la misión recibida sobre la entrada que ya existe (menos su id), para actualizarla sin duplicarla.</summary>
        public static void CopyInto(Quest target, Quest source)
        {
            foreach (var f in typeof(Quest).GetFields(AllInstance))
            {
                if (f.Name == "id" || f.IsInitOnly || f.IsLiteral || f.IsStatic) continue;
                f.SetValue(target, f.GetValue(source));
            }
            foreach (var part in target.PartsListForReading) part.quest = target;
        }

        /// <summary>La misión temporal recién armada no tiene que quedar registrada como receptora de señales.</summary>
        public static void Detach(Quest quest)
        {
            try { Find.SignalManager.DeregisterReceiver(quest); } catch { }
        }
    }
}
