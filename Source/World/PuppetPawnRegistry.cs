using System.Collections.Generic;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Un "pawn títere" es una copia visual real (mismo cuerpo/pelo/ropa) de un pawn que en
    /// realidad vive y se simula en el juego de OTRO jugador. Existe solo para que se vea como
    /// una persona de verdad en vez de un cuadrado en el mapa espejo — nunca debe actuar por su
    /// cuenta (comer, patrullar, pelear), porque esa simulación real ya está pasando del otro lado.
    /// Pawn_Tick_Patch usa este registro para saltearle el Tick() entero.
    /// </summary>
    public static class PuppetPawnRegistry
    {
        public struct PuppetInfo
        {
            public int HostPlayerId;
            public int HostPawnId; // thingIDNumber del lado del dueño real, no el local
        }

        private static readonly Dictionary<Pawn, PuppetInfo> _puppets = new Dictionary<Pawn, PuppetInfo>();

        public static void Register(Pawn pawn, int hostPlayerId, int hostPawnId)
        {
            _puppets[pawn] = new PuppetInfo { HostPlayerId = hostPlayerId, HostPawnId = hostPawnId };
        }

        public static void Unregister(Pawn pawn) => _puppets.Remove(pawn);

        public static bool IsPuppet(Pawn pawn) => pawn != null && _puppets.ContainsKey(pawn);

        public static bool TryGetInfo(Pawn pawn, out PuppetInfo info)
        {
            info = default;
            return pawn != null && _puppets.TryGetValue(pawn, out info);
        }
    }
}
