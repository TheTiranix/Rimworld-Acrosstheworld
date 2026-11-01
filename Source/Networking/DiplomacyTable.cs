using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RimCoopMod.Networking
{
    public enum Relation : byte { Neutral = 0, Ally = 1, Hostile = 2 }

    /// <summary>Relación entre dos jugadores (por nombre: es lo estable entre sesiones, igual que los ids del servidor).</summary>
    public class DiplomacyEntry
    {
        public string A;
        public string B;
        public Relation State;
        public long HostileSinceUnix;  // cuándo se declaró: la hostilidad vale recién HostileWarningSeconds después
        public string PendingAllyFrom = "";  // quién propuso alianza y espera que el otro la acepte
        public string PendingPeaceFrom = "";
    }

    /// <summary>
    /// Diplomacia entre jugadores: aliado, neutral u hostil. La guarda el servidor (es la misma para los dos lados) y la reparte
    /// a todos; los clientes solo la consultan. Sin nada de RimWorld para poder probarla sola (tests/DiplomacyTests).
    ///  - Alianza: la proponen los dos (una propuesta queda pendiente hasta que el otro también la haga). Máximo MaxAllies por
    ///    jugador: son las colonias con las que se colabora a la vez.
    ///  - Hostilidad: la declara uno solo, rompe la alianza si había, y vale recién después de un aviso (para que el otro se entere
    ///    antes de que lo puedan atacar). La paz también la tienen que proponer los dos.
    /// Los métodos que cambian algo devuelven null si salió bien o la clave de texto (Loc) con el motivo.
    /// </summary>
    public class DiplomacyTable
    {
        public const int MaxAllies = 4;
        public const int HostileWarningSeconds = 300;

        private readonly int _warningSeconds;
        private readonly Dictionary<string, DiplomacyEntry> _pairs = new Dictionary<string, DiplomacyEntry>();

        public DiplomacyTable(int hostileWarningSeconds = HostileWarningSeconds) { _warningSeconds = hostileWarningSeconds; }

        public List<DiplomacyEntry> Entries => _pairs.Values.ToList();

        public Relation Get(string a, string b, long nowUnix)
        {
            var e = Find(a, b);
            if (e == null) return Relation.Neutral;
            if (e.State == Relation.Hostile && nowUnix - e.HostileSinceUnix < _warningSeconds) return Relation.Neutral;
            return e.State;
        }

        public bool IsHostilePending(string a, string b, long nowUnix)
        {
            var e = Find(a, b);
            return e != null && e.State == Relation.Hostile && nowUnix - e.HostileSinceUnix < _warningSeconds;
        }

        public int AllyCount(string name) => _pairs.Values.Count(e => e.State == Relation.Ally && (Same(e.A, name) || Same(e.B, name)));

        public string ProposeAlliance(string from, string to)
        {
            if (Same(from, to)) return "Diplomacy.Self";
            var e = GetOrCreate(from, to);
            if (e.State == Relation.Ally) return null;
            if (e.State == Relation.Hostile) return "Diplomacy.HostileFirst";
            if (AllyCount(from) >= MaxAllies || AllyCount(to) >= MaxAllies)
            {
                e.PendingAllyFrom = ""; // no queda colgada una propuesta que no se puede cumplir
                Cleanup(e);
                return "Diplomacy.MaxAllies";
            }
            if (Same(e.PendingAllyFrom, to))
            {
                e.State = Relation.Ally;
                e.PendingAllyFrom = "";
                return null;
            }
            e.PendingAllyFrom = from;
            return null;
        }

        public string DeclareHostile(string from, string to, long nowUnix)
        {
            if (Same(from, to)) return "Diplomacy.Self";
            var e = GetOrCreate(from, to);
            if (e.State == Relation.Hostile) return null; // ya lo era: el aviso no vuelve a empezar
            e.State = Relation.Hostile;
            e.HostileSinceUnix = nowUnix;
            e.PendingAllyFrom = "";
            e.PendingPeaceFrom = "";
            return null;
        }

        public string ProposePeace(string from, string to)
        {
            var e = Find(from, to);
            if (e == null || e.State != Relation.Hostile) return "Diplomacy.NotHostile";
            if (Same(e.PendingPeaceFrom, to))
            {
                e.State = Relation.Neutral;
                e.PendingPeaceFrom = "";
                Cleanup(e);
                return null;
            }
            e.PendingPeaceFrom = from;
            return null;
        }

        public string BreakAlliance(string from, string to)
        {
            var e = Find(from, to);
            if (e == null || e.State != Relation.Ally) return "Diplomacy.NotAllied";
            e.State = Relation.Neutral;
            Cleanup(e);
            return null;
        }

        /// <summary>"from" rechaza la propuesta (de alianza o de paz) que le hizo "to": se borra y el otro puede volver a proponer.</summary>
        public string Decline(string from, string to)
        {
            var e = Find(from, to);
            if (e != null && Same(e.PendingAllyFrom, to)) e.PendingAllyFrom = "";
            else if (e != null && Same(e.PendingPeaceFrom, to)) e.PendingPeaceFrom = "";
            else return "Diplomacy.NothingToDecline";
            Cleanup(e);
            return null;
        }

        /// <summary>Reemplaza todo por lo que mandó el servidor (lado cliente).</summary>
        public void Replace(IEnumerable<DiplomacyEntry> entries)
        {
            _pairs.Clear();
            foreach (var e in entries ?? Enumerable.Empty<DiplomacyEntry>())
                if (!string.IsNullOrEmpty(e.A) && !string.IsNullOrEmpty(e.B) && !Same(e.A, e.B)) _pairs[Key(e.A, e.B)] = e;
        }

        // Una línea por par: a|b|estado|hostilDesde|propuestaAlianza|propuestaPaz ('|' y '\' van escapados).
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, _pairs.Values.Select(e => string.Join("|",
                Esc(e.A), Esc(e.B), ((int)e.State).ToString(), e.HostileSinceUnix.ToString(), Esc(e.PendingAllyFrom), Esc(e.PendingPeaceFrom))));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path); // escribir aparte y reemplazar: un corte a mitad no deja el archivo roto
        }

        public void Load(string path)
        {
            _pairs.Clear();
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var f = line.Split('|');
                if (f.Length < 6 || !int.TryParse(f[2], out int state) || !long.TryParse(f[3], out long since)) continue;
                var e = new DiplomacyEntry
                {
                    A = Unesc(f[0]), B = Unesc(f[1]), State = (Relation)state, HostileSinceUnix = since,
                    PendingAllyFrom = Unesc(f[4]), PendingPeaceFrom = Unesc(f[5])
                };
                if (!string.IsNullOrEmpty(e.A) && !string.IsNullOrEmpty(e.B)) _pairs[Key(e.A, e.B)] = e;
            }
        }

        private DiplomacyEntry Find(string a, string b) =>
            string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) ? null : _pairs.TryGetValue(Key(a, b), out var e) ? e : null;

        private DiplomacyEntry GetOrCreate(string a, string b)
        {
            string key = Key(a, b);
            if (!_pairs.TryGetValue(key, out var e)) _pairs[key] = e = new DiplomacyEntry { A = a, B = b };
            return e;
        }

        // Un par neutral sin nada pendiente es lo mismo que no tenerlo: no se guarda.
        private void Cleanup(DiplomacyEntry e)
        {
            if (e.State == Relation.Neutral && string.IsNullOrEmpty(e.PendingAllyFrom) && string.IsNullOrEmpty(e.PendingPeaceFrom))
                _pairs.Remove(Key(e.A, e.B));
        }

        private static string Key(string a, string b)
        {
            string x = a.ToLowerInvariant(), y = b.ToLowerInvariant();
            return string.CompareOrdinal(x, y) <= 0 ? x + "\u0001" + y : y + "\u0001" + x;
        }

        private static bool Same(string a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("|", "\\p").Replace("\n", "\\n").Replace("\r", "");

        private static string Unesc(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 'p' ? '|' : c == 'n' ? '\n' : c);
            }
            return sb.ToString();
        }
    }
}
