using System.Collections.Generic;
using Verse;

namespace RimCoopMod
{
    /// <summary>Recuerda los últimos datos usados para conectar (IP, puerto, nombre), para no tener que
    /// tipearlos de nuevo cada vez que abrís el diálogo de conexión, y la lista de servidores que
    /// fuiste guardando (estilo Half-Life/CS) para no tener que memorizar IPs.</summary>
    public class RimCoopModSettings : ModSettings
    {
        public string LastServerIp = "127.0.0.1";
        public string LastServerPort = "34500";
        public string LastPlayerName = "Jugador";

        // Cada entrada es "Apodo|IP|Puerto". Un List<string> plano es más simple de persistir con
        // Scribe que una clase propia, y alcanza para esto.
        public List<string> SavedServers = new List<string>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref LastServerIp, "rimcoopLastServerIp", "127.0.0.1");
            Scribe_Values.Look(ref LastServerPort, "rimcoopLastServerPort", "34500");
            Scribe_Values.Look(ref LastPlayerName, "rimcoopLastPlayerName", "Jugador");
            Scribe_Collections.Look(ref SavedServers, "rimcoopSavedServers", LookMode.Value);
            if (SavedServers == null) SavedServers = new List<string>();
        }
    }
}
