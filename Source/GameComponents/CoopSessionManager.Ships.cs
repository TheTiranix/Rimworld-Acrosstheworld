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
            public readonly List<string> PendingDeltas = new List<string>(); // llegaron mientras yo comerciaba con esta nave
        }

        private static bool IsTradingWith(TradeShip ship) => TradeSession.Active && TradeSession.trader == ship;

        private readonly List<ShipLink> _shipLinks = new List<ShipLink>();

        private static int ShipLoadId(PassingShip ship) => Traverse.Create(ship).Field("loadID").GetValue<int>();

        private static string GoodsCsv(TradeShip ship) => string.Join(";", ship.Goods
            .Where(t => t != null && !(t is Pawn) && !t.Destroyed && IsCsvTransferable(t))
            .Select(t => t.def.defName + "," + (t.Stuff?.defName ?? "") + "," + t.stackCount + "," + QualityOf(t) + "," + t.HitPoints));

        // "def,material,cantidad,calidad,vida" -> cantidad por "def,material,calidad,vida"
        private static Dictionary<string, int> GoodsCounts(string csv)
        {
            var counts = new Dictionary<string, int>();
            foreach (var entry in (csv ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length < 5 || !int.TryParse(f[2], out int n)) continue;
                string key = f[0] + "," + f[1] + "," + f[3] + "," + f[4];
                counts.TryGetValue(key, out int old);
                counts[key] = old + n;
            }
            return counts;
        }

        /// <summary>
        /// Qué cambió entre dos fotos del stock, con cantidades negativas para lo que se fue. Las naves compartidas mandan esto y no
        /// el stock entero: si los dos compraban casi a la vez, el stock entero del último pisaba la compra del otro (y la cosa
        /// comprada reaparecía); las diferencias se suman sin pisarse.
        /// </summary>
        private static string GoodsDelta(string before, string after)
        {
            var a = GoodsCounts(before);
            var b = GoodsCounts(after);
            var parts = new List<string>();
            foreach (var key in a.Keys.Union(b.Keys))
            {
                a.TryGetValue(key, out int x);
                b.TryGetValue(key, out int y);
                if (x == y) continue;
                var f = key.Split(',');
                parts.Add(f[0] + "," + f[1] + "," + (y - x) + "," + f[2] + "," + f[3]);
            }
            return string.Join(";", parts);
        }

        private static void ApplyGoodsDelta(TradeShip ship, string delta)
        {
            var added = new List<string>();
            foreach (var entry in (delta ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length < 5 || !int.TryParse(f[2], out int n) || n == 0) continue;
                if (n > 0) { added.Add(entry); continue; }

                int left = -n;
                foreach (var t in ship.GetDirectlyHeldThings().ToList())
                {
                    if (left <= 0) break;
                    if (t is Pawn || t.def.defName != f[0] || (t.Stuff?.defName ?? "") != f[1]
                        || QualityOf(t).ToString() != f[3] || t.HitPoints.ToString() != f[4]) continue;
                    int take = Math.Min(left, t.stackCount);
                    t.SplitOff(take).Destroy(DestroyMode.Vanish);
                    left -= take;
                }
            }
            foreach (var t in MakeGoods(string.Join(";", added))) ship.GetDirectlyHeldThings().TryAdd(t, true);
        }

        private static List<Thing> MakeGoods(string csv)
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
            return things;
        }

        private static void ApplyShipGoods(TradeShip ship, string csv)
        {
            var things = MakeGoods(csv);

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

                case "delta":
                    {
                        var link = _shipLinks.FirstOrDefault(l => l.PartnerId == m.FromPlayerId && l.OriginPlayerId == m.OriginPlayerId && l.ShipId == m.ShipId);
                        if (link?.Ship == null || link.Ship.Departed) return;
                        // Con la ventana de comercio abierta, sus filas apuntan a estas cosas: destruirlas ahora cobraba la plata sin
                        // entregar lo comprado. Se aplica apenas se cierre (ver TickShips).
                        if (IsTradingWith(link.Ship)) { link.PendingDeltas.Add(m.Goods); return; }
                        string mine = GoodsCsv(link.Ship);
                        if (mine != link.LastSig) SendShipDelta(link, mine); // lo mío que todavía no mandé sale antes de mezclar
                        ApplyGoodsDelta(link.Ship, m.Goods);
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
                if (sig != link.LastSig) SendShipDelta(link, sig);
                if (link.PendingDeltas.Count > 0 && !IsTradingWith(link.Ship))
                {
                    foreach (var delta in link.PendingDeltas) ApplyGoodsDelta(link.Ship, delta);
                    link.PendingDeltas.Clear();
                }
                link.LastSig = GoodsCsv(link.Ship);
            }
        }

        private static void SendShipDelta(ShipLink link, string current)
        {
            CoopClient.Instance.SendShipMessage(new ShipMessagePayload
            {
                ToPlayerId = link.PartnerId, Kind = "delta", OriginPlayerId = link.OriginPlayerId, ShipId = link.ShipId,
                Ticks = link.Ship.ticksUntilDeparture, Goods = GoodsDelta(link.LastSig, current)
            });
            link.LastSig = current;
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
            SaveGameNow(name, Loc.T("Common.You"));
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
