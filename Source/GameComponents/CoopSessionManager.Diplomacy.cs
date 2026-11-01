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

        /// <summary>Quién propuso alianza (o paz) con ese jugador y espera respuesta: "" si nadie.</summary>
        public static string PendingFrom(int playerId, bool peace)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || !instance._playerNames.TryGetValue(playerId, out var name)) return "";
            string me = CoopClient.Instance.LocalPlayerName;
            var e = instance._diplomacy.Entries.FirstOrDefault(x => Same(x.A, me) && Same(x.B, name) || Same(x.A, name) && Same(x.B, me));
            return (peace ? e?.PendingPeaceFrom : e?.PendingAllyFrom) ?? "";
        }

        /// <summary>Minutos que faltan para que valga la guerra declarada con ese jugador (0 si no hay una en aviso).</summary>
        public static int HostileStartsInMinutes(int playerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || !instance._playerNames.TryGetValue(playerId, out var name)) return 0;
            string me = CoopClient.Instance.LocalPlayerName;
            var e = instance._diplomacy.Entries.FirstOrDefault(x => Same(x.A, me) && Same(x.B, name) || Same(x.A, name) && Same(x.B, me));
            if (e == null || !instance._diplomacy.IsHostilePending(me, name, instance.ServerNow())) return 0;
            long left = e.HostileSinceUnix + DiplomacyTable.HostileWarningSeconds - instance.ServerNow();
            return Mathf.Max(1, Mathf.CeilToInt(left / 60f));
        }

        private static bool Same(string a, string b) => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);

        // ---- Lo que cada relación permite (se valida en quien recibe el pedido, no solo en los botones) ----

        // Un hostil no mira mi base: ni entrar ni seguir mirando cuando empieza la guerra.
        private bool RefuseHostileWatcher(int playerId)
        {
            if (RelationOf(playerId) != Relation.Hostile) return false;
            TellOnce(playerId, "watch", Loc.Wire("Diplomacy.NoWatchHostile", CoopClient.Instance.LocalPlayerName));
            return true;
        }

        // Un aviso por jugador y tema cada tanto: cada plano o edición rechazados mandaban su propio cartel.
        private readonly Dictionary<string, float> _toldAt = new Dictionary<string, float>();
        private void TellOnce(int playerId, string topic, string wireMessage)
        {
            string key = playerId + ":" + topic;
            if (_toldAt.TryGetValue(key, out float at) && Time.realtimeSinceStartup - at < 10f) return;
            _toldAt[key] = Time.realtimeSinceStartup;
            CoopClient.Instance.SendJoinResult(playerId, false, wireMessage);
        }

        private static void DrawViewOnlyNotice()
        {
            var rect = new Rect(Verse.UI.screenWidth / 2f - 260f, 40f, 520f, 26f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.45f));
            Widgets.Label(rect, Loc.T("Diplomacy.ViewOnly"));
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // La guerra empieza a valer sola (al terminar el aviso), sin paquete nuevo: se revisa cada tanto.
        private float _nextRelationCheck;
        private void EnforceRelationsPeriodically()
        {
            if (Time.realtimeSinceStartup < _nextRelationCheck) return;
            _nextRelationCheck = Time.realtimeSinceStartup + 2f;
            EnforceRelations();
        }

        private void EnforceRelations()
        {
            // Dueño: los que ahora están en guerra conmigo dejan de recibir fotos de mi base.
            foreach (int w in _watchers.ToList())
            {
                if (RelationOf(w) != Relation.Hostile) continue;
                _watchers.Remove(w);
                _sentThingSigs.Remove(w);
                _sentAppearances.Remove(w);
            }
            // Espectador: si estaba mirando la base de alguien con quien empezó la guerra, se cierra ese espejo.
            foreach (int host in _coopMaps.Keys.ToList())
            {
                if (RelationOf(host) != Relation.Hostile || !_playerNames.TryGetValue(host, out var name)) continue;
                if (_remoteBases.TryGetValue(name, out var wobj) && wobj != null) DropMirrorMap(wobj, host);
            }
        }

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
            EnforceRelations();
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
