using System;
using System.Collections.Generic;
using System.Linq;
using RimCoopMod.Networking;
using RimCoopMod.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Colonos enviados entre jugadores sin duplicarlos ni perderlos aunque alguien cargue una partida vieja.
    ///
    /// Un colono enviado con "Colaborar" existe en UNA sola partida a la vez, pero cada jugador guarda la suya por separado:
    ///  - si el que lo mandó carga una partida de ANTES de mandarlo, lo tiene en casa mientras el otro también lo tiene (duplicado);
    ///  - si el que lo recibió carga una partida de ANTES de recibirlo, el colono ya no está en ninguna (perdido).
    /// El servidor recuerda quién tiene cada colono (con su copia, ver ColonistRecord). Al conectarse, cada jugador recibe el
    /// "manifiesto": los que le tocan y los que están en otro lado. Si tiene en casa uno que está en otro lado, se saca esa copia
    /// (la que está afuera es la vigente); si le toca uno que no tiene en ningún lado, se lo reconstruye desde la copia del servidor.
    /// </summary>
    public partial class CoopSessionManager
    {
        // Identifica a la "línea" de esta colonia (un juego nuevo genera otra): sin esto, un juego nuevo del mismo jugador en la
        // misma partida del servidor chocaría con el registro del anterior (mismo nombre, mismos números de pawn) y le borraría colonos.
        private string _lineageId;

        // thingIDNumber local -> id estable del colono. Solo tiene a los que viajaron (los propios se calculan al vuelo, ver UidOf).
        private Dictionary<int, string> _pawnUids = new Dictionary<int, string>();

        // Colonos que murieron/se fueron mientras estaba desconectado: se avisa al servidor cuando vuelvo.
        private List<string> _pendingGoneUids = new List<string>();

        private ColonistManifestPayload _pendingManifest;
        private float _nextLedgerCheck;

        private string LineageId() => _lineageId ?? (_lineageId = Guid.NewGuid().ToString("N").Substring(0, 12));

        // Se crea apenas empieza o se carga la partida, así queda guardada desde el próximo guardado (si se generara recién al mandar
        // un colono, una partida guardada antes de mandarlo tendría OTRA línea y la copia duplicada no se reconocería).
        public override void StartedNewGame() { base.StartedNewGame(); LineageId(); }
        public override void LoadedGame() { base.LoadedGame(); LineageId(); }

        /// <summary>
        /// Id estable de un colono para el registro del servidor, o null si no se lo sigue: los títeres del mapa espejo no cuentan y
        /// un colono que ya me habían prestado antes de que existiera el registro no tiene id de origen (queda como antes).
        /// </summary>
        public static string UidOf(Pawn pawn)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || pawn == null || PuppetPawnRegistry.IsPuppet(pawn)) return null;

            if (instance._pawnUids.TryGetValue(pawn.thingIDNumber, out var uid)) return uid;
            if (GetOwnerName(pawn) != null) return null;

            string me = CoopClient.Instance.LocalPlayerName;
            if (string.IsNullOrEmpty(me)) return null;
            return me + "~" + instance.LineageId() + "~" + pawn.thingIDNumber;
        }

        // ---- bajas ----

        /// <summary>Un colono mío murió: si estaba en el registro se da de baja para que no se lo "reviva" desde la copia.</summary>
        public static void OnColonistKilled(Pawn pawn)
        {
            if (pawn == null || !pawn.RaceProps.Humanlike || pawn.Faction != Faction.OfPlayer) return;
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null) return;

            string uid = UidOf(pawn);
            if (uid == null) return;
            if (!instance._pendingGoneUids.Contains(uid)) instance._pendingGoneUids.Add(uid);
            instance.FlushPendingGone();
        }

        private void FlushPendingGone()
        {
            if (_pendingGoneUids.Count == 0 || !CoopClient.Instance.IsConnected) return;
            CoopClient.Instance.SendColonistGone(new List<string>(_pendingGoneUids));
            _pendingGoneUids.Clear();
        }

        // ---- reconciliación ----

        private void HandleColonistManifest(ColonistManifestPayload manifest) => _pendingManifest = manifest;

        /// <summary>Corre cada tanto (también en pausa): cuando ya hay colonia, se aplica el manifiesto pendiente.</summary>
        private void UpdateColonistLedger()
        {
            if (Time.realtimeSinceStartup < _nextLedgerCheck) return;
            _nextLedgerCheck = Time.realtimeSinceStartup + 2f;

            if (!CoopClient.Instance.IsConnected || Current.ProgramState != ProgramState.Playing) return;
            FlushPendingGone();

            if (_pendingManifest == null || LocalBaseMap == null) return;
            var manifest = _pendingManifest;
            _pendingManifest = null;
            try { ReconcileColonists(manifest); }
            catch (Exception e) { CoopLog.Error(Loc.T("SessionManager_Colonists.01", e)); }
        }

        private void ReconcileColonists(ColonistManifestPayload manifest)
        {
            var elsewhere = new HashSet<string>(manifest.Elsewhere);

            // 1) Duplicados: tengo en casa a alguien que el servidor dice que está en otro lado (mi partida era anterior a mandarlo).
            foreach (var pawn in PawnsFinder.AllMaps_FreeColonists.ToList())
            {
                if (pawn.Map != null && GetHostPlayerIdForMap(pawn.Map) >= 0) continue; // mapa espejo de otro jugador
                string uid = UidOf(pawn);
                if (uid == null || !elsewhere.Contains(uid)) continue;
                RemoveDuplicateColonist(pawn, uid);
            }

            if (manifest.Hold.Count == 0) return;

            // 2) Perdidos: el servidor dice que me tocan y no los tengo en ningún lado (ni vivos ni muertos).
            var existing = new HashSet<string>();
            foreach (var pawn in PawnsFinder.All_AliveOrDead)
            {
                string uid = UidOf(pawn);
                if (uid != null) existing.Add(uid);
            }

            foreach (var entry in manifest.Hold)
            {
                if (existing.Contains(entry.Uid) || _pendingGoneUids.Contains(entry.Uid)) continue;
                RestoreColonist(entry);
            }
        }

        private void RemoveDuplicateColonist(Pawn pawn, string uid)
        {
            try
            {
                string name = pawn.LabelShortCap;
                _pawnOwners.Remove(pawn.thingIDNumber);
                _pawnOwnerNames.Remove(pawn.thingIDNumber);
                _pawnUids.Remove(pawn.thingIDNumber);

                PawnTransfer.SuppressRelationLossOnDestroy = true; // no se murió: no hay que meterle pensamientos de duelo a los demás
                try { pawn.Destroy(DestroyMode.Vanish); }
                finally { PawnTransfer.SuppressRelationLossOnDestroy = false; }

                CoopLog.Message(Loc.T("SessionManager_Colonists.02", name, uid));
                Messages.Message(Loc.T("SessionManager_Colonists.03", name), MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                CoopLog.Warning(Loc.T("SessionManager_Colonists.04", uid, e.Message));
            }
        }

        private void RestoreColonist(ColonistManifestEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Blob)) return;
            CoopLog.Message(Loc.T("SessionManager_Colonists.05", entry.Uid));
            var req = new JoinRequestPayload { FromPlayerId = -1, ToPlayerId = CoopClient.Instance.LocalPlayerId };
            req.SerializedPawns.Add(entry.Blob);
            req.OwnerNames.Add(entry.OwnerName ?? "");
            req.Uids.Add(entry.Uid);
            HandleJoinRequest(req, restoring: true);
        }
    }
}
