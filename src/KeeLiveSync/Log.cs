using System;
using System.IO;
using System.Text;
using System.Threading;

namespace KeeLiveSync
{
    /// <summary>
    /// Protokoll in %APPDATA%\KeeLiveSync\logs\keelivesync-JJJJ-MM-TT.log, 14 Tage aufbewahrt.
    /// Immer: Fehler, Warnungen, Ereignisse. Debug nur, solange das ausführliche Protokoll eingeschaltet ist
    /// (schaltet sich nach 24 Stunden selbst ab). Nie: Tokens, Passwörter, Schlüssel, Datenbankinhalte.
    /// </summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private const int KeepDays = 14;

        internal static string Folder
        {
            get { return Paths.Sub("logs"); }
        }

        internal static bool DebugEnabled
        {
            get { return Settings.DebugUntil > DateTime.UtcNow; }
        }

        internal static void Error(string message, Exception ex = null)
        {
            Write("FEHLER", ex == null ? message : message + " – " + Describe(ex));
            if (ex != null && DebugEnabled) Write("DEBUG", ex.ToString());
        }

        internal static void Warn(string message)
        {
            Write("WARNUNG", message);
        }

        internal static void Info(string message)
        {
            Write("INFO", message);
        }

        internal static void Debug(string message)
        {
            if (DebugEnabled) Write("DEBUG", message);
        }

        internal static string Describe(Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null && (inner is AggregateException || inner.Message.Length == 0))
                inner = inner.InnerException;
            return inner.GetType().Name + ": " + inner.Message;
        }

        /// <summary>Nur die ersten Zeichen einer ID o. Ä., damit das Protokoll lesbar und unkritisch bleibt</summary>
        internal static string Short(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Length <= 12 ? value : value.Substring(0, 12) + "…";
        }

        private static void Write(string level, string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] [T" +
                              Thread.CurrentThread.ManagedThreadId + "] " + message + Environment.NewLine;
                lock (Sync)
                {
                    string file = Path.Combine(Folder, "keelivesync-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    bool isNew = !File.Exists(file);
                    File.AppendAllText(file, line, Encoding.UTF8);
                    if (isNew) Cleanup();
                }
            }
            catch (Exception)
            {
                // Protokoll darf nie den Abgleich stören
            }
        }

        private static void Cleanup()
        {
            foreach (string f in Directory.GetFiles(Folder, "keelivesync-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-KeepDays))
                    File.Delete(f);
        }
    }
}
