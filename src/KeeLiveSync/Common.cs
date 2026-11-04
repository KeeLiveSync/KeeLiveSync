using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace KeeLiveSync
{
    /// <summary>Ablageorte unter %APPDATA%\KeeLiveSync</summary>
    internal static class Paths
    {
        internal static string Root
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeeLiveSync");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        internal static string Sub(string name)
        {
            string dir = Path.Combine(Root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Einstellungen in der KeePass-Konfiguration (wandern bei portabler Installation mit)</summary>
    internal static class Settings
    {
        private const string KeyDebugUntil = "KeeLiveSync.DebugUntil";
        private const string KeyNotify = "KeeLiveSync.NotifyRemoteChanges";
        private const string KeyAutoSave = "KeeLiveSync.AutoSave";

        internal static DateTime DebugUntil
        {
            get
            {
                string s = KeePass.Program.Config.CustomConfig.GetString(KeyDebugUntil, "");
                DateTime d;
                return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)
                    ? d : DateTime.MinValue;
            }
            set
            {
                KeePass.Program.Config.CustomConfig.SetString(KeyDebugUntil,
                    value == DateTime.MinValue ? "" : value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            }
        }

        internal static bool NotifyRemoteChanges
        {
            get { return KeePass.Program.Config.CustomConfig.GetBool(KeyNotify, true); }
            set { KeePass.Program.Config.CustomConfig.SetBool(KeyNotify, value); }
        }

        /// <summary>Verknüpfte Datenbanken nach jeder Änderung selbst speichern und hochladen</summary>
        internal static bool AutoSave
        {
            get { return KeePass.Program.Config.CustomConfig.GetBool(KeyAutoSave, true); }
            set { KeePass.Program.Config.CustomConfig.SetBool(KeyAutoSave, value); }
        }
    }

    /// <summary>JSON über JavaScriptSerializer (Teil von .NET Framework, keine Fremdbibliothek)</summary>
    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        internal static Dictionary<string, object> Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || json[0] != '{') return null;
            return Serializer.Deserialize<Dictionary<string, object>>(json);
        }

        internal static T Parse<T>(string json)
        {
            return Serializer.Deserialize<T>(json);
        }

        internal static string Write(object value)
        {
            return Serializer.Serialize(value);
        }

        internal static string Str(this Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        internal static long Long(this Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
        }

        internal static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        internal static IEnumerable<Dictionary<string, object>> Arr(this Dictionary<string, object> d, string key)
        {
            object v;
            var list = d != null && d.TryGetValue(key, out v) ? v as ArrayList : null;
            if (list == null) yield break;
            foreach (object o in list)
            {
                var item = o as Dictionary<string, object>;
                if (item != null) yield return item;
            }
        }

        internal static DateTime Date(this Dictionary<string, object> d, string key)
        {
            DateTime t;
            string s = d.Str(key);
            return s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)
                ? t.ToLocalTime() : DateTime.MinValue;
        }
    }
}
