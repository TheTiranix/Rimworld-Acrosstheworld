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
        public int Wealth;

        public override string Label => RemotePlayerName ?? Loc.T("Common.Player");

        // Generador propio: solo terreno, sin base de facción falsa ni fauna al azar
        // (eso se sincroniza por red, ver CoopSessionManager). Ver Defs/MapGeneratorDefs.
        public override MapGeneratorDef MapGeneratorDef => DefDatabase<MapGeneratorDef>.GetNamed("RimCoop_MirrorBase");

        private static Command_Action Disabled(Command_Action c, string reason)
        {
            if (reason != null) c.Disable(reason);
            return c;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            var relation = CoopSessionManager.RelationWith(RemotePlayerId);
            bool ally = relation == Relation.Ally;
            bool hostile = relation == Relation.Hostile;
            int warMinutes = CoopSessionManager.HostileStartsInMinutes(RemotePlayerId);
            string me = CoopClient.Instance.LocalPlayerName;

            foreach (var g in DiplomacyGizmos(relation, warMinutes, me)) yield return g;

            yield return Disabled(new Command_Action
            {
                defaultLabel = Loc.T("PlayerBase.01"),
                defaultDesc = Loc.T("PlayerBase.02", RemotePlayerName),
                action = () => Find.WindowStack.Add(new Dialog_TradeOffer(RemotePlayerId, RemotePlayerName))
            }, hostile ? Loc.T("Diplomacy.HostileReason") : null);

            yield return Disabled(new Command_Action
            {
                defaultLabel = Loc.T("PlayerBase.03"),
                defaultDesc = Loc.T("PlayerBase.04", RemotePlayerName),
                action = () => Find.WindowStack.Add(new Dialog_ChooseColonistsToSend(this))
            }, ally ? null : Loc.T("Diplomacy.NeedAllyReason"));

            if (CoopSessionManager.IsCollaboratingWith(RemotePlayerId))
            {
                yield return new Command_Action
                {
                    defaultLabel = Loc.T("PlayerBase.05"),
                    defaultDesc = Loc.T("PlayerBase.06", RemotePlayerName),
                    action = () => Find.WindowStack.Add(new Dialog_ShareQuest(RemotePlayerId, RemotePlayerName))
                };

                yield return new Command_Action
                {
                    defaultLabel = Loc.T("PlayerBase.07"),
                    defaultDesc = Loc.T("PlayerBase.08", RemotePlayerName),
                    action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        Loc.T("PlayerBase.09", RemotePlayerName),
                        () => CoopSessionManager.StopCollaborating(RemotePlayerId)))
                };
            }

            yield return Disabled(new Command_Action
            {
                defaultLabel = Loc.T("PlayerBase.10"),
                defaultDesc = Loc.T("PlayerBase.11", RemotePlayerName),
                action = () => CoopSessionManager.EnterCoopMap(this)
            }, hostile ? Loc.T("Diplomacy.HostileReason") : null);

            string ideoSummary = CoopSessionManager.GetRemoteIdeoSummary(RemotePlayerId);
            if (!string.IsNullOrEmpty(ideoSummary))
            {
                yield return new Command_Action
                {
                    defaultLabel = Loc.T("PlayerBase.15"),
                    defaultDesc = Loc.T("PlayerBase.16", RemotePlayerName),
                    action = () => Find.WindowStack.Add(new Dialog_RemoteIdeo(RemotePlayerName, ideoSummary))
                };
            }

            yield return Disabled(new Command_Action
            {
                defaultLabel = Loc.T("PlayerBase.12"),
                defaultDesc = Loc.T("PlayerBase.13", RemotePlayerName),
                action = () => Find.WindowStack.Add(new Dialog_AttackStrength(RemotePlayerId, RemotePlayerName, Wealth))
            }, hostile ? null : warMinutes > 0 ? Loc.T("Diplomacy.AttackReasonWarning", warMinutes) : Loc.T("Diplomacy.AttackReasonPeace"));
        }

        // Alianza (proponer/aceptar), guerra y paz. Romper la alianza es el "Dejar de colaborar" de siempre (ver más abajo).
        private IEnumerable<Gizmo> DiplomacyGizmos(Relation relation, int warMinutes, string me)
        {
            if (relation == Relation.Neutral && warMinutes == 0)
            {
                string pending = CoopSessionManager.PendingFrom(RemotePlayerId, peace: false);
                bool theyProposed = !string.IsNullOrEmpty(pending) && !string.Equals(pending, me, System.StringComparison.OrdinalIgnoreCase);
                bool iProposed = string.Equals(pending, me, System.StringComparison.OrdinalIgnoreCase);
                string reason = iProposed ? Loc.T("Diplomacy.AllyPendingMine", RemotePlayerName)
                    : CoopSessionManager.LocalAllyCount() >= DiplomacyTable.MaxAllies ? Loc.T("Diplomacy.MaxAlliesReason") : null;
                yield return Disabled(new Command_Action
                {
                    defaultLabel = Loc.T(theyProposed ? "Diplomacy.AcceptAlly" : "Diplomacy.ProposeAlly"),
                    defaultDesc = Loc.T("Diplomacy.ProposeAllyDesc", RemotePlayerName),
                    action = () => CoopSessionManager.RequestDiplomacy(RemotePlayerId, "ally")
                }, reason);
            }

            if (relation != Relation.Hostile && warMinutes == 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = Loc.T("Diplomacy.DeclareWar"),
                    defaultDesc = Loc.T("Diplomacy.DeclareWarDesc", RemotePlayerName),
                    action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        Loc.T("Diplomacy.DeclareWarConfirm", RemotePlayerName),
                        () => CoopSessionManager.RequestDiplomacy(RemotePlayerId, "hostile"), destructive: true))
                };
            }
            else
            {
                bool iProposed = string.Equals(CoopSessionManager.PendingFrom(RemotePlayerId, peace: true), me, System.StringComparison.OrdinalIgnoreCase);
                yield return Disabled(new Command_Action
                {
                    defaultLabel = Loc.T("Diplomacy.ProposePeace"),
                    defaultDesc = Loc.T("Diplomacy.ProposePeaceDesc", RemotePlayerName),
                    action = () => CoopSessionManager.RequestDiplomacy(RemotePlayerId, "peace")
                }, iProposed ? Loc.T("Diplomacy.PeacePendingMine", RemotePlayerName) : null);
            }
        }

        public override string GetInspectString()
        {
            string text = Loc.T("PlayerBase.14", RemotePlayerName, ColonistCount, Wealth);
            string anomaly = CoopSessionManager.GetRemoteSnapshot(RemotePlayerId)?.DlcInfo;
            if (!string.IsNullOrEmpty(anomaly)) text += "\n" + anomaly;
            return text;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref RemotePlayerId, "remotePlayerId");
            Scribe_Values.Look(ref RemotePlayerName, "remotePlayerName");
            Scribe_Values.Look(ref ColonistCount, "colonistCount");
            Scribe_Values.Look(ref Wealth, "wealth");
        }
    }
}
