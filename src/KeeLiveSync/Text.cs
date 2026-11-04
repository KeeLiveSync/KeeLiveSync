using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace KeeLiveSync
{
    /// <summary>
    /// Sichtbare Texte. Deutsch und Englisch stehen im Code (T("deutsch", "english")); weitere Sprachen liegen als
    /// Languages/&lt;iso&gt;.json eingebettet in der DLL und übersetzen den englischen Text. Maßgeblich ist die
    /// Sprache von KeePass, ohne Übersetzung die Windows-Sprache; fehlt ein Text, erscheint Englisch.
    /// </summary>
    internal static class Text
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Loaded =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>ISO-639-1-Code der Anzeigesprache ("de", "en", "fr" …)</summary>
        internal static string Language
        {
            get
            {
                try
                {
                    var t = KeePass.Program.Translation;
                    string code = t != null && t.Properties != null ? t.Properties.Iso6391Code : null;
                    if (!string.IsNullOrEmpty(code)) return code.ToLowerInvariant();
                }
                catch (Exception)
                {
                    // ältere KeePass-Versionen: Windows-Sprache
                }
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            }
        }

        internal static string T(string de, string en)
        {
            string lang = Language;
            if (lang == "de") return de;
            if (lang == "en") return en;
            return Lookup(lang, en) ?? en;
        }

        /// <summary>Text mit Platzhaltern {0}, {1} …</summary>
        internal static string T(string de, string en, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, T(de, en), args);
        }

        /// <summary>
        /// Einzahl/Mehrzahl mit Platzhalter {0} für die Anzahl. Sprachen mit weiterer Form (Polnisch: 2–4, 22–24 …)
        /// verwenden in der Sprachdatei den Schlüssel "&lt;englische Mehrzahl&gt;#few".
        /// </summary>
        internal static string Plural(long n, string deOne, string deMany, string enOne, string enMany)
        {
            string lang = Language;
            string text;
            if (lang == "de") text = n == 1 ? deOne : deMany;
            else if (lang == "en") text = n == 1 ? enOne : enMany;
            else
            {
                text = null;
                if (n != 1 && IsFew(lang, n)) text = Lookup(lang, enMany + "#few");
                if (text == null) text = n == 1 ? (Lookup(lang, enOne) ?? enOne) : (Lookup(lang, enMany) ?? enMany);
            }
            return string.Format(CultureInfo.CurrentCulture, text, n);
        }

        private static bool IsFew(string lang, long n)
        {
            if (lang != "pl") return false;
            long m10 = n % 10, m100 = n % 100;
            return m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14);
        }

        private static string Lookup(string lang, string en)
        {
            Dictionary<string, string> map;
            lock (Loaded)
            {
                if (!Loaded.TryGetValue(lang, out map))
                {
                    map = Load(lang);
                    Loaded[lang] = map;
                }
            }
            string s;
            return map != null && map.TryGetValue(en, out s) && !string.IsNullOrEmpty(s) ? s : null;
        }

        private static Dictionary<string, string> Load(string lang)
        {
            try
            {
                var asm = typeof(Text).Assembly;
                using (var stream = asm.GetManifestResourceStream("KeeLiveSync.Languages." + lang + ".json"))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream))
                    {
                        var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        var map = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var kv in raw)
                            if (!kv.Key.StartsWith("_", StringComparison.Ordinal)) map[kv.Key] = kv.Value as string;
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Sprachdatei " + lang + " nicht lesbar: " + Log.Describe(ex));
                return null;
            }
        }

        /// <summary>Kultur für Datums- und Zahlenformate passend zur Anzeigesprache</summary>
        internal static CultureInfo Culture
        {
            get
            {
                try
                {
                    return CultureInfo.GetCultureInfo(Language);
                }
                catch (CultureNotFoundException)
                {
                    return CultureInfo.CurrentCulture;
                }
            }
        }

        /// <summary>„vor 3 Minuten“, „gestern 14:05“ …</summary>
        internal static string Ago(DateTime local)
        {
            if (local == DateTime.MinValue) return T("noch nie", "never");
            var span = DateTime.Now - local;
            if (span.TotalSeconds < 60) return T("gerade eben", "just now");
            if (span.TotalMinutes < 60)
            {
                int m = (int)span.TotalMinutes;
                return Plural(m, "vor {0} Minute", "vor {0} Minuten", "{0} minute ago", "{0} minutes ago");
            }
            string time = local.ToString("t", Culture);
            if (local.Date == DateTime.Today) return T("heute {0}", "today {0}", time);
            if (local.Date == DateTime.Today.AddDays(-1)) return T("gestern {0}", "yesterday {0}", time);
            return DateAndTime(local);
        }

        internal static string DateAndTime(DateTime local)
        {
            return local.ToString("g", Culture);
        }

        internal static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", Culture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#", Culture) + " MB";
        }
    }
}
