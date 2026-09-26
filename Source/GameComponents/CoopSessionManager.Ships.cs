using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Naves comerciales orbitales (comercio con el Imperio y otros comerciantes) compartidas entre colaboradores: cuando a un
    /// jugador le llega una nave, sus colaboradores la reciben también, con el mismo stock. Si alguno compra o vende, el stock
    /// cambia en todos; cuando la nave se va, se va de todos. Cada uno comercia con SUS colonos y SU plata (la consola de
    /// comunicaciones es de cada base).
    /// </summary>
    public partial class CoopSessionManager
    {
        [ThreadStatic] public static bool CreatingSharedShip;

        private class ShipLink
        {
            public TradeShip Ship;
            public int PartnerId;
            public int OriginPlayerId;
            public int ShipId;
            public string LastSig = "";
        }

        private readonly List<ShipLink> _shipLinks = new List<ShipLink>();

        private static int ShipLoadId(PassingShip ship) => Traverse.Create(ship).Field("loadID").GetValue<int>();

        private static string GoodsCsv(TradeShip ship) => string.Join(";", ship.Goods
            .Where(t => t != null && !(t is Pawn) && !t.Destroyed)
            .Select(t => t.def.defName + "," + (t.Stuff?.defName ?? "") + "," + t.stackCount + "," + QualityOf(t) + "," + t.HitPoints));

        private static void ApplyShipGoods(TradeShip ship, string csv)
        {
            var things = new List<Thing>();
            foreach (var entry in (csv ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length < 5) continue;
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(f[0]);
                if (def == null || !int.TryParse(f[2], out int count) || count <= 0) continue;
                var stuff = string.IsNullOrEmpty(f[1]) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(f[1]);
                try
                {
                    Thing t = ThingMaker.MakeThing(def, stuff);
                    t.stackCount = count;
                    ApplyQualityAndHp(t, int.TryParse(f[3], out int q) ? q : -1, int.TryParse(f[4], out int hp) ? hp : 0);
                    things.Add(t);
                }
                catch { }
            }

            // Los "bienes" que no son cosas comunes (esclavos, animales) no viajan: se quedan como estén.
            foreach (var old in ship.GetDirectlyHeldThings().ToList())
                if (!(old is Pawn)) { ship.GetDirectlyHeldThings().Remove(old); old.Destroy(DestroyMode.Vanish); }
            foreach (var t in things) ship.GetDirectlyHeldThings().TryAdd(t, false);
        }

        /// <summary>Le llegó una nave a mi base: se la paso a mis colaboradores conectados.</summary>
        public static void OnLocalTradeShipAdded(PassingShipManager manager, PassingShip passing)
        {
            if (CreatingSharedShip || !(passing is TradeShip ship) || !CoopClient.Instance.IsConnected) return;
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;

            var map = Traverse.Create(manager).Field("map").GetValue<Map>();
            if (map == null || map != LocalBaseMap) return;

            foreach (int partner in instance.OnlineCollaborators())
            {
                instance._shipLinks.Add(new ShipLink { Ship = ship, PartnerId = partner, OriginPlayerId = CoopClient.Instance.LocalPlayerId, ShipId = ShipLoadId(ship), LastSig = GoodsCsv(ship) });
                CoopClient.Instance.SendShipMessage(new ShipMessagePayload
                {
                    ToPlayerId = partner, Kind = "ship", OriginPlayerId = CoopClient.Instance.LocalPlayerId, ShipId = ShipLoadId(ship),
                    Name = ship.name, DefName = ship.def?.defName, FactionDef = Traverse.Create(ship).Field("faction").GetValue<Faction>()?.def.defName ?? "",
                    Ticks = ship.ticksUntilDeparture, Seed = ship.RandomPriceFactorSeed, Goods = GoodsCsv(ship)
                });
                CoopLog.Message(Loc.T("SessionManager_Ships.01", ship.name, partner));
            }
        }

        private void HandleShipMessage(ShipMessagePayload m)
        {
            if (!IsCollaborator(m.FromPlayerId)) return;
            TryRestoreShipLinks(); // un "update"/"depart" puede llegar antes del primer tick después de cargar

            switch (m.Kind)
            {
                case "ship":
                    {
                        var map = LocalBaseMap;
                        var def = DefDatabase<TraderKindDef>.GetNamedSilentFail(m.DefName);
                        if (map == null || def == null) return;
                        var faction = string.IsNullOrEmpty(m.FactionDef) ? null : Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.def.defName == m.FactionDef);

                        CreatingSharedShip = true;
                        try
                        {
                            var ship = new TradeShip(def, faction);
                            ship.name = m.Name;
                            ship.ticksUntilDeparture = m.Ticks;
                            Traverse.Create(ship).Field("randomPriceFactorSeed").SetValue(m.Seed);
                            ApplyShipGoods(ship, m.Goods);
                            map.passingShipManager.AddShip(ship);
                            _shipLinks.Add(new ShipLink { Ship = ship, PartnerId = m.FromPlayerId, OriginPlayerId = m.OriginPlayerId, ShipId = m.ShipId, LastSig = GoodsCsv(ship) });
                        }
                        finally { CreatingSharedShip = false; }
                        break;
                    }

                case "update":
                    {
                        var link = _shipLinks.FirstOrDefault(l => l.PartnerId == m.FromPlayerId && l.OriginPlayerId == m.OriginPlayerId && l.ShipId == m.ShipId);
                        if (link?.Ship == null || link.Ship.Departed) return;
                        ApplyShipGoods(link.Ship, m.Goods);
                        link.LastSig = GoodsCsv(link.Ship);
                        break;
                    }

                case "depart":
                    {
                        var link = _shipLinks.FirstOrDefault(l => l.PartnerId == m.FromPlayerId && l.OriginPlayerId == m.OriginPlayerId && l.ShipId == m.ShipId);
                        if (link == null) return;
                        _shipLinks.Remove(link);
                        CreatingSharedShip = true;
                        try { if (link.Ship != null && !link.Ship.Departed) link.Ship.Depart(); }
                        finally { CreatingSharedShip = false; }
                        break;
                    }
            }
        }

        /// <summary>Cada ~5 s: si el stock de una nave compartida cambió (alguien compró o vendió) se avisa; si se fue, también.</summary>
        private void TickShips()
        {
            if (!CoopClient.Instance.IsConnected) return;
            TryRestoreShipLinks();
            if (_shipLinks.Count == 0) return;

            foreach (var link in _shipLinks.ToList())
            {
                bool gone = link.Ship == null || link.Ship.Departed;
                if (gone)
                {
                    CoopClient.Instance.SendShipMessage(new ShipMessagePayload { ToPlayerId = link.PartnerId, Kind = "depart", OriginPlayerId = link.OriginPlayerId, ShipId = link.ShipId });
                    _shipLinks.Remove(link);
                    continue;
                }

                string sig = GoodsCsv(link.Ship);
                if (sig == link.LastSig) continue;
                link.LastSig = sig;
                CoopClient.Instance.SendShipMessage(new ShipMessagePayload
                {
                    ToPlayerId = link.PartnerId, Kind = "update", OriginPlayerId = link.OriginPlayerId, ShipId = link.ShipId,
                    Ticks = link.Ship.ticksUntilDeparture, Goods = sig
                });
            }
        }

        // =====================================================================
        // Guardar todos a la vez: cada jugador tiene su propia partida, y los colonos que se mandaron entre sí solo existen
        // en UNA. Si cada uno guarda por separado, después de cargar puede haber colonos duplicados o perdidos.
        // =====================================================================

        public static void RequestSaveAll()
        {
            if (!CoopClient.Instance.IsConnected) return;
            string name = "RimCoop_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            CoopClient.Instance.SendSaveAll(name);
            SaveGameNow(name, "vos");
        }

        private static void SaveGameNow(string saveName, string requestedBy)
        {
            try
            {
                GameDataSaveLoader.SaveGame(saveName);
                Messages.Message(Loc.T("SessionManager_Ships.02", saveName, requestedBy), MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("SessionManager_Ships.03", saveName, e.Message));
            }
        }
    }
}
