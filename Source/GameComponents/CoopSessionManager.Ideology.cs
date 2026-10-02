using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimCoopMod.Networking;
using RimWorld;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>
    /// Ideología de la colonia de otro jugador (memes, preceptos y roles) para poder verla desde el mapa mundial. El objeto Ideo real
    /// no cruza entre partidas (es de la del dueño), así que viaja como un resumen en texto, y el que mira lo muestra en una ventana
    /// (Dialog_RemoteIdeo) con sus propios títulos.
    /// </summary>
    public partial class CoopSessionManager
    {
        // Lo manda el dueño de vez en cuando (y apenas alguien empieza a mirarlo), no en cada foto: casi nunca cambia.
        private bool _ideoSummaryDue = true;

        // (de quién es la base) -> último resumen recibido
        private readonly Dictionary<int, string> _remoteIdeoSummaries = new Dictionary<int, string>();

        public static string GetRemoteIdeoSummary(int hostPlayerId)
        {
            var instance = Current.Game?.GetComponent<CoopSessionManager>();
            return instance != null && instance._remoteIdeoSummaries.TryGetValue(hostPlayerId, out var text) ? text : null;
        }

        private static string CleanField(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        /// <summary>
        /// Líneas "X\tcampo[\tcampo]": N nombre, C cultura, M memes, P precepto (tema, texto), R rol (rol, quién lo tiene).
        /// Vacío si no hay Ideology o todavía no hay ideología principal.
        /// </summary>
        private static string BuildIdeoSummary()
        {
            if (!ModsConfig.IdeologyActive) return "";
            try
            {
                var ideo = Faction.OfPlayer?.ideos?.PrimaryIdeo;
                if (ideo == null) return "";

                var sb = new StringBuilder();
                sb.Append("N\t").Append(CleanField(ideo.name)).Append('\n');
                if (ideo.culture != null) sb.Append("C\t").Append(CleanField(ideo.culture.LabelCap)).Append('\n');
                if (ideo.memes != null && ideo.memes.Count > 0)
                    sb.Append("M\t").Append(CleanField(string.Join(", ", ideo.memes.Where(m => m != null).Select(m => m.LabelCap.ToString())))).Append('\n');

                foreach (var precept in ideo.PreceptsListForReading)
                {
                    if (precept?.def == null || !precept.def.visible || precept is Precept_Role) continue;
                    sb.Append("P\t").Append(CleanField(precept.def.issue?.LabelCap)).Append('\t').Append(CleanField(precept.LabelCap)).Append('\n');
                }

                foreach (var role in ideo.RolesListForReading)
                {
                    if (role == null) continue;
                    string roleName = role.def.LabelCap.ToString();
                    string flavor = role.LabelCap;
                    if (!string.IsNullOrEmpty(flavor) && flavor != roleName) roleName += " (" + flavor + ")";
                    string holders = string.Join(", ", role.ChosenPawns().Where(p => p != null).Select(p => p.LabelShortCap));
                    sb.Append("R\t").Append(CleanField(roleName)).Append('\t').Append(CleanField(holders)).Append('\n');
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>Lado del espejo: el resumen listo para mostrar, con los títulos en MI idioma.</summary>
        public static string FormatIdeoSummary(string summary)
        {
            if (string.IsNullOrEmpty(summary)) return "";
            var precepts = new List<string>();
            var roles = new List<string>();
            string name = null, culture = null, memes = null;

            foreach (var line in summary.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                switch (f[0])
                {
                    case "N": if (f.Length > 1) name = f[1]; break;
                    case "C": if (f.Length > 1) culture = f[1]; break;
                    case "M": if (f.Length > 1) memes = f[1]; break;
                    case "P": if (f.Length > 2) precepts.Add((f[1].Length > 0 ? f[1] + ": " : "") + f[2]); break;
                    case "R":
                        if (f.Length > 1) roles.Add(f[1] + ": " + (f.Length > 2 && f[2].Length > 0 ? f[2] : Loc.T("Ideo.Vacant")));
                        break;
                }
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(name)) sb.Append(name).Append("\n\n");
            if (!string.IsNullOrEmpty(culture)) sb.Append(Loc.T("Ideo.Culture", culture)).Append('\n');
            if (!string.IsNullOrEmpty(memes)) sb.Append(Loc.T("Ideo.Memes", memes)).Append('\n');
            if (precepts.Count > 0) sb.Append('\n').Append(Loc.T("Ideo.Precepts")).Append('\n').Append(string.Join("\n", precepts.Select(p => "  - " + p))).Append('\n');
            if (roles.Count > 0) sb.Append('\n').Append(Loc.T("Ideo.Roles")).Append('\n').Append(string.Join("\n", roles.Select(r => "  - " + r))).Append('\n');
            return sb.ToString();
        }
    }
}
