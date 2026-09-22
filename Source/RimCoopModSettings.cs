using Verse;

namespace RimCoopMod
{
    /// <summary>Recuerda los últimos datos usados para conectar (IP, puerto, nombre), para no tener que
    /// tipearlos de nuevo cada vez que abrís el diálogo de conexión.</summary>
    public class RimCoopModSettings : ModSettings
    {
        public string LastServerIp = "127.0.0.1";
        public string LastServerPort = "34500";
        public string LastPlayerName = "Jugador";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref LastServerIp, "rimcoopLastServerIp", "127.0.0.1");
            Scribe_Values.Look(ref LastServerPort, "rimcoopLastServerPort", "34500");
            Scribe_Values.Look(ref LastPlayerName, "rimcoopLastPlayerName", "Jugador");
        }
    }
}
