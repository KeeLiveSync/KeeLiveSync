using System;
using System.Globalization;

namespace KeeLiveSync
{
    /// <summary>
    /// Sichtbare Texte auf Deutsch oder Englisch. Maßgeblich ist die Sprache von KeePass, ohne Übersetzung die Windows-Sprache.
    /// </summary>
    internal static class Text
    {
        internal static bool German
        {
            get
            {
                try
                {
                    var t = KeePass.Program.Translation;
                    string code = t != null && t.Properties != null ? t.Properties.Iso6391Code : null;
                    if (!string.IsNullOrEmpty(code)) return code.Equals("de", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    // ältere KeePass-Versionen: Windows-Sprache
                }
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
            }
        }

        internal static string T(string de, string en)
        {
            return German ? de : en;
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
                return German ? "vor " + m + (m == 1 ? " Minute" : " Minuten") : m + (m == 1 ? " minute ago" : " minutes ago");
            }
            if (local.Date == DateTime.Today) return T("heute ", "today ") + local.ToString("HH:mm");
            if (local.Date == DateTime.Today.AddDays(-1)) return T("gestern ", "yesterday ") + local.ToString("HH:mm");
            return local.ToString(German ? "dd.MM.yyyy HH:mm" : "yyyy-MM-dd HH:mm");
        }

        internal static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
        }
    }
}
