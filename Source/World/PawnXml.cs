using System.Xml;

namespace RimCoopMod.World
{
    /// <summary>Ajustes al XML de un pawn copiado entre partidas (ver PawnTransfer), sin nada de RimWorld para poder probarlo solo.</summary>
    public static class PawnXml
    {
        /// <summary>
        /// Saca el trabajo en curso y la cola de trabajos. Apuntan a cosas de la otra partida (la comida que estaba comiendo, por
        /// ejemplo): al cargarlos el juego arma sus pasos y revienta (NullReferenceException en JobDriver_Ingest y otros, y el pawn
        /// queda "sin trabajo"). El pawn llega sin trabajo y elige uno nuevo (o lo copia del dueño, si es un títere).
        /// </summary>
        public static string StripJobs(string xml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var jobs = doc.SelectSingleNode("/*/pawn/jobs");
            if (jobs == null) return xml;
            foreach (var name in new[] { "curJob", "curDriver", "jobQueue" })
            {
                var node = jobs.SelectSingleNode(name);
                if (node != null) jobs.RemoveChild(node);
            }
            return doc.OuterXml;
        }
    }
}
