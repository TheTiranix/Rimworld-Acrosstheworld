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

        // ---- Avisos ----

        private enum DiploView { Neutral, Ally, WarPending, War }

        private DiploView ViewOf(int playerId)
        {
            var r = RelationOf(playerId);
            if (r == Relation.Ally) return DiploView.Ally;
            if (r == Relation.Hostile) return DiploView.War;
            return IsHostilePendingWith(playerId) ? DiploView.WarPending : DiploView.Neutral;
        }

        // Lo último que vi con cada uno (por nombre). La primera vez que se lo ve no hay carta: al conectar no se repite todo.
        private readonly Dictionary<string, DiploView> _lastViews = new Dictionary<string, DiploView>(System.StringComparer.OrdinalIgnoreCase);

        private void AnnounceRelationChanges()
        {
            foreach (var kv in _playerNames.ToList())
            {
                if (kv.Key == CoopClient.Instance.LocalPlayerId || string.IsNullOrEmpty(kv.Value)) continue;
                var now = ViewOf(kv.Key);
                if (!_lastViews.TryGetValue(kv.Value, out var before)) { _lastViews[kv.Value] = now; continue; }
                if (before == now) continue;
                _lastViews[kv.Value] = now;

                string name = kv.Value;
                switch (now)
                {
                    case DiploView.Ally: DiploLetter("Diplomacy.LetterAlly", name, LetterDefOf.PositiveEvent); break;
                    case DiploView.WarPending: DiploLetter("Diplomacy.LetterWarPending", name, LetterDefOf.ThreatSmall, HostileStartsInMinutes(kv.Key)); break;
                    case DiploView.War: DiploLetter("Diplomacy.LetterWar", name, LetterDefOf.ThreatBig); break;
                    case DiploView.Neutral:
                        DiploLetter(before == DiploView.Ally ? "Diplomacy.LetterBreak" : "Diplomacy.LetterPeace", name, LetterDefOf.NeutralEvent);
                        break;
                }
            }
        }

        private void DiploLetter(string key, string name, LetterDef def, int minutes = 0)
        {
            _relayingLetter = true; // es mía, no un evento de mi base: no se les reenvía a quienes la miran
            try { Find.LetterStack.ReceiveLetter(Loc.T(key + "Label", name, minutes), Loc.T(key + "Text", name, minutes), def); }
            finally { _relayingLetter = false; }
        }

        // Propuestas que me hicieron (alianza o paz) y todavía no contesté: se muestran como ventana, también al volver a conectar.
        private void ShowPendingProposals()
        {
            if (!_diplomacyKnown) return;
            string me = CoopClient.Instance.LocalPlayerName;
            var open = Find.WindowStack.Windows.OfType<Dialog_DiplomacyProposal>().ToList();
            foreach (var kv in _playerNames.ToList())
            {
                if (kv.Key == CoopClient.Instance.LocalPlayerId || !_connectedPlayerIds.Contains(kv.Key)) continue;
                foreach (bool peace in new[] { false, true })
                {
                    string from = PendingFrom(kv.Key, peace);
                    if (!Same(from, kv.Value) || Same(from, me)) continue;
                    if (open.Any(w => w.FromPlayerId == kv.Key && w.Peace == peace)) continue;
                    if (IsSnoozed(DiploSnoozeKey(kv.Value, peace))) continue;
                    Find.WindowStack.Add(new Dialog_DiplomacyProposal(kv.Key, kv.Value, peace));
                }
            }
        }

        private static string DiploSnoozeKey(string name, bool peace) => (peace ? "dp:" : "da:") + name;
        public static void SnoozeDiplomacy(string name, bool peace) => Snooze(DiploSnoozeKey(name, peace));

        /// <summary>Color del punto de la base en el mapa mundial según la relación (ver CoopPlayerBase.ExpandingIconColor).</summary>
        public static Color? RelationColor(int playerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || !instance._diplomacyKnown) return null;
            switch (instance.ViewOf(playerId))
            {
                case DiploView.Ally: return new Color(0.4f, 0.9f, 0.4f);
                case DiploView.WarPending: return new Color(1f, 0.65f, 0.15f);
                case DiploView.War: return new Color(0.95f, 0.3f, 0.25f);
                default: return null; // neutral: el celeste de siempre (el del def)
            }
        }

        /// <summary>"Relación: aliado / neutral / guerra en N min / en guerra" para el panel de la base.</summary>
        public static string RelationLine(int playerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            if (instance == null || !instance._diplomacyKnown) return null;
            var view = instance.ViewOf(playerId);
            string state = view == DiploView.WarPending ? Loc.T("Diplomacy.View.WarPending", HostileStartsInMinutes(playerId)) : Loc.T("Diplomacy.View." + view);
            return Loc.T("Diplomacy.RelationLine", state);
        }

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
            AnnounceRelationChanges(); // por ejemplo, el fin del aviso de guerra
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
            AnnounceRelationChanges();
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
