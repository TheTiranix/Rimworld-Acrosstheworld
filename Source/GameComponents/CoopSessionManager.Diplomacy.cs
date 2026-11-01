using System.Collections.Generic;
using System.Linq;
using RimCoopMod.Networking;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Diplomacia del lado del jugador: el servidor manda la tabla entera (DiplomacyState) y acá solo se consulta y se piden
    /// cambios. "Colaborador" ahora es "aliado según el servidor" (máximo DiplomacyTable.MaxAllies): la lista vieja guardada en la
    /// partida (_collaboratorNames) se usa una vez, para proponerle alianza a quienes ya colaboraban.
    /// </summary>
    public partial class CoopSessionManager
    {
        private readonly DiplomacyTable _diplomacy = new DiplomacyTable();
        private long _serverNowAtReceipt;
        private float _diplomacyReceivedAt;
        private bool _diplomacyKnown;
        private readonly HashSet<string> _toMigrate = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // La hora del servidor ahora (el aviso de hostilidad se cuenta con su reloj, no con el de cada juego).
        private long ServerNow() => _serverNowAtReceipt + (long)(Time.realtimeSinceStartup - _diplomacyReceivedAt);

        private Relation RelationOf(int playerId) =>
            _playerNames.TryGetValue(playerId, out var name) ? _diplomacy.Get(CoopClient.Instance.LocalPlayerName, name, ServerNow()) : Relation.Neutral;

        public static Relation RelationWith(int playerId) => Current.Game?.GetComponent<CoopSessionManager>()?.RelationOf(playerId) ?? Relation.Neutral;

        public static bool IsHostilePendingWith(int playerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            return instance != null && instance._playerNames.TryGetValue(playerId, out var name)
                && instance._diplomacy.IsHostilePending(CoopClient.Instance.LocalPlayerName, name, instance.ServerNow());
        }

        public static int LocalAllyCount() => Current.Game?.GetComponent<CoopSessionManager>()?._diplomacy.AllyCount(CoopClient.Instance.LocalPlayerName) ?? 0;

        public static void RequestDiplomacy(int playerId, string kind) => CoopClient.Instance.SendDiplomacy(playerId, kind);

        private void HandleDiplomacyState(DiplomacyStatePayload s)
        {
            var before = _playerNames.Keys.ToDictionary(id => id, RelationOf);

            _diplomacy.Replace(s.Entries);
            _serverNowAtReceipt = s.ServerNowUnix;
            _diplomacyReceivedAt = Time.realtimeSinceStartup;

            // Recién aliados: empieza a compartirse la investigación (como antes al empezar a colaborar).
            foreach (var kv in before)
                if (kv.Value != Relation.Ally && RelationOf(kv.Key) == Relation.Ally && _connectedPlayerIds.Contains(kv.Key)) SendResearchFullTo(kv.Key);

            if (!_diplomacyKnown)
            {
                _diplomacyKnown = true;
                StartCollaboratorMigration();
            }
            TryMigrateCollaborators();
        }

        // Quienes colaboraban en versiones anteriores siguen siendo aliados sin hacer nada: cada lado le propone alianza al otro (con
        // los dos propuestas queda formada). Hasta el máximo; a los que sobran se les avisa que quedan neutrales.
        private void StartCollaboratorMigration()
        {
            string me = CoopClient.Instance.LocalPlayerName;
            int room = DiplomacyTable.MaxAllies - _diplomacy.AllyCount(me);
            var over = new List<string>();
            foreach (var name in _collaboratorNames.OrderBy(n => n))
            {
                if (_diplomacy.Get(me, name, ServerNow()) == Relation.Ally) continue;
                if (room <= 0) { over.Add(name); continue; }
                _toMigrate.Add(name);
                room--;
            }
            if (over.Count > 0) Messages.Message(Loc.T("Diplomacy.MigrationCapped", string.Join(", ", over)), MessageTypeDefOf.CautionInput, false);
        }

        // Se reintenta cuando se conoce el id de cada uno (el pedido al servidor va por id).
        private void TryMigrateCollaborators()
        {
            if (_toMigrate.Count == 0) return;
            foreach (var name in _toMigrate.ToList())
            {
                int id = PlayerIdByName(name);
                if (id < 0) continue;
                _toMigrate.Remove(name);
                if (RelationOf(id) == Relation.Neutral) RequestDiplomacy(id, "ally");
            }
        }
    }
}
