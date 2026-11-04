using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using KeePass.Forms;
using KeePass.UI;
using KeePassLib;
using KeePassLib.Collections;
using KeePassLib.Interfaces;
using KeePassLib.Serialization;

namespace KeeLiveSync
{
    internal enum SyncState
    {
        Synced,
        Syncing,
        Offline,
        Error,
        SignInRequired
    }

    /// <summary>Was ein Abgleich an Einträgen verändert hat (Titel für die Anzeige, IDs zum Zeigen)</summary>
    internal sealed class ChangeSet
    {
        public readonly List<KeyValuePair<PwUuid, string>> Added = new List<KeyValuePair<PwUuid, string>>();
        public readonly List<KeyValuePair<PwUuid, string>> Changed = new List<KeyValuePair<PwUuid, string>>();
        public readonly List<KeyValuePair<PwUuid, string>> Removed = new List<KeyValuePair<PwUuid, string>>();
        public readonly List<KeyValuePair<PwUuid, string>> Restored = new List<KeyValuePair<PwUuid, string>>();

        public bool IsEmpty
        {
            get { return Added.Count + Changed.Count + Removed.Count + Restored.Count == 0; }
        }

        /// <summary>Einträge, die sich in der Liste zeigen lassen (neu, geändert, wiederhergestellt)</summary>
        public List<PwUuid> Showable
        {
            get { return Added.Concat(Restored).Concat(Changed).Select(kv => kv.Key).ToList(); }
        }
    }

    /// <summary>Eine geöffnete, verknüpfte Datenbank</summary>
    internal sealed class Session
    {
        public Link Link;
        public PwDatabase Db;
        /// <summary>cTag der Cloud-Fassung, die zuletzt in die offene Datenbank eingeflossen ist</summary>
        public string MergedCTag;
        /// <summary>Änderungszeit (UTC) der Cloud-Fassung zu MergedCTag</summary>
        public DateTime MergedModified;
        public bool Busy;
        public bool CheckPending;
        public bool SuppressUpload;
        public int ConflictRounds;
        public SyncState State = SyncState.Syncing;
        public string StateText = "";
        public Tuple<List<byte[]>, DriveItem> Deferred;
        /// <summary>Hauptschlüssel, nur zwischen Schließen und Abschluss des letzten Hochladens gehalten</summary>
        public KeePassLib.Keys.CompositeKey ClosingKey;
        /// <summary>Speichern direkt vor dem Schließen: Hochladen übernimmt FinishAfterClose</summary>
        public bool SavingForClose;
        /// <summary>Zuletzt eingeblendetes Problem (gleiches Problem nur einmal melden)</summary>
        public string ProblemShown;
        /// <summary>Cloud-Fassung ist mit einem anderen Hauptschlüssel verschlüsselt</summary>
        public bool KeyChanged;
        /// <summary>Laufendes Hochladen (Graph-Aufgabe ohne UI-Fortsetzung) und Prüfsumme der hochgeladenen Daten</summary>
        public Tuple<Task<UploadResult>, string> InFlight;
    }

    /// <summary>
    /// Abgleich zwischen offener Datenbank, Cache-Datei und Cloud-Datei:
    /// Öffnen aus dem Cache → cTag prüfen → bei Änderung herunterladen und zusammenführen (PwDatabase.MergeIn, Synchronize);
    /// Speichern → Hochladen mit If-Match → bei 412 zusammenführen und erneut speichern; Push-Meldungen lösen eine Prüfung aus.
    /// </summary>
    internal sealed class SyncEngine
    {
        private readonly MainForm _main;
        private readonly Dictionary<PwDatabase, Session> _sessions = new Dictionary<PwDatabase, Session>();
        private readonly Dictionary<string, PushChannel> _channels = new Dictionary<string, PushChannel>();

        internal event Action StateChanged;
        /// <summary>Änderungen anderer übernommen: Sitzung, Zusammenfassung, neu hinzugekommene Einträge</summary>
        internal event Action<Session, ChangeSet> RemoteChangesApplied;
        /// <summary>Abgleich gestört (einmal je Problem): Sitzung, Meldung</summary>
        internal event Action<Session, string> ProblemOccurred;

        internal SyncEngine(MainForm main)
        {
            _main = main;
            _main.FileOpened += OnFileOpened;
            _main.FileSaved += OnFileSaved;
            _main.FileClosingPre += OnFileClosingPre;
            _main.UIStateUpdated += (o, e) => ScheduleAutoSave();
            _autoSave = new Timer { Interval = 1000 };
            _autoSave.Tick += (o, e) => AutoSaveNow();
            _main.FileClosed += OnFileClosed;
            GlobalWindowManager.WindowRemoved += (s, e) =>
            {
                RunDeferred();
                ScheduleAutoSave();
            };
        }

        internal IEnumerable<Session> Sessions
        {
            get { return _sessions.Values.ToList(); }
        }

        internal Session Find(PwDatabase db)
        {
            Session s;
            return db != null && _sessions.TryGetValue(db, out s) ? s : null;
        }

        internal bool PushConnected(Session s)
        {
            PushChannel c;
            return _channels.TryGetValue(ChannelKey(s.Link), out c) && c.Connected;
        }

        private static string ChannelKey(Link l)
        {
            return l.AccountId + "|" + l.DriveId;
        }

        private void OnUi(Action action)
        {
            if (_main.IsDisposed) return;
            if (_main.InvokeRequired) _main.BeginInvoke(action);
            else action();
        }

        internal event Action<string> SignInNeeded;
        private readonly HashSet<string> _signInAsked = new HashSet<string>();

        /// <summary>Nach erneuter Anmeldung: alle Datenbanken dieses Kontos sofort wieder abgleichen</summary>
        internal void Resume(string accountId)
        {
            _signInAsked.Remove(accountId);
            foreach (var key in _channels.Keys.Where(k => k.StartsWith(accountId + "|", StringComparison.Ordinal)).ToList())
            {
                _channels[key].Stop();
                _channels.Remove(key);
            }
            foreach (var s in _sessions.Values.Where(x => x.Link.AccountId == accountId).ToList())
            {
                StartChannel(s.Link);
                Check(s, "neu angemeldet");
            }
        }

        private void SetState(Session s, SyncState state, string text)
        {
            if (state == SyncState.SignInRequired && _signInAsked.Add(s.Link.AccountId))
            {
                Log.Warn("Anmeldung abgelaufen für Konto " + Log.Short(s.Link.AccountId) + " – frage nach");
                OnUi(() =>
                {
                    var h = SignInNeeded;
                    if (h != null) h(s.Link.AccountId);
                });
            }
            s.State = state;
            s.StateText = text;
            bool report = state == SyncState.Error && text != s.ProblemShown;
            if (state == SyncState.Error) s.ProblemShown = text;
            else if (state == SyncState.Synced)
            {
                s.ProblemShown = null;
                s.KeyChanged = false;
            }
            OnUi(() =>
            {
                var h = StateChanged;
                if (h != null) h();
                if (!report) return;
                var p = ProblemOccurred;
                if (p != null) p(s, text);
            });
        }

        // ---------- Öffnen ----------

        /// <summary>Cache-Datei in KeePass öffnen (KeePass fragt den Hauptschlüssel ab)</summary>
        /// <summary>Verständliche Ursache für die Anzeige; technische Einzelheiten stehen im Protokoll</summary>
        private static string FriendlyError(Exception ex)
        {
            var g = ex as GraphException;
            if (g != null)
            {
                int code = (int)g.Status;
                if (code == 404) return Text.T("Die Datei wurde in OneDrive nicht gefunden (verschoben oder gelöscht?).",
                    "The file was not found in OneDrive (moved or deleted?).");
                if (code == 403) return Text.T("Kein Zugriff mehr auf die Datei in OneDrive (Freigabe aufgehoben?).",
                    "No access to the file in OneDrive anymore (sharing removed?).");
                if (code >= 500) return Text.T("OneDrive ist gerade nicht erreichbar.", "OneDrive is currently unavailable.");
                return Text.T("OneDrive hat die Anfrage abgelehnt (Fehler {0}).", "OneDrive rejected the request (error {0}).", code);
            }
            if (ex is IOException || ex is UnauthorizedAccessException)
                return Text.T("Die lokale Kopie konnte nicht gelesen oder geschrieben werden.", "The local copy could not be read or written.");
            if (ex is InvalidOperationException) return ex.Message;
            return Text.T("Unerwarteter Fehler – Details im Protokoll.", "Unexpected error – see the log for details.");
        }

        /// <summary>Datenbank der Sitzung in den Vordergrund holen</summary>
        internal void Activate(Session s)
        {
            var doc = _main.DocumentManager.FindDocument(s.Db);
            if (doc != null) _main.MakeDocumentActive(doc);
        }

        /// <summary>Datenbank nach vorn holen, die Gruppe des ersten Eintrags zeigen, die Einträge markieren und hervorheben</summary>
        internal void ShowEntries(Session s, List<PwUuid> ids)
        {
            var doc = _main.DocumentManager.FindDocument(s.Db);
            if (doc == null || !s.Db.IsOpen) return;
            var entries = new PwObjectList<PwEntry>();
            foreach (var id in ids)
            {
                var e = s.Db.RootGroup.FindEntry(id, true);
                if (e != null) entries.Add(e);
            }
            if (entries.UCount == 0) return;
            if (_main.DocumentManager.ActiveDocument != doc) _main.MakeDocumentActive(doc);
            // wie die KeePass-Suche: virtuelle Gruppe mit genau diesen Einträgen, gruppenübergreifend;
            // ein Klick auf eine Gruppe im Baum führt zur normalen Ansicht zurück
            var results = new PwGroup(true, true, Text.T("Änderungen", "Changes"), PwIcon.List) { IsVirtual = true };
            foreach (var e in entries) results.AddEntry(e, false);
            _main.UpdateUI(false, null, false, null, true, results, false);
            _main.SelectEntries(entries, true, true);
            _main.SetStatusEx(Text.Plural(entries.UCount, "{0} geänderter Eintrag – zum Beenden links eine Gruppe anklicken",
                "{0} geänderte Einträge – zum Beenden links eine Gruppe anklicken",
                "{0} changed entry – click a group on the left to return", "{0} changed entries – click a group on the left to return"));
            _main.EnsureVisibleEntry(entries.GetAt(0).Uuid);
            Highlight(s, ids);
        }

        private static readonly Color HighlightColor = Color.FromArgb(214, 240, 236);

        /// <summary>Neue Einträge in der sichtbaren Liste ein paar Sekunden farbig hinterlegen</summary>
        private void Highlight(Session s, List<PwUuid> ids)
        {
            var lv = EntryList;
            var doc = _main.DocumentManager.FindDocument(s.Db);
            if (lv == null || ids.Count == 0 || doc == null || _main.DocumentManager.ActiveDocument != doc) return;
            var set = new HashSet<PwUuid>(ids);
            var marked = new List<KeyValuePair<ListViewItem, Color>>();
            foreach (ListViewItem lvi in lv.Items)
            {
                var e = EntryOf(lvi);
                if (e == null || !set.Contains(e.Uuid)) continue;
                marked.Add(new KeyValuePair<ListViewItem, Color>(lvi, lvi.BackColor));
                lvi.BackColor = HighlightColor;
            }
            if (marked.Count == 0) return;
            var timer = new Timer { Interval = 5000 };
            timer.Tick += (o, e) =>
            {
                timer.Stop();
                timer.Dispose();
                foreach (var m in marked)
                    if (m.Key.ListView != null && m.Key.BackColor == HighlightColor) m.Key.BackColor = m.Value;
            };
            timer.Start();
        }

        /// <summary>
        /// Nach einem Schlüsselwechsel in der Cloud: Datenbank schließen (wie Datei → Schließen), die neue Fassung in die lokale
        /// Kopie holen und neu öffnen – KeePass fragt dann nach dem neuen Hauptschlüssel. Nicht hochgeladene eigene Änderungen
        /// bleiben als Sicherungsdatei neben der lokalen Kopie erhalten.
        /// </summary>
        internal async void ReopenAfterKeyChange(Session s)
        {
            var link = s.Link;
            try
            {
                var doc = _main.DocumentManager.FindDocument(s.Db);
                if (doc != null)
                {
                    _main.MakeDocumentActive(doc);
                    var close = _main.MainMenu.Items.Find("m_menuFileClose", true).FirstOrDefault() as ToolStripMenuItem;
                    if (close == null)
                    {
                        Log.Warn("Menü Datei → Schließen nicht gefunden");
                        return;
                    }
                    close.PerformClick();
                    if (s.Db.IsOpen)
                    {
                        Log.Info("Schließen abgebrochen, Neu-Öffnen entfällt: " + link.Name);
                        return;
                    }
                }
                if (link.LocalDirty && File.Exists(link.CachePath))
                {
                    string backup = Path.Combine(Path.GetDirectoryName(link.CachePath),
                        Path.GetFileNameWithoutExtension(link.Name) + " (vor Schlüsselwechsel " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ").kdbx");
                    File.Copy(link.CachePath, backup, true);
                    Log.Warn("Nicht hochgeladene Änderungen gesichert: " + backup);
                    MessageBox.Show(_main, Text.T(
                        "Die lokale Kopie enthielt Änderungen, die wegen des neuen Hauptschlüssels nicht hochgeladen werden konnten. Sie sind gesichert in:\n\n{0}\n\nDu kannst sie mit Datei → Synchronisieren übernehmen.",
                        "The local copy contained changes that could not be uploaded because of the new master key. They are saved in:\n\n{0}\n\nYou can apply them with File → Synchronize.", backup),
                        "KeeLiveSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                var item = await Graph.Item(link.AccountId, link.DriveId, link.ItemId);
                byte[] data = await Graph.Download(link.AccountId, link.DriveId, link.ItemId);
                File.WriteAllBytes(link.CachePath, data);
                link.CacheCTag = item.CTag;
                link.CacheModified = item.Modified;
                link.LocalDirty = false;
                link.LastSync = DateTime.Now;
                Links.Upsert(link);
                Links.Save();
                Log.Info("Lokale Kopie nach Schlüsselwechsel erneuert, öffne neu: " + link.Name);
                OpenLink(link);
            }
            catch (Exception ex)
            {
                Log.Error("Neu öffnen nach Schlüsselwechsel fehlgeschlagen: " + link.Name, ex);
                MessageBox.Show(_main, Log.Describe(ex), "KeeLiveSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        internal void OpenLink(Link link)
        {
            string target = Path.GetFullPath(link.CachePath);
            Func<IOConnectionInfo, bool> same = ioc => ioc != null && !string.IsNullOrEmpty(ioc.Path) &&
                                                       string.Equals(Path.GetFullPath(ioc.Path), target, StringComparison.OrdinalIgnoreCase);
            var open = _main.DocumentManager.Documents.FirstOrDefault(d =>
                (d.Database.IsOpen && same(d.Database.IOConnectionInfo)) || same(d.LockedIoc));
            if (open != null)
            {
                _main.MakeDocumentActive(open);
                return;
            }
            _main.OpenDatabase(IOConnectionInfo.FromPath(link.CachePath), null, false);
        }

        /// <summary>Cloud-Datei in den Cache holen, falls nötig (ohne eigene, noch nicht hochgeladene Änderungen zu überschreiben)</summary>
        internal async Task<Link> PrepareCache(Account account, DriveItem item, Action<string> progress)
        {
            var link = Links.Get(item.DriveId, item.Id) ?? new Link
            {
                AccountId = account.Id,
                DriveId = item.DriveId,
                ItemId = item.Id,
                Name = item.Name
            };
            link.AccountId = account.Id;
            link.OwnerName = item.OwnerName;
            bool haveCache = File.Exists(link.CachePath);
            if (!haveCache || (!link.LocalDirty && link.CacheCTag != item.CTag))
            {
                progress(Text.T("Lade „{0}“ herunter …", "Downloading \"{0}\" …", item.Name));
                byte[] data = await Graph.Download(account.Id, item.DriveId, item.Id);
                string tmp = link.CachePath + ".download";
                File.WriteAllBytes(tmp, data);
                if (haveCache) File.Replace(tmp, link.CachePath, null);
                else File.Move(tmp, link.CachePath);
                link.CacheCTag = item.CTag;
                link.CacheModified = item.Modified;
                link.LastSync = DateTime.Now;
                link.LastRemoteBy = item.ModifiedBy;
                Log.Info("Cache aktualisiert: " + item.Name + " (" + Text.Size(data.Length) + ")");
            }
            Links.Upsert(link);
            return link;
        }

        private void OnFileOpened(object sender, FileOpenedEventArgs e)
        {
            var db = e.Database;
            var link = Links.ByPath(db.IOConnectionInfo.Path);
            if (link == null) return;
            Session s;
            if (!_sessions.TryGetValue(db, out s))
            {
                s = new Session { Link = link, Db = db, MergedCTag = link.CacheCTag, MergedModified = link.CacheModified };
                _sessions[db] = s;
                Log.Info("Geöffnet: " + link.Name + " (Cloud-Datei " + Log.Short(link.ItemId) + ")");
            }
            StartChannel(link);
            Check(s, "geöffnet");
        }

        private readonly Timer _autoSave;

        /// <summary>Nach einer Änderung (KeePass setzt Modified und aktualisiert die Oberfläche) in 1 s speichern; weitere Änderungen verschieben das</summary>
        private void ScheduleAutoSave()
        {
            if (!Settings.AutoSave || !_sessions.Values.Any(x => x.Db.IsOpen && x.Db.Modified)) return;
            _autoSave.Stop();
            _autoSave.Start();
        }

        private void AutoSaveNow()
        {
            _autoSave.Stop();
            foreach (var s in _sessions.Values.Where(x => x.Db.IsOpen && x.Db.Modified).ToList())
            {
                if (!CanApplyNow(s))
                {
                    // Dialog offen: nach dem Schließen erneut versuchen (UIStateUpdated folgt)
                    continue;
                }
                Log.Debug("Automatisch speichern: " + s.Link.Name);
                s.SuppressUpload = false;
                SaveQuietly(s);
            }
        }

        private void OnFileClosingPre(object sender, FileClosingEventArgs e)
        {
            var s = Find(e.Database);
            if (s == null) return;
            s.ClosingKey = s.Db.MasterKey;
            // FileClosingPre kommt vor KeePass' Frage „Änderungen speichern?“ – mit automatischem Speichern gar nicht erst fragen lassen
            if (Settings.AutoSave && s.Db.Modified)
            {
                _autoSave.Stop();
                Log.Debug("Automatisch speichern vor dem Schließen: " + s.Link.Name);
                s.SuppressUpload = false;
                s.SavingForClose = true;
                try
                {
                    SaveQuietly(s);
                }
                finally
                {
                    s.SavingForClose = false;
                }
            }
        }

        private void OnFileClosed(object sender, FileClosedEventArgs e)
        {
            var closed = _sessions.Values.Where(s => !s.Db.IsOpen).ToList();
            foreach (var s in closed)
            {
                _sessions.Remove(s.Db);
                Log.Info("Geschlossen: " + s.Link.Name);
                if (s.Link.LocalDirty && s.ClosingKey != null && !s.KeyChanged) FinishAfterClose(s);
                else s.ClosingKey = null;
            }
            foreach (var key in _channels.Keys.ToList())
                if (!_sessions.Values.Any(s => ChannelKey(s.Link) == key))
                {
                    _channels[key].Stop();
                    _channels.Remove(key);
                }
            OnUi(() =>
            {
                var h = StateChanged;
                if (h != null) h();
            });
        }

        private void StartChannel(Link link)
        {
            string key = ChannelKey(link);
            if (_channels.ContainsKey(key)) return;
            var c = new PushChannel(link.AccountId, link.DriveId);
            c.Changed += reason => OnUi(() =>
            {
                foreach (var s in _sessions.Values.Where(x => ChannelKey(x.Link) == key).ToList())
                    Check(s, reason);
            });
            c.ConnectedChanged += connected => OnUi(() =>
            {
                var h = StateChanged;
                if (h != null) h();
            });
            _channels[key] = c;
            c.Start();
        }

        // ---------- Prüfen und Zusammenführen ----------

        /// <summary>Cloud-Datei auf Änderungen prüfen (cTag) und ggf. zusammenführen bzw. Ausstehendes hochladen</summary>
        internal async void Check(Session s, string reason)
        {
            if (s.Busy)
            {
                s.CheckPending = true;
                return;
            }
            s.Busy = true;
            try
            {
                Log.Debug("Prüfe " + s.Link.Name + " (" + reason + ")");
                var item = await Graph.Item(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId);
                if (item.CTag == s.MergedCTag)
                {
                    if (s.Link.LocalDirty && !s.Db.Modified)
                    {
                        Log.Info("Lade ausstehende Änderungen hoch: " + s.Link.Name);
                        await UploadCache(s);
                    }
                    else
                    {
                        s.Link.LastSync = DateTime.Now;
                        SetState(s, SyncState.Synced, "");
                    }
                    return;
                }
                Log.Info("Cloud-Datei geändert (" + reason + "): " + s.Link.Name + ", zuletzt von " + (item.ModifiedBy ?? "?"));
                SetState(s, SyncState.Syncing, Text.T("Übernehme Änderungen …", "Applying changes …"));
                ApplyRemote(s, await DownloadWithSkipped(s, item), item);
            }
            catch (SignInRequiredException)
            {
                SetState(s, SyncState.SignInRequired, Text.T("Bitte erneut anmelden", "Please sign in again"));
            }
            catch (Exception ex) when (IsOffline(ex))
            {
                Log.Warn("Offline: " + Log.Describe(ex));
                SetState(s, SyncState.Offline, Text.T("Keine Verbindung", "No connection"));
            }
            catch (Exception ex)
            {
                Log.Error("Abgleich fehlgeschlagen: " + s.Link.Name, ex);
                SetState(s, SyncState.Error, FriendlyError(ex));
            }
            finally
            {
                s.Busy = false;
                if (s.CheckPending)
                {
                    s.CheckPending = false;
                    Check(s, "nachgeholt");
                }
            }
        }

        private static bool IsOffline(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is System.Net.Http.HttpRequestException || e is System.Net.WebException || e is TaskCanceledException ||
                    e is System.Net.Sockets.SocketException)
                    return true;
            return false;
        }

        private bool CanApplyNow(Session s)
        {
            var doc = _main.DocumentManager.FindDocument(s.Db);
            return s.Db.IsOpen && doc != null && !_main.IsFileLocked(doc) && GlobalWindowManager.WindowCount == 0;
        }

        private void RunDeferred()
        {
            // erst nach dem Austragen des geschlossenen Fensters aus WindowCount prüfen
            if (_main.IsDisposed || !_sessions.Values.Any(x => x.Deferred != null)) return;
            _main.BeginInvoke(new Action(() =>
            {
                foreach (var s in _sessions.Values.Where(x => x.Deferred != null).ToList())
                {
                    if (!CanApplyNow(s))
                    {
                        Log.Debug("Aufgeschobenes Zusammenführen wartet weiter (" + GlobalWindowManager.WindowCount + " Dialog(e) offen): " + s.Link.Name);
                        continue;
                    }
                    var d = s.Deferred;
                    s.Deferred = null;
                    Log.Debug("Hole aufgeschobenes Zusammenführen nach: " + s.Link.Name);
                    ApplyRemote(s, d.Item1, d.Item2);
                }
            }));
        }

        /// <summary>Heruntergeladene Cloud-Fassung in die offene Datenbank einführen (UI-Thread)</summary>
        /// <summary>Aktuelle Cloud-Fassung plus übersprungene Versionen dazwischen (älteste zuerst, aktuelle zuletzt)</summary>
        private async Task<List<byte[]>> DownloadWithSkipped(Session s, DriveItem item)
        {
            var result = new List<byte[]>();
            // Zwischenversionen nur, wenn der bekannte Stand nicht direkt der Vorgänger ist (cTag-Zähler springt um mehr als 1)
            int seen = CTagVersion(s.MergedCTag), latest = CTagVersion(item.CTag);
            bool skipped = seen == 0 || latest == 0 || latest > seen + 1;
            if (skipped && s.MergedModified != DateTime.MinValue)
            {
                try
                {
                    // Versions-IDs entsprechen nicht immer dem cTag-Zähler → über die Änderungszeit auswählen;
                    // die aktuelle Fassung (neueste) kommt über den normalen Download
                    var versions = await Graph.Versions(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId);
                    var between = versions.Skip(1)
                        .Where(v => v.Value > s.MergedModified && v.Value < item.Modified)
                        .ToList();
                    if (between.Count > MaxSkippedVersions)
                    {
                        Log.Warn((between.Count - MaxSkippedVersions) + " ältere Zwischenversionen werden nicht geprüft: " + s.Link.Name);
                        between = between.Take(MaxSkippedVersions).ToList();
                    }
                    between.Reverse();
                    foreach (var v in between)
                    {
                        byte[] old = await Graph.DownloadVersion(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId, v.Key);
                        if (old != null) result.Add(old);
                        else Log.Debug("Version " + v.Key + " nicht im Verlauf: " + s.Link.Name);
                    }
                    if (between.Count > 0)
                        Log.Info("Übersprungene Versionen geholt (" + result.Count + " von " + between.Count + "): " + s.Link.Name);
                }
                catch (SignInRequiredException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Zwischenversionen sind eine Zusatzsicherung – der normale Abgleich darf daran nicht scheitern
                    Log.Warn("Zwischenversionen nicht geprüft: " + s.Link.Name + " – " + Log.Describe(ex));
                    result.Clear();
                }
            }
            result.Add(await Graph.Download(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId));
            return result;
        }

        private const int MaxSkippedVersions = 10;

        /// <summary>Versionszähler aus dem cTag (Format "c:{GUID},14" → 14); 0, wenn nicht erkennbar</summary>
        private static int CTagVersion(string cTag)
        {
            if (string.IsNullOrEmpty(cTag)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(cTag, @",(\d+)""?$");
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : 0;
        }

        private PwDatabase OpenRemote(Session s, byte[] data, string tmp)
        {
            File.WriteAllBytes(tmp, data);
            var db = new PwDatabase();
            db.Open(IOConnectionInfo.FromPath(tmp), s.Db.MasterKey, new NullStatusLogger());
            return db;
        }

        private void ApplyRemote(Session s, List<byte[]> versions, DriveItem item)
        {
            if (_main.InvokeRequired)
            {
                _main.BeginInvoke(new Action(() => ApplyRemote(s, versions, item)));
                return;
            }
            if (!CanApplyNow(s))
            {
                // nicht während eines offenen Dialogs (z. B. Eintrag bearbeiten) oder bei gesperrter Datenbank
                s.Deferred = Tuple.Create(versions, item);
                Log.Debug("Zusammenführen aufgeschoben (Dialog offen oder gesperrt): " + s.Link.Name);
                SetState(s, SyncState.Syncing, Text.T("Änderungen warten …", "Changes pending …"));
                return;
            }

            string tmp = Path.Combine(Paths.Sub("temp"), Guid.NewGuid().ToString("N") + ".kdbx");
            var remote = new PwDatabase();
            try
            {
                try
                {
                    remote = OpenRemote(s, versions[versions.Count - 1], tmp);
                }
                catch (Exception ex)
                {
                    Log.Error("Cloud-Fassung lässt sich mit dem aktuellen Hauptschlüssel nicht öffnen: " + s.Link.Name, ex);
                    s.KeyChanged = true;
                    SetState(s, SyncState.Error, Text.T("Neuer Hauptschlüssel – bitte neu öffnen", "New master key – please reopen"));
                    return;
                }

                bool wasModified = s.Db.Modified;
                var before = Snapshot(s.Db);
                var titlesBefore = Titles(s.Db);
                var latest = Snapshot(remote);
                string groupsBefore = GroupSignature(s.Db);
                var view = SaveView();
                for (int i = 0; i < versions.Count - 1; i++)
                {
                    try
                    {
                        var old = OpenRemote(s, versions[i], tmp);
                        s.Db.MergeIn(old, PwMergeMethod.Synchronize, null);
                        old.Close();
                    }
                    catch (Exception ex)
                    {
                        // ältere Version mit anderem Hauptschlüssel o. Ä.: überspringen
                        Log.Warn("Zwischenversion nicht zusammengeführt: " + Log.Describe(ex));
                    }
                }
                s.Db.MergeIn(remote, PwMergeMethod.Synchronize, null);
                var after = Snapshot(s.Db);
                string summary = Summarize(before, after);

                // Stand, den die Cloud nicht hat (z. B. von einem Handy überschriebene Einträge) → wieder hochladen
                int restored = after.Keys.Count(k => !latest.ContainsKey(k) && !before.ContainsKey(k));
                bool ahead = after.Any(kv => !latest.ContainsKey(kv.Key) || kv.Value > latest[kv.Key]);
                if (restored > 0)
                {
                    string r = Text.Plural(restored, "{0} überschriebener Eintrag wiederhergestellt", "{0} überschriebene Einträge wiederhergestellt",
                        "{0} overwritten entry restored", "{0} overwritten entries restored");
                    summary = summary.Length == 0 ? r : summary + ", " + r;
                }
                if (ahead && !s.Link.LocalDirty)
                {
                    Log.Info("Zusammengeführter Stand ist neuer als die Cloud – wird hochgeladen: " + s.Link.Name);
                    s.Link.LocalDirty = true;
                }
                s.MergedCTag = item.CTag;
                s.MergedModified = item.Modified;
                s.Link.LastSync = DateTime.Now;
                s.Link.LastRemoteBy = item.ModifiedBy;
                Log.Info("Zusammengeführt: " + s.Link.Name + " – " + (summary.Length == 0 ? "keine sichtbaren Änderungen" : summary));

                var titlesAfter = Titles(s.Db);
                var changes = new ChangeSet();
                foreach (var kv in after)
                {
                    var entry = new KeyValuePair<PwUuid, string>(kv.Key, titlesAfter[kv.Key]);
                    if (!before.ContainsKey(kv.Key))
                    {
                        if (latest.ContainsKey(kv.Key)) changes.Added.Add(entry);
                        else changes.Restored.Add(entry);
                    }
                    else if (before[kv.Key] != kv.Value) changes.Changed.Add(entry);
                }
                foreach (var k in before.Keys.Where(k => !after.ContainsKey(k)))
                    changes.Removed.Add(new KeyValuePair<PwUuid, string>(k, titlesBefore[k]));
                if (summary.Length > 0 || GroupSignature(s.Db) != groupsBefore)
                {
                    if (GroupSignature(s.Db) != groupsBefore || !RefreshEntriesOnly(s, view))
                        RefreshView(s, view, true);
                    Highlight(s, changes.Showable);
                }

                if (!wasModified)
                {
                    // Cache auf den zusammengeführten Stand bringen; hochladen nur, wenn noch eigene Änderungen ausstehen
                    s.SuppressUpload = !s.Link.LocalDirty;
                    SaveQuietly(s);
                    Log.Debug("Scrollposition nach Speichern: " + TopIndex(EntryList));
                    RestoreTop(view);
                }
                else
                {
                    Log.Debug("Eigene ungespeicherte Änderungen bleiben offen; Hochladen beim nächsten Speichern");
                }
                Links.Save();
                SetState(s, SyncState.Synced, "");
                if (summary.Length > 0)
                {
                    var h = RemoteChangesApplied;
                    if (h != null && !changes.IsEmpty) h(s, changes);
                }
            }
            finally
            {
                if (remote.IsOpen) remote.Close();
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception)
                {
                    // Temp-Datei wird beim nächsten Start aufgeräumt
                }
            }
        }

        /// <summary>Titel aller Einträge außerhalb des Papierkorbs (nur für die Anzeige)</summary>
        private static Dictionary<PwUuid, string> Titles(PwDatabase db)
        {
            var map = new Dictionary<PwUuid, string>();
            foreach (var e in db.RootGroup.GetEntries(true))
                if (db.RecycleBinUuid == null || e.ParentGroup == null || !e.ParentGroup.Uuid.Equals(db.RecycleBinUuid))
                {
                    string title = e.Strings.ReadSafe(PwDefs.TitleField);
                    string path = e.ParentGroup != null && e.ParentGroup != db.RootGroup ? e.ParentGroup.GetFullPath("/", false) : "";
                    map[e.Uuid] = path.Length > 0 ? path + "/" + title : title;
                }
            return map;
        }

        private static Dictionary<PwUuid, DateTime> Snapshot(PwDatabase db)
        {
            var map = new Dictionary<PwUuid, DateTime>();
            foreach (var e in db.RootGroup.GetEntries(true))
                if (db.RecycleBinUuid == null || e.ParentGroup == null || !e.ParentGroup.Uuid.Equals(db.RecycleBinUuid))
                    map[e.Uuid] = e.LastModificationTime;
            return map;
        }

        private static string Summarize(Dictionary<PwUuid, DateTime> before, Dictionary<PwUuid, DateTime> after)
        {
            int added = after.Keys.Count(k => !before.ContainsKey(k));
            int removed = before.Keys.Count(k => !after.ContainsKey(k));
            int changed = after.Count(kv => before.ContainsKey(kv.Key) && before[kv.Key] != kv.Value);
            var parts = new List<string>();
            if (added > 0) parts.Add(Text.Plural(added, "{0} neuer Eintrag", "{0} neue Einträge", "{0} new entry", "{0} new entries"));
            if (changed > 0) parts.Add(Text.Plural(changed, "{0} geändert", "{0} geändert", "{0} changed", "{0} changed"));
            if (removed > 0) parts.Add(Text.Plural(removed, "{0} gelöscht", "{0} gelöscht", "{0} deleted", "{0} deleted"));
            return string.Join(", ", parts);
        }

        // ---------- Anzeige ruhig aktualisieren ----------

        /// <summary>Gruppenbaum-Fingerabdruck: ändert er sich, wird der Baum neu aufgebaut</summary>
        private static string GroupSignature(PwDatabase db)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var g in db.RootGroup.GetGroups(true))
                sb.Append(g.Uuid.ToHexString()).Append(g.ParentGroup != null ? g.ParentGroup.Uuid.ToHexString() : "")
                    .Append(g.Name).Append((int)g.IconId).Append('|');
            return sb.ToString();
        }

        private static readonly System.Reflection.BindingFlags Private =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        /// <summary>
        /// Nur die Einträgsliste anpassen wie bei einer echten Synchronisation: verschwundene Einträge entfernen,
        /// neue der angezeigten Gruppe einfügen, geänderte Zeilen auffrischen. Nutzt interne KeePass-Methoden
        /// (MainForm.RemoveEntriesFromList/AddEntriesToList/UpdateEntryList); false → vollständiger Neuaufbau.
        /// </summary>
        private bool RefreshEntriesOnly(Session s, ViewState view)
        {
            try
            {
                var doc = _main.DocumentManager.FindDocument(s.Db);
                var lv = EntryList;
                var group = _main.GetSelectedGroup();
                if (doc == null || _main.DocumentManager.ActiveDocument != doc || lv == null || group == null)
                {
                    Log.Debug("Liste nicht angepasst: Datenbank nicht im Vordergrund – KeePass baut sie beim Wechsel auf: " + s.Link.Name);
                    return false;
                }

                // Nur, wenn die Liste vor dem Zusammenführen genau die Einträge der markierten Gruppe zeigte (keine Suche o. Ä.)
                if (!view.ListIsGroup || view.Group != group)
                {
                    Log.Debug("Liste nicht angepasst: zeigt keine einzelne Gruppe (Suche o. Ä.) – vollständiger Neuaufbau");
                    return false;
                }
                var shown = lv.Items.Cast<ListViewItem>().Select(EntryOf).ToList();
                if (shown.Any(e => e == null)) return false;

                var type = typeof(MainForm);
                var remove = type.GetMethod("RemoveEntriesFromList", Private);
                var add = type.GetMethod("AddEntriesToList", Private);
                var update = type.GetMethod("UpdateEntryList", Private, null, new[] { typeof(PwGroup), typeof(bool) }, null);
                if (remove == null || add == null || update == null)
                {
                    Log.Debug("Interne KeePass-Listenmethoden nicht gefunden – vollständiger Neuaufbau");
                    return false;
                }

                // Entfernen: nicht mehr in der Gruppe (gelöscht, verschoben, in den Papierkorb) oder durch neues Objekt ersetzt
                var gone = shown.Where(e => e.ParentGroup != group || s.Db.RootGroup.FindEntry(e.Uuid, true) != e).ToArray();
                var present = new HashSet<PwUuid>(shown.Except(gone).Select(e => e.Uuid));
                var added = new PwObjectList<PwEntry>();
                foreach (var e in group.Entries)
                    if (!present.Contains(e.Uuid)) added.Add(e);

                lv.BeginUpdate();
                try
                {
                    Log.Debug("Scrollposition vorher: " + TopIndex(lv));
                    if (gone.Length > 0) remove.Invoke(_main, new object[] { gone });
                    Log.Debug("Scrollposition nach Entfernen: " + TopIndex(lv));
                    if (added.UCount > 0) add.Invoke(_main, new object[] { added });
                    Log.Debug("Scrollposition nach Einfügen: " + TopIndex(lv));
                    update.Invoke(_main, new object[] { null, true });
                    Log.Debug("Scrollposition nach Auffrischen: " + TopIndex(lv));
                    if (lv.ListViewItemSorter != null) lv.Sort();
                    Log.Debug("Scrollposition nach Sortieren: " + TopIndex(lv));
                }
                finally
                {
                    lv.EndUpdate();
                }
                // Titel, Menüs und Statusleiste (Änderungsstern, Anzahl) nachziehen, ohne die Listen neu aufzubauen
                _main.UpdateUI(false, doc, false, null, false, null, true);
                Log.Debug("Scrollposition nach UpdateUI: " + TopIndex(lv));
                RestoreTop(view);
                Log.Debug("Liste angepasst: " + gone.Length + " entfernt, " + added.UCount + " eingefügt, sichtbare Zeilen aufgefrischt");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Feine Listenaktualisierung fehlgeschlagen, baue neu auf: " + Log.Describe(ex));
                return false;
            }
        }

        private sealed class ViewState
        {
            public PwGroup Group;
            public List<PwUuid> Selected = new List<PwUuid>();
            public PwUuid Top;
            public bool ListIsGroup;
        }

        private ListView EntryList
        {
            get { return _main.Controls.Find("m_lvEntries", true).FirstOrDefault() as ListView; }
        }

        private ViewState SaveView()
        {
            var v = new ViewState { Group = _main.GetSelectedGroup() };
            var lv = EntryList;
            if (lv == null) return v;
            v.ListIsGroup = v.Group != null && lv.Items.Cast<ListViewItem>().All(i =>
            {
                var e = EntryOf(i);
                return e != null && e.ParentGroup == v.Group;
            }) && lv.Items.Count == v.Group.Entries.UCount;
            foreach (ListViewItem lvi in lv.SelectedItems)
            {
                var e = EntryOf(lvi);
                if (e != null) v.Selected.Add(e.Uuid);
            }
            if (lv.TopItem != null)
            {
                var top = EntryOf(lv.TopItem);
                if (top != null) v.Top = top.Uuid;
            }
            return v;
        }

        private static string TopIndex(ListView lv)
        {
            return lv == null || lv.TopItem == null ? "-" : lv.TopItem.Index.ToString();
        }

        /// <summary>Oberste sichtbare Zeile wiederherstellen, falls KeePass die Liste nach oben gescrollt hat</summary>
        private void RestoreTop(ViewState v)
        {
            var lv = EntryList;
            if (lv == null || v.Top == null || lv.TopItem == null) return;
            if (EntryOf(lv.TopItem) != null && EntryOf(lv.TopItem).Uuid.Equals(v.Top)) return;
            foreach (ListViewItem lvi in lv.Items)
            {
                var e = EntryOf(lvi);
                if (e == null || !e.Uuid.Equals(v.Top)) continue;
                SendMessage(lv.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    lv.TopItem = lvi;
                }
                catch (Exception)
                {
                    lvi.EnsureVisible();
                }
                finally
                {
                    SendMessage(lv.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                    lv.Invalidate();
                }
                Log.Debug("Scrollposition wiederhergestellt: " + TopIndex(lv));
                break;
            }
        }

        private static PwEntry EntryOf(ListViewItem lvi)
        {
            var tag = lvi.Tag as PwListItem;
            return tag != null ? tag.Entry : lvi.Tag as PwEntry;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WmSetRedraw = 0x000B;

        private void RefreshView(Session s, ViewState v, bool setModified)
        {
            var doc = _main.DocumentManager.FindDocument(s.Db);
            if (doc == null || _main.DocumentManager.ActiveDocument != doc)
            {
                // andere Datenbank im Vordergrund: nur Registerkarten/Titel auffrischen
                _main.UpdateUI(false, null, false, null, false, null, false);
                return;
            }
            var lv = EntryList;
            if (lv != null) SendMessage(lv.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            try
            {
                PwGroup group = v.Group != null ? s.Db.RootGroup.FindGroup(v.Group.Uuid, true) : null;
                _main.UpdateUI(false, doc, true, group, true, null, setModified);

                var selected = new PwObjectList<PwEntry>();
                foreach (var id in v.Selected)
                {
                    var e = s.Db.RootGroup.FindEntry(id, true);
                    if (e != null) selected.Add(e);
                }
                if (selected.UCount > 0) _main.SelectEntries(selected, true, false);
                if (lv != null && v.Top != null)
                    foreach (ListViewItem lvi in lv.Items)
                    {
                        var e = EntryOf(lvi);
                        if (e == null || !e.Uuid.Equals(v.Top)) continue;
                        try
                        {
                            lv.TopItem = lvi;
                        }
                        catch (Exception)
                        {
                            lvi.EnsureVisible();
                        }
                        break;
                    }
            }
            finally
            {
                if (lv != null)
                {
                    SendMessage(lv.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                    lv.Invalidate();
                }
            }
        }

        // ---------- Speichern und Hochladen ----------

        /// <summary>
        /// Zusammengeführten Stand direkt über KeePassLib in den Cache schreiben – ohne MainForm.SaveDatabase,
        /// das die Oberfläche während des Speicherns sperrt (grau). Rückfall: Speichern über KeePass.
        /// </summary>
        private void SaveQuietly(Session s)
        {
            // wie MainForm.SaveDatabase: Plugin-Ereignis FileSaving + Trigger „Datenbank wird gespeichert“, danach die „gespeichert“-Gegenstücke
            var saveId = Guid.NewGuid();
            var saving = new FileSavingEventArgs(false, false, s.Db, saveId);
            RaiseMainFormEvent("FileSaving", saving);
            if (saving.Cancel)
            {
                Log.Info("Speichern von einem anderen Plugin abgebrochen: " + s.Link.Name);
                return;
            }
            RaiseTrigger("SavingDatabaseFile", s.Db);
            bool ok = false;
            try
            {
                s.Db.Save(new NullStatusLogger());
                ok = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Direktes Speichern fehlgeschlagen, speichere über KeePass: " + Log.Describe(ex));
            }
            if (ok) RaiseTrigger("SavedDatabaseFile", s.Db);
            _quietSave = true;
            try
            {
                RaiseMainFormEvent("FileSaved", new FileSavedEventArgs(ok, s.Db, saveId));
            }
            finally
            {
                _quietSave = false;
            }
            if (!ok)
            {
                _main.SaveDatabase(s.Db, null);
                return;
            }
            if (Log.DebugEnabled) Log.Debug("Cache gespeichert, Prüfsumme für KeePass " + (HashMatchesDisk(s) ? "aktuell" : "VERALTET"));
            var doc = _main.DocumentManager.FindDocument(s.Db);
            if (doc != null) _main.UpdateUI(false, doc, false, null, false, null, false);
            AfterSave(s);
        }

        /// <summary>true, während SaveQuietly das FileSaved-Ereignis selbst auslöst (eigener Handler übergeht es dann)</summary>
        private bool _quietSave;

        /// <summary>Ereignis von MainForm für andere Plugins auslösen (das Ereignis-Feld ist intern; fehlt es, entfällt nur die Benachrichtigung)</summary>
        private void RaiseMainFormEvent<T>(string name, T args) where T : EventArgs
        {
            try
            {
                var field = typeof(MainForm).GetField(name, Private);
                var handler = field != null ? field.GetValue(_main) as EventHandler<T> : null;
                if (field == null) Log.Debug("KeePass-Ereignis " + name + " nicht gefunden – andere Plugins werden nicht benachrichtigt");
                if (handler != null) handler(_main, args);
            }
            catch (Exception ex)
            {
                Log.Warn("Ereignis " + name + " für andere Plugins fehlgeschlagen: " + Log.Describe(ex));
            }
        }

        /// <summary>KeePass-Trigger-Ereignis auslösen; die Ereignis-IDs stehen in der internen Klasse EcasEventIDs</summary>
        private static void RaiseTrigger(string eventName, PwDatabase db)
        {
            try
            {
                var ids = typeof(KeePass.Ecas.EcasTriggerSystem).Assembly.GetType("KeePass.Ecas.EcasEventIDs");
                var field = ids != null ? ids.GetField(eventName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
                var eventId = field != null ? field.GetValue(null) as PwUuid : null;
                if (eventId == null)
                {
                    Log.Debug("KeePass-Trigger-Ereignis " + eventName + " nicht gefunden");
                    return;
                }
                var props = new KeePass.Ecas.EcasPropertyDictionary();
                props.Set(KeePass.Ecas.EcasProperty.IOConnectionInfo, db.IOConnectionInfo);
                props.Set(KeePass.Ecas.EcasProperty.Database, db);
                KeePass.Program.TriggerSystem.RaiseEvent(eventId, props);
            }
            catch (Exception ex)
            {
                Log.Warn("KeePass-Trigger nicht ausgelöst: " + Log.Describe(ex));
            }
        }

        /// <summary>KeePass vergleicht beim nächsten Speichern HashOfFileOnDisk mit der Datei; beide müssen übereinstimmen</summary>
        private static bool HashMatchesDisk(Session s)
        {
            try
            {
                byte[] expected = s.Db.HashOfFileOnDisk;
                if (expected == null) return false;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(s.Db.IOConnectionInfo.Path))
                    return sha.ComputeHash(fs).SequenceEqual(expected);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (_quietSave) return;
            var s = Find(e.Database);
            if (s == null) return;
            if (!e.Success)
            {
                Log.Warn("KeePass konnte nicht speichern: " + s.Link.Name);
                return;
            }
            AfterSave(s);
        }

        private void AfterSave(Session s)
        {
            if (s.SuppressUpload)
            {
                s.SuppressUpload = false;
                s.Link.CacheCTag = s.MergedCTag;
                s.Link.CacheModified = s.MergedModified;
                Links.Save();
                Log.Debug("Cache auf Cloud-Stand gebracht, kein Hochladen nötig: " + s.Link.Name);
                return;
            }
            s.Link.LocalDirty = true;
            Links.Save();
            if (!s.SavingForClose) UploadAfterSave(s);
        }

        private async void UploadAfterSave(Session s)
        {
            if (s.Busy)
            {
                s.CheckPending = true;
                return;
            }
            s.Busy = true;
            try
            {
                await UploadCache(s);
            }
            catch (SignInRequiredException)
            {
                SetState(s, SyncState.SignInRequired, Text.T("Bitte erneut anmelden", "Please sign in again"));
            }
            catch (Exception ex) when (IsOffline(ex))
            {
                Log.Warn("Offline, Hochladen später: " + Log.Describe(ex));
                SetState(s, SyncState.Offline, Text.T("Offline – wird später hochgeladen", "Offline – will upload later"));
            }
            catch (Exception ex)
            {
                Log.Error("Hochladen fehlgeschlagen: " + s.Link.Name, ex);
                SetState(s, SyncState.Error, FriendlyError(ex));
            }
            finally
            {
                s.Busy = false;
                if (s.CheckPending)
                {
                    s.CheckPending = false;
                    Check(s, "nachgeholt");
                }
            }
        }

        private async Task UploadCache(Session s)
        {
            SetState(s, SyncState.Syncing, Text.T("Lade hoch …", "Uploading …"));
            byte[] data = File.ReadAllBytes(s.Link.CachePath);
            var started = DateTime.UtcNow;
            var upload = Graph.Upload(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId, data, s.MergedCTag);
            s.InFlight = Tuple.Create(upload, Hash(data));
            UploadResult result;
            try
            {
                result = await upload;
            }
            finally
            {
                s.InFlight = null;
            }
            if (result.Conflict)
            {
                s.ConflictRounds++;
                Log.Info("Cloud-Datei wurde inzwischen geändert (412) – führe zusammen, Runde " + s.ConflictRounds + ": " + s.Link.Name);
                if (s.ConflictRounds > 3)
                {
                    s.ConflictRounds = 0;
                    throw new InvalidOperationException(Text.T("Zu viele gleichzeitige Änderungen, bitte später erneut speichern",
                        "Too many concurrent changes, please save again later"));
                }
                var item = await Graph.Item(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId);
                var remote = await DownloadWithSkipped(s, item);
                // LocalDirty bleibt gesetzt → ApplyRemote speichert erneut und lädt mit neuem cTag hoch
                ApplyRemote(s, remote, item);
                return;
            }
            s.ConflictRounds = 0;
            s.MergedCTag = result.Item.CTag;
            s.MergedModified = result.Item.Modified;
            s.Link.CacheCTag = result.Item.CTag;
            s.Link.CacheModified = result.Item.Modified;
            s.Link.LocalDirty = false;
            s.Link.LastSync = DateTime.Now;
            Links.Save();
            Log.Info("Hochgeladen: " + s.Link.Name + " (" + Text.Size(data.Length) + ", " + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms)");
            SetState(s, SyncState.Synced, "");
        }

        internal void Shutdown()
        {
            foreach (var c in _channels.Values) c.Stop();
            _channels.Clear();
            Task[] pending;
            lock (_finishing) pending = _finishing.ToArray();
            if (pending.Length == 0) return;
            Log.Info("Warte beim Beenden auf " + pending.Length + " ausstehende(s) Hochladen");
            if (!Task.WaitAll(pending, TimeSpan.FromSeconds(20)))
                Log.Warn("Hochladen beim Beenden nicht fertig geworden – wird beim nächsten Öffnen nachgeholt");
        }

        private readonly List<Task> _finishing = new List<Task>();

        /// <summary>
        /// Nach dem Schließen noch nicht hochgeladene Änderungen ohne Oberfläche abschließen:
        /// Cache-Datei hochladen; hat sich die Cloud inzwischen geändert, Cloud-Fassung(en) in die Cache-Datei einführen und erneut versuchen.
        /// </summary>
        private void FinishAfterClose(Session s)
        {
            var key = s.ClosingKey;
            s.ClosingKey = null;
            Log.Info("Lade ausstehende Änderungen nach dem Schließen hoch: " + s.Link.Name);
            Task t = null;
            t = Task.Run(async () =>
            {
                try
                {
                    // Läuft noch ein Hochladen vom Speichern kurz vor dem Schließen, dessen Ergebnis abwarten statt parallel hochzuladen
                    var inFlight = s.InFlight;
                    if (inFlight != null)
                    {
                        try
                        {
                            var earlier = await inFlight.Item1.ConfigureAwait(false);
                            if (!earlier.Conflict)
                            {
                                s.MergedCTag = earlier.Item.CTag;
                                s.MergedModified = earlier.Item.Modified;
                                if (Hash(File.ReadAllBytes(s.Link.CachePath)) == inFlight.Item2)
                                {
                                    s.Link.CacheCTag = earlier.Item.CTag;
                                    s.Link.CacheModified = earlier.Item.Modified;
                                    s.Link.LocalDirty = false;
                                    s.Link.LastSync = DateTime.Now;
                                    Links.Save();
                                    Log.Info("Hochladen vom Speichern vor dem Schließen abgeschlossen: " + s.Link.Name);
                                    return;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("Vorheriges Hochladen fehlgeschlagen, lade erneut: " + Log.Describe(ex));
                        }
                    }
                    for (int round = 1; round <= 4; round++)
                    {
                        var result = await Graph.Upload(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId,
                            File.ReadAllBytes(s.Link.CachePath), s.MergedCTag).ConfigureAwait(false);
                        if (!result.Conflict)
                        {
                            s.MergedCTag = result.Item.CTag;
                            s.MergedModified = result.Item.Modified;
                            s.Link.CacheCTag = result.Item.CTag;
                            s.Link.CacheModified = result.Item.Modified;
                            s.Link.LocalDirty = false;
                            s.Link.LastSync = DateTime.Now;
                            Links.Save();
                            Log.Info("Hochgeladen nach dem Schließen: " + s.Link.Name);
                            return;
                        }
                        Log.Info("Cloud-Datei inzwischen geändert (412) – führe in die Cache-Datei zusammen, Runde " + round + ": " + s.Link.Name);
                        var item = await Graph.Item(s.Link.AccountId, s.Link.DriveId, s.Link.ItemId).ConfigureAwait(false);
                        var versions = await DownloadWithSkipped(s, item).ConfigureAwait(false);
                        MergeIntoCache(s, key, versions);
                        s.MergedCTag = item.CTag;
                        s.MergedModified = item.Modified;
                    }
                    Log.Warn("Hochladen nach dem Schließen aufgegeben (zu viele gleichzeitige Änderungen): " + s.Link.Name);
                }
                catch (Exception ex)
                {
                    Log.Warn("Hochladen nach dem Schließen fehlgeschlagen, wird beim nächsten Öffnen nachgeholt: " + Log.Describe(ex));
                }
                finally
                {
                    lock (_finishing) _finishing.Remove(t);
                }
            });
            lock (_finishing) _finishing.Add(t);
        }

        private static string Hash(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(data));
        }

        private static void MergeIntoCache(Session s, KeePassLib.Keys.CompositeKey key, List<byte[]> versions)
        {
            var cache = new PwDatabase();
            cache.Open(IOConnectionInfo.FromPath(s.Link.CachePath), key, new NullStatusLogger());
            string tmp = Path.Combine(Paths.Sub("temp"), Guid.NewGuid().ToString("N") + ".kdbx");
            try
            {
                foreach (var data in versions)
                {
                    File.WriteAllBytes(tmp, data);
                    var remote = new PwDatabase();
                    remote.Open(IOConnectionInfo.FromPath(tmp), key, new NullStatusLogger());
                    cache.MergeIn(remote, PwMergeMethod.Synchronize, null);
                    remote.Close();
                }
                cache.Save(new NullStatusLogger());
            }
            finally
            {
                cache.Close();
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception)
                {
                    // Temp-Datei wird beim nächsten Start aufgeräumt
                }
            }
        }
    }
}
