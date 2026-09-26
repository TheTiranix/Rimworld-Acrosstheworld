using System;
using System.Collections.Generic;
using System.Linq;
using RimCoopMod.Networking;
using RimCoopMod.UI;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Comercio entre jugadores y ataques (incursiones) entre bases.
    ///
    /// Comercio: A pide el stock de B, arma una oferta ("te doy X, quiero Y") y B la acepta o no.
    /// Al aceptar, cada lado saca de SU mapa lo que se comprometió y se lo manda al otro, que lo
    /// recibe en cápsulas (como un comerciante). Nadie toca el mapa del otro directamente.
    ///
    /// Ataque: el atacante elige la fuerza y el defensor sufre una incursión enemiga real, generada
    /// por el storyteller de SU juego con esos puntos.
    /// </summary>
    public partial class CoopSessionManager
    {
        // =====================================================================
        // Ítems: contar, sacar del mapa, recibir
        // =====================================================================

        private static IEnumerable<Thing> FreeItems(Map map) =>
            map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.Spawned && t.def.category == ThingCategory.Item && !(t is Corpse));

        public static Dictionary<ThingDef, int> AggregateStock(Map map)
        {
            var result = new Dictionary<ThingDef, int>();
            if (map == null) return result;
            foreach (var t in FreeItems(map))
            {
                result.TryGetValue(t.def, out int n);
                result[t.def] = n + t.stackCount;
            }
            return result;
        }

        private static string StockToCsv(Dictionary<ThingDef, int> stock) =>
            string.Join(";", stock.Select(kv => kv.Key.defName + ",," + kv.Value + ",-1,0"));

        public static Dictionary<string, int> ParseSimpleItems(string csv)
        {
            var result = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(csv)) return result;
            foreach (var entry in csv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length >= 3 && int.TryParse(f[2], out int n) && n > 0)
                {
                    result.TryGetValue(f[0], out int old);
                    result[f[0]] = old + n;
                }
            }
            return result;
        }

        public static string SimpleItemsToCsv(Dictionary<ThingDef, int> items) =>
            string.Join(";", items.Where(kv => kv.Value > 0).Select(kv => kv.Key.defName + ",," + kv.Value + ",-1,0"));

        /// <summary>Saca del mapa hasta la cantidad pedida de cada ítem; devuelve lo que realmente pudo sacar (con material, calidad y vida).</summary>
        private static string RemoveItems(Map map, Dictionary<string, int> wanted)
        {
            var taken = new List<string>();
            foreach (var kv in wanted)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
                if (def == null) continue;
                int left = kv.Value;

                foreach (var t in FreeItems(map).Where(x => x.def == def).ToList())
                {
                    if (left <= 0) break;
                    int n = Math.Min(left, t.stackCount);
                    Thing part = t.SplitOff(n);
                    taken.Add(def.defName + "," + (part.Stuff?.defName ?? "") + "," + part.stackCount + "," + QualityOf(part) + "," + part.HitPoints);
                    left -= part.stackCount;
                    part.Destroy(DestroyMode.Vanish);
                }
            }
            return string.Join(";", taken);
        }

        /// <summary>Recibe ítems como si los trajera un comerciante: caen en cápsulas cerca de la zona de comercio de mi base.</summary>
        private static void ReceiveItems(string csv, string fromName)
        {
            var map = LocalBaseMap;
            if (map == null || string.IsNullOrEmpty(csv)) return;

            var things = new List<Thing>();
            foreach (var entry in csv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = entry.Split(',');
                if (f.Length < 5) continue;
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(f[0]);
                if (def == null || !int.TryParse(f[2], out int count) || count <= 0) continue;
                var stuff = string.IsNullOrEmpty(f[1]) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(f[1]);

                while (count > 0)
                {
                    int chunk = Math.Min(count, Math.Max(1, def.stackLimit));
                    try
                    {
                        Thing t = ThingMaker.MakeThing(def, stuff);
                        t.stackCount = chunk;
                        ApplyQualityAndHp(t, int.TryParse(f[3], out int q) ? q : -1, int.TryParse(f[4], out int hp) ? hp : 0);
                        things.Add(t);
                    }
                    catch (Exception e) { CoopLog.Warning(Loc.T("SessionManager_Trade.01", def.defName, e.Message)); break; }
                    count -= chunk;
                }
            }

            if (things.Count == 0) return;
            DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, things, 110, false, false, true, true, true, null);
            Messages.Message(Loc.T("SessionManager_Trade.02", fromName), MessageTypeDefOf.PositiveEvent, false);
        }

        // =====================================================================
        // Mensajes de comercio
        // =====================================================================

        private readonly Dictionary<int, TradeMessagePayload> _pendingOffersSent = new Dictionary<int, TradeMessagePayload>();

        public static void SendOffer(int toPlayerId, string giveCsv, string wantCsv)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;
            int offerId = UnityEngine.Random.Range(1, int.MaxValue);
            var msg = new TradeMessagePayload { ToPlayerId = toPlayerId, Kind = "offer", OfferId = offerId, Data = giveCsv + "#" + wantCsv };
            instance.RegisterSentOffer(offerId, msg);
            CoopClient.Instance.SendTradeMessage(toPlayerId, "offer", msg.Data, offerId);
        }

        private void HandleTradeMessage(TradeMessagePayload m)
        {
            switch (m.Kind)
            {
                case "stockreq":
                    CoopClient.Instance.SendTradeMessage(m.FromPlayerId, "stock", StockToCsv(AggregateStock(LocalBaseMap)));
                    break;

                case "stock":
                    Dialog_TradeOffer.Current?.SetPartnerStock(m.FromPlayerId, m.Data);
                    break;

                case "offer":
                    {
                        var parts = (m.Data ?? "").Split('#');
                        AddIncomingOffer(m.FromPlayerId, m.FromPlayerName, m.OfferId,
                            parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
                        break;
                    }

                case "reject":
                    ForgetSentOffer(m.OfferId);
                    Messages.Message(Loc.T("SessionManager_Trade.03", m.FromPlayerName), MessageTypeDefOf.RejectInput, false);
                    break;

                case "accept":
                    {
                        // El otro aceptó y ya me mandó lo que yo pedía: ahora cumplo mi parte.
                        if (!_pendingOffersSent.TryGetValue(m.OfferId, out var sent)) break;
                        ForgetSentOffer(m.OfferId);
                        var give = ParseSimpleItems(sent.Data.Split('#')[0]);
                        string removed = RemoveItems(LocalBaseMap, give);
                        CoopClient.Instance.SendTradeMessage(m.FromPlayerId, "transfer", removed);
                        Messages.Message(Loc.T("SessionManager_Trade.04", m.FromPlayerName), MessageTypeDefOf.PositiveEvent, false);
                        break;
                    }

                case "transfer":
                    ReceiveItems(m.Data, m.FromPlayerName ?? Loc.T("SessionManager_Trade.05"));
                    break;
            }
        }

        /// <summary>El receptor aceptó la oferta: saca lo que le piden (si lo tiene), se lo manda al oferente y le avisa.</summary>
        public static bool AcceptOffer(int fromPlayerId, string fromName, int offerId, string wantCsv)
        {
            // Los mensajes a un jugador desconectado se pierden: si acepto ahora, mis ítems saldrían y nunca llegarían.
            if (!IsPlayerOnline(fromPlayerId))
            {
                Messages.Message(Loc.T("SessionManager_Trade.06", fromName), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            var map = LocalBaseMap;
            var want = ParseSimpleItems(wantCsv);
            var stock = AggregateStock(map);
            foreach (var kv in want)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
                stock.TryGetValue(def ?? ThingDefOf.Silver, out int have);
                if (def == null || have < kv.Value)
                {
                    Messages.Message(Loc.T("SessionManager_Trade.07", def?.label ?? kv.Key), MessageTypeDefOf.RejectInput, false);
                    CoopClient.Instance.SendTradeMessage(fromPlayerId, "reject", "", offerId);
                    return true; // la oferta quedó resuelta (rechazada)
                }
            }

            string removed = RemoveItems(map, want);
            CoopClient.Instance.SendTradeMessage(fromPlayerId, "transfer", removed);
            CoopClient.Instance.SendTradeMessage(fromPlayerId, "accept", "", offerId);
            return true;
        }

        // =====================================================================
        // Ataques
        // =====================================================================

        private void HandleAttack(TradeOrAttackPayload a)
        {
            var map = LocalBaseMap;
            if (map == null) return;

            try
            {
                var def = IncidentDefOf.RaidEnemy;
                var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                parms.forced = true;
                parms.points = Mathf.Clamp(a.Points > 0 ? a.Points : 300, 50, 4000);
                parms.faction = Find.FactionManager.RandomEnemyFaction(false, false, false, TechLevel.Undefined) ?? Faction.OfPirates;

                Messages.Message(Loc.T("SessionManager_Trade.08", a.FromPlayerName), MessageTypeDefOf.ThreatBig, false);
                if (!def.Worker.TryExecute(parms))
                    CoopLog.Warning(Loc.T("SessionManager_Trade.09", a.FromPlayerName));
                else
                    CoopLog.Message(Loc.T("SessionManager_Trade.10", a.FromPlayerName, parms.points));
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("SessionManager_Trade.11", e.Message));
            }
        }
    }
}
