using System.Collections.Generic;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimCoopMod.UI;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimCoopMod.World
{
    /// <summary>
    /// Punto en el mapa mundial que representa la colonia de otro jugador conectado.
    /// Es un MapParent real (la misma clase base que usan los asentamientos de facción y los
    /// campamentos de caravana), así que se puede "entrar" y ver un Map real, no una vista simulada.
    /// El terreno sale igual porque comparte seed+tile con el dueño real; las construcciones e
    /// ítems se rellenan por red (ver CoopSessionManager) porque eso es estado de juego, no
    /// generación procedural.
    /// </summary>
    public class CoopPlayerBase : MapParent
    {
        public int RemotePlayerId;
        public string RemotePlayerName;
        public int ColonistCount;

        public override string Label => RemotePlayerName ?? "Jugador";

        // Generador propio: solo terreno, sin base de facción falsa ni fauna al azar
        // (eso se sincroniza por red, ver CoopSessionManager). Ver Defs/MapGeneratorDefs.
        public override MapGeneratorDef MapGeneratorDef => DefDatabase<MapGeneratorDef>.GetNamed("RimCoop_MirrorBase");

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Comerciar",
                defaultDesc = $"Enviar una solicitud de comercio a {RemotePlayerName}.",
                action = () => Find.WindowStack.Add(new Dialog_TradeOffer(RemotePlayerId, RemotePlayerName))
            };

            yield return new Command_Action
            {
                defaultLabel = "Colaborar",
                defaultDesc = $"Enviar colonos tuyos a vivir y trabajar en la base de {RemotePlayerName}.",
                action = () => Find.WindowStack.Add(new Dialog_ChooseColonistsToSend(this))
            };

            yield return new Command_Action
            {
                defaultLabel = "Entrar a la base",
                defaultDesc = $"Entrar al mapa real de la base de {RemotePlayerName}, como cuando visitás el campamento de una caravana.",
                action = () => CoopSessionManager.EnterCoopMap(this)
            };

            yield return new Command_Action
            {
                defaultLabel = "Atacar",
                defaultDesc = $"Mandar una incursión contra la base de {RemotePlayerName}.",
                action = () => Find.WindowStack.Add(new Dialog_AttackStrength(RemotePlayerId, RemotePlayerName))
            };
        }

        public override string GetInspectString()
        {
            return $"Jugador: {RemotePlayerName}\nColonos: {ColonistCount}";
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref RemotePlayerId, "remotePlayerId");
            Scribe_Values.Look(ref RemotePlayerName, "remotePlayerName");
            Scribe_Values.Look(ref ColonistCount, "colonistCount");
        }
    }
}
