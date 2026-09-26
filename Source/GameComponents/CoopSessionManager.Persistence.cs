using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimCoopMod.Networking;
using RimCoopMod.UI;
using RimWorld;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Lo que estaba "en el aire" entre jugadores y se perdía al guardar y cargar: ofertas de comercio (las que mandé y las que me
    /// mandaron), invitaciones a misiones compartidas y el vínculo de las naves comerciales compartidas. Todo se guarda con la
    /// partida y se referencia por NOMBRE de jugador (los ids cambian entre sesiones), y se retoma cuando el otro está conectado.
    /// </summary>
    public partial class CoopSessionManager
    {
        private class IncomingOffer
        {
            public int OfferId;
            public string FromName;
            public string GiveCsv;
            public string WantCsv;
        }

        // Ofertas que me mandaron y todavía no respondí (ni acepté ni rechacé).
        private List<IncomingOffer> _incomingOffers = new List<IncomingOffer>();
        // Invitaciones a misión que me mandaron y todavía no respondí. FromPlayerId no se usa: se resuelve por FromPlayerName al abrirla.
        private List<QuestMessagePayload> _incomingQuestInvites = new List<QuestMessagePayload>();
        // Cuándo (TicksGame) mandé cada oferta, para descartar las viejísimas al cargar.
        private readonly Dictionary<int, int> _sentOfferTicks = new Dictionary<int, int>();

        // Vínculos de naves compartidas leídos de la partida que todavía no se pudieron reconstruir (falta la nave o que el otro esté conectado).
        private List<string[]> _pendingShipRestores = new List<string[]>();

        private const int SentOfferMaxAgeTicks = 60000 * 30;

        // ---- guardado ----

        private static string Enc(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        private static string Dec(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { return ""; }
        }

        // Base64 en cada campo: las descripciones de misión tienen saltos de línea, y los CSV de ítems tienen de todo.
        private static string Pack(params string[] fields) => string.Join("|", fields.Select(Enc));
        private static string[] Unpack(string record) => (record ?? "").Split('|').Select(Dec).ToArray();

        private string NameOfPlayerId(int id)
        {
            if (id == CoopClient.Instance.LocalPlayerId) return CoopClient.Instance.LocalPlayerName ?? "";
            return _playerNames.TryGetValue(id, out var n) ? n : "";
        }

        private int PlayerIdByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (name == CoopClient.Instance.LocalPlayerName) return CoopClient.Instance.LocalPlayerId;
            foreach (var kv in _playerNames) if (kv.Value == name) return kv.Key;
            return -1;
        }

        private void ExposePendingInteractions()
        {
            // --- ofertas que mandé ---
            var sent = new List<string>();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                foreach (var kv in _pendingOffersSent)
                {
                    _sentOfferTicks.TryGetValue(kv.Key, out int tick);
                    string toName = NameOfPlayerId(kv.Value.ToPlayerId);
                    if (string.IsNullOrEmpty(toName) && _pendingOfferPartnerNames.TryGetValue(kv.Key, out var known)) toName = known;
                    sent.Add(Pack(kv.Key.ToString(), toName, tick.ToString(), kv.Value.Data));
                }
            }
            Scribe_Collections.Look(ref sent, "rimcoopSentOffers", LookMode.Value);

            // --- ofertas que me mandaron ---
            var incoming = _incomingOffers.Select(o => Pack(o.OfferId.ToString(), o.FromName, o.GiveCsv, o.WantCsv)).ToList();
            Scribe_Collections.Look(ref incoming, "rimcoopIncomingOffers", LookMode.Value);

            // --- invitaciones a misión ---
            var invites = _incomingQuestInvites.Select(q => Pack(q.FromPlayerName, q.QuestId.ToString(), q.Name, q.Description,
                q.State.ToString(), q.Rating.ToString(), q.Participants.ToString())).ToList();
            Scribe_Collections.Look(ref invites, "rimcoopQuestInvites", LookMode.Value);

            // --- naves compartidas ---
            var ships = new List<string>();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                foreach (var link in _shipLinks)
                {
                    if (link.Ship == null || link.Ship.Departed) continue;
                    ships.Add(Pack(ShipLoadId(link.Ship).ToString(), NameOfPlayerId(link.PartnerId), NameOfPlayerId(link.OriginPlayerId), link.ShipId.ToString()));
                }
                foreach (var pending in _pendingShipRestores) ships.Add(Pack(pending));
            }
            Scribe_Collections.Look(ref ships, "rimcoopShipLinks", LookMode.Value);

            // --- registro de colonos (ver CoopSessionManager.Colonists) ---
            Scribe_Values.Look(ref _lineageId, "rimcoopLineageId");
            var uids = _pawnUids.Select(kv => kv.Key + "=" + kv.Value).ToList();
            Scribe_Collections.Look(ref uids, "rimcoopPawnUids", LookMode.Value);
            var gone = _pendingGoneUids.ToList();
            Scribe_Collections.Look(ref gone, "rimcoopPendingGone", LookMode.Value);

            if (Scribe.mode != LoadSaveMode.LoadingVars) return;

            _pawnUids = new Dictionary<int, string>();
            foreach (var s in uids ?? new List<string>())
            {
                var f = s.Split(new[] { '=' }, 2);
                if (f.Length == 2 && int.TryParse(f[0], out int id)) _pawnUids[id] = f[1];
            }
            _pendingGoneUids = gone ?? new List<string>();

            _pendingOffersSent.Clear();
            _sentOfferTicks.Clear();
            foreach (var rec in sent ?? new List<string>())
            {
                var f = Unpack(rec);
                if (f.Length < 4 || !int.TryParse(f[0], out int offerId) || !int.TryParse(f[2], out int tick)) continue;
                _pendingOffersSent[offerId] = new TradeMessagePayload { Kind = "offer", OfferId = offerId, Data = f[3] };
                _sentOfferTicks[offerId] = tick;
                _pendingOfferPartnerNames[offerId] = f[1];
            }

            _incomingOffers = new List<IncomingOffer>();
            foreach (var rec in incoming ?? new List<string>())
            {
                var f = Unpack(rec);
                if (f.Length < 4 || !int.TryParse(f[0], out int offerId)) continue;
                _incomingOffers.Add(new IncomingOffer { OfferId = offerId, FromName = f[1], GiveCsv = f[2], WantCsv = f[3] });
            }

            _incomingQuestInvites = new List<QuestMessagePayload>();
            foreach (var rec in invites ?? new List<string>())
            {
                var f = Unpack(rec);
                if (f.Length < 7 || !int.TryParse(f[1], out int questId)) continue;
                int.TryParse(f[4], out int state); int.TryParse(f[5], out int rating); int.TryParse(f[6], out int participants);
                _incomingQuestInvites.Add(new QuestMessagePayload { Kind = "invite", FromPlayerName = f[0], QuestId = questId, Name = f[2], Description = f[3], State = state, Rating = rating, Participants = participants });
            }

            _pendingShipRestores = new List<string[]>();
            foreach (var rec in ships ?? new List<string>())
            {
                var f = Unpack(rec);
                if (f.Length >= 4) _pendingShipRestores.Add(f);
            }
        }

        private readonly Dictionary<int, string> _pendingOfferPartnerNames = new Dictionary<int, string>();

        // ---- ofertas de comercio ----

        private void RegisterSentOffer(int offerId, TradeMessagePayload msg)
        {
            _pendingOffersSent[offerId] = msg;
            _sentOfferTicks[offerId] = Find.TickManager.TicksGame;
            _pendingOfferPartnerNames[offerId] = NameOfPlayerId(msg.ToPlayerId);
        }

        private void ForgetSentOffer(int offerId)
        {
            _pendingOffersSent.Remove(offerId);
            _sentOfferTicks.Remove(offerId);
            _pendingOfferPartnerNames.Remove(offerId);
        }

        private void PruneOldSentOffers()
        {
            int now = Find.TickManager.TicksGame;
            foreach (int id in _sentOfferTicks.Where(kv => now - kv.Value > SentOfferMaxAgeTicks).Select(kv => kv.Key).ToList())
                ForgetSentOffer(id);
        }

        private void AddIncomingOffer(int fromId, string fromName, int offerId, string giveCsv, string wantCsv)
        {
            _incomingOffers.RemoveAll(o => o.OfferId == offerId && o.FromName == fromName);
            _incomingOffers.Add(new IncomingOffer { OfferId = offerId, FromName = fromName, GiveCsv = giveCsv, WantCsv = wantCsv });
            Find.WindowStack.Add(new Dialog_TradeIncoming(fromId, fromName, offerId, giveCsv, wantCsv));
        }

        /// <summary>Respondí (acepté o rechacé) la oferta: ya no queda pendiente.</summary>
        public static void ResolveIncomingOffer(int offerId, string fromName)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            instance?._incomingOffers.RemoveAll(o => o.OfferId == offerId && o.FromName == fromName);
        }

        // ---- invitaciones a misión ----

        private void AddIncomingQuestInvite(QuestMessagePayload m)
        {
            _incomingQuestInvites.RemoveAll(q => q.FromPlayerName == m.FromPlayerName && q.QuestId == m.QuestId);
            _incomingQuestInvites.Add(m);
            Find.WindowStack.Add(new Dialog_QuestInvite(m));
        }

        public static void ResolveQuestInvite(QuestMessagePayload invite)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            instance?._incomingQuestInvites.RemoveAll(q => q.FromPlayerName == invite.FromPlayerName && q.QuestId == invite.QuestId);
        }

        /// <summary>El jugador está conectado: ¿se puede responderle ahora? (sus mensajes no se guardan si está desconectado)</summary>
        public static bool IsPlayerOnline(int playerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            return instance != null && instance._connectedPlayerIds.Contains(playerId);
        }

        // Cada tanto: vuelve a mostrar lo pendiente cuyo remitente está conectado y todavía no tiene ventana abierta.
        private void TickPendingInteractions()
        {
            if (!CoopClient.Instance.IsConnected || Current.ProgramState != ProgramState.Playing) return;

            PruneOldSentOffers();
            var windows = Find.WindowStack.Windows;

            foreach (var offer in _incomingOffers.ToList())
            {
                int fromId = PlayerIdByName(offer.FromName);
                if (fromId < 0 || !_connectedPlayerIds.Contains(fromId)) continue;
                if (windows.OfType<Dialog_TradeIncoming>().Any(w => w.OfferId == offer.OfferId)) continue;
                Find.WindowStack.Add(new Dialog_TradeIncoming(fromId, offer.FromName, offer.OfferId, offer.GiveCsv, offer.WantCsv));
            }

            foreach (var invite in _incomingQuestInvites.ToList())
            {
                int fromId = PlayerIdByName(invite.FromPlayerName);
                if (fromId < 0 || !_connectedPlayerIds.Contains(fromId)) continue;
                if (!IsCollaborator(fromId)) continue; // dejaron de colaborar: la invitación ya no vale
                if (windows.OfType<Dialog_QuestInvite>().Any(w => w.QuestId == invite.QuestId && w.FromName == invite.FromPlayerName)) continue;
                invite.FromPlayerId = fromId;
                Find.WindowStack.Add(new Dialog_QuestInvite(invite));
            }
        }

        // ---- naves compartidas ----

        /// <summary>Reconstruye los vínculos de naves guardados: la nave tiene que seguir en el mapa y el otro jugador tiene que estar conectado.</summary>
        private void TryRestoreShipLinks()
        {
            if (_pendingShipRestores.Count == 0) return;

            foreach (var rec in _pendingShipRestores.ToList())
            {
                if (!int.TryParse(rec[0], out int localLoadId) || !int.TryParse(rec[3], out int shipId)) { _pendingShipRestores.Remove(rec); continue; }

                TradeShip ship = null;
                foreach (var map in Find.Maps)
                {
                    ship = map.passingShipManager.passingShips.OfType<TradeShip>().FirstOrDefault(s => ShipLoadId(s) == localLoadId);
                    if (ship != null) break;
                }
                if (ship == null || ship.Departed) { _pendingShipRestores.Remove(rec); continue; } // la nave ya se fue: no hay nada que retomar

                int partnerId = PlayerIdByName(rec[1]);
                int originId = PlayerIdByName(rec[2]);
                if (partnerId < 0 || originId < 0) continue; // todavía no se conectó (o no lo conozco): se reintenta

                _pendingShipRestores.Remove(rec);
                if (_shipLinks.Any(l => l.Ship == ship && l.PartnerId == partnerId)) continue;
                _shipLinks.Add(new ShipLink { Ship = ship, PartnerId = partnerId, OriginPlayerId = originId, ShipId = shipId, LastSig = GoodsCsv(ship) });
                CoopLog.Message(Loc.T("SessionManager_Persistence.01", ship.name, rec[1]));
            }
        }

        /// <summary>Un participante de misiones compartidas volvió: se le reenvía la misión aunque no haya cambiado desde que se desconectó.</summary>
        private void ForceQuestResendFor(string playerName)
        {
            foreach (var kv in _sharedQuestParticipants)
                if (kv.Value.Contains(playerName)) _sharedQuestLastXml.Remove(kv.Key);
        }
    }
}
