using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KeeLiveSync
{
    /// <summary>Verknüpfung einer Cloud-Datei mit ihrer lokalen Cache-Kopie</summary>
    internal sealed class Link
    {
        public string AccountId { get; set; }
        public string DriveId { get; set; }
        public string ItemId { get; set; }
        public string Name { get; set; }
        public string OwnerName { get; set; }
        /// <summary>cTag der Cloud-Fassung, die in der Cache-Datei steckt</summary>
        public string CacheCTag { get; set; }
        /// <summary>Cache enthält eigene Änderungen, die noch nicht hochgeladen sind</summary>
        public bool LocalDirty { get; set; }
        public DateTime LastSync { get; set; }
        public string LastRemoteBy { get; set; }

        public string Key
        {
            get { return Accounts.Hash(DriveId + "|" + ItemId); }
        }

        public string CachePath
        {
            get { return Path.Combine(Paths.Sub(Path.Combine("cache", Key)), Name); }
        }
    }

    /// <summary>Alle Verknüpfungen, gespeichert in %APPDATA%\KeeLiveSync\links.json</summary>
    internal static class Links
    {
        private static readonly object Sync = new object();
        private static List<Link> _list;

        private static string File_
        {
            get { return Path.Combine(Paths.Root, "links.json"); }
        }

        internal static List<Link> All
        {
            get
            {
                lock (Sync)
                {
                    if (_list == null)
                    {
                        try
                        {
                            _list = File.Exists(File_) ? Json.Parse<List<Link>>(File.ReadAllText(File_)) : new List<Link>();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Verknüpfungen nicht lesbar", ex);
                            _list = new List<Link>();
                        }
                    }
                    return _list.ToList();
                }
            }
        }

        internal static Link ByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
            return All.FirstOrDefault(l => string.Equals(Path.GetFullPath(l.CachePath), full, StringComparison.OrdinalIgnoreCase));
        }

        internal static Link Get(string driveId, string itemId)
        {
            return All.FirstOrDefault(l => l.DriveId == driveId && l.ItemId == itemId);
        }

        internal static void Upsert(Link link)
        {
            lock (Sync)
            {
                All.ToString();
                _list.RemoveAll(l => l.Key == link.Key);
                _list.Add(link);
                Save();
            }
        }

        internal static void Save()
        {
            lock (Sync)
            {
                if (_list == null) return;
                string tmp = File_ + ".tmp";
                File.WriteAllText(tmp, Json.Write(_list));
                if (File.Exists(File_)) File.Replace(tmp, File_, null);
                else File.Move(tmp, File_);
            }
        }

        internal static void Remove(Link link)
        {
            lock (Sync)
            {
                All.ToString();
                _list.RemoveAll(l => l.Key == link.Key);
                Save();
            }
        }
    }
}
