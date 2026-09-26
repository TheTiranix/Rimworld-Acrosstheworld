using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimCoopMod
{
    /// <summary>
    /// Traducciones del mod y del servidor (español / inglés). Un solo sistema compartido por los dos: los textos viven en
    /// Strings.g.cs (claves -> texto por idioma) y se piden con Loc.T("clave", argumentos...). El idioma se elige en las opciones
    /// del mod (o con "auto": el idioma del juego) y en el servidor con --idioma / el comando "idioma".
    /// </summary>
    public static class Loc
    {
        public const string English = "en";
        public const string Spanish = "es";

        private static string _language = Spanish;
        public static string Language => _language;
        public static bool IsSpanish => _language == Spanish;

        /// <summary>Se dispara al cambiar de idioma (para refrescar textos que se guardaron en algún lado, como los defs).</summary>
        public static event Action LanguageChanged;

        public static void SetLanguage(string language)
        {
            string wanted = Normalize(language);
            if (wanted == _language) return;
            _language = wanted;
            LanguageChanged?.Invoke();
        }

        /// <summary>"es", "es-AR", "Spanish (Español)" -> "es"; cualquier otra cosa -> "en".</summary>
        public static string Normalize(string language)
        {
            if (string.IsNullOrEmpty(language)) return English;
            string l = language.Trim().ToLowerInvariant();
            return l.StartsWith("es") || l.StartsWith("spanish") || l.StartsWith("espa") ? Spanish : English;
        }

        /// <summary>El idioma del sistema operativo: español si es español, si no inglés.</summary>
        public static string SystemLanguage() => Normalize(CultureInfo.CurrentUICulture.Name);

        private static string Lookup(string key, string language)
        {
            var table = language == Spanish ? Strings.Es : Strings.En;
            return table.TryGetValue(key, out var text) ? text : null;
        }

        /// <summary>El texto de esa clave en el idioma actual (con "{0}", "{1}"... reemplazados por los argumentos).</summary>
        public static string T(string key, params object[] args) => Format(Template(key, _language), args);

        /// <summary>Igual que T pero en un idioma puntual (para armar textos para otro jugador).</summary>
        public static string In(string language, string key, params object[] args) => Format(Template(key, Normalize(language)), args);

        private static string Template(string key, string language)
        {
            // Si falta en el idioma pedido se usa el otro, y si no existe en ninguno se muestra la clave (así se nota el faltante).
            return Lookup(key, language) ?? Lookup(key, language == Spanish ? English : Spanish) ?? key;
        }

        private static string Format(string template, object[] args)
        {
            if (args == null || args.Length == 0) return template;
            try { return string.Format(CultureInfo.CurrentCulture, template, args); }
            catch (FormatException) { return template; }
        }

        /// <summary>"texto en español / text in English": para mensajes que el servidor manda a jugadores de cualquier idioma.</summary>
        public static string Both(string key, params object[] args) =>
            Format(Template(key, Spanish), args) + " / " + Format(Template(key, English), args);

        // ---- Textos que viajan por la red y se traducen en el que los recibe ----
        // Quien arma un mensaje para OTRO jugador (ej. el resultado de un traspaso de colonos) no sabe en qué idioma juega el otro:
        // manda la clave y los argumentos, y el que lo recibe lo traduce con su idioma (Unwire).

        private const char WireStart = '\u0001';
        private const char WireSep = '\u0002';

        public static string Wire(string key, params object[] args)
        {
            var sb = new StringBuilder();
            sb.Append(WireStart).Append(key);
            if (args != null)
                foreach (var a in args) sb.Append(WireSep).Append(Convert.ToString(a, CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Si el texto viene de Wire lo traduce; si es un texto común (de una versión vieja, o ya armado) lo devuelve tal cual.</summary>
        public static string Unwire(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != WireStart) return text;
            var parts = text.Substring(1).Split(WireSep);
            var args = new object[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
            return T(parts[0], args);
        }
    }
}
