using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KeePass.Plugins;
using KeeLiveSync.UI;

namespace KeeLiveSync
{
    /// <summary>
    /// KeeLiveSync: KeePass-Datenbanken in OneDrive live abgleichen.
    /// Datei → Öffnen → „Aus der Cloud öffnen“, Statusanzeige unten rechts, Extras → KeeLiveSync-Optionen.
    /// </summary>
    public sealed class KeeLiveSyncExt : Plugin
    {
        private IPluginHost _host;
        private SyncEngine _engine;
        private StatusBadge _badge;
        private ToolStripMenuItem _openItem;

        /// <summary>Versionsdatei für die Update-Prüfung von KeePass (Format: keepass.info/help/v2_dev/plg_index.html#upd)</summary>
        public override string UpdateUrl
        {
            get { return "https://raw.githubusercontent.com/KeeLiveSync/KeeLiveSync/main/version.txt"; }
        }

        public override Image SmallIcon
        {
            get { return Theme.Logo(Theme.ScaleScreen(16)); }
        }

        public override bool Initialize(IPluginHost host)
        {
            if (host == null) return false;
            _host = host;
            // KeePass-Update-Prüfung: version.txt ist mit dem Release-Schlüssel signiert (signing/sign-version.ps1);
            // unsignierte oder veränderte Dateien zeigt KeePass als Fehler statt einer falschen Version
            KeePass.Util.UpdateCheckEx.SetFileSigKey(UpdateUrl, ReleaseKey.PublicKeyXml);
            Log.Info("KeeLiveSync " + typeof(KeeLiveSyncExt).Assembly.GetName().Version.ToString(3) + " gestartet (KeePass " +
                     KeePassLib.PwDefs.VersionString + ", Debug " + (Log.DebugEnabled ? "an" : "aus") + ")");
            CleanTemp();

            _engine = new SyncEngine(host.MainWindow);
            _engine.RemoteChangesApplied += OnRemoteChangesApplied;
            _engine.ProblemOccurred += OnProblem;
            _badge = new StatusBadge(host.MainWindow, _engine);
            _badge.History = RecentChanges;
            _badge.OptionsRequested += ShowOptions;
            _badge.SignInRequested += ReSignIn;
            _engine.SignInNeeded += OnSignInNeeded;
            // im Infobereich/minimiert keine Einblendungen; beim Zurückholen gesammelt anzeigen
            host.MainWindow.Resize += (s, e) => ShowPending();
            host.MainWindow.VisibleChanged += (s, e) => ShowPending();
            host.MainWindow.Activated += (s, e) => ShowPending();

            _openItem = new ToolStripMenuItem(Text.T("Aus der Cloud öffnen (KeeLiveSync) …", "Open from the cloud (KeeLiveSync) …"),
                Theme.Logo(Theme.ScaleScreen(16)), (s, e) => ShowOpen());
            var fileOpen = host.MainWindow.MainMenu.Items.Find("m_menuFileOpen", true).FirstOrDefault() as ToolStripMenuItem;
            if (fileOpen != null)
            {
                int index = fileOpen.DropDownItems.IndexOfKey("m_menuFileOpenUrl");
                fileOpen.DropDownItems.Insert(index >= 0 ? index + 1 : fileOpen.DropDownItems.Count, _openItem);
            }
            else
            {
                Log.Warn("Menü Datei → Öffnen nicht gefunden");
            }
            return true;
        }

        public override ToolStripMenuItem GetMenuItem(PluginMenuType t)
        {
            if (t != PluginMenuType.Main) return null;
            var root = new ToolStripMenuItem("KeeLiveSync", Theme.Logo(Theme.ScaleScreen(16)));
            root.DropDownItems.Add(Text.T("Aus der Cloud öffnen …", "Open from the cloud …"), null, (s, e) => ShowOpen());
            root.DropDownItems.Add(Text.T("Optionen …", "Options …"), null, (s, e) => ShowOptions());
            return root;
        }

        private void ShowOpen()
        {
            using (var f = new OpenForm(_engine))
            {
                if (f.ShowDialog(_host.MainWindow) == DialogResult.OK && f.ResultLink != null)
                    _engine.OpenLink(f.ResultLink);
            }
        }

        private void ShowOptions()
        {
            using (var f = new OptionsForm(_engine)) f.ShowDialog(_host.MainWindow);
            _badge.Update();
        }

        /// <summary>Übernommene Änderungen dieser KeePass-Sitzung (nur im Speicher – Eintragstitel nicht auf die Platte)</summary>
        private readonly List<Tuple<DateTime, Session, ChangeSet, string>> _history = new List<Tuple<DateTime, Session, ChangeSet, string>>();
        private const int HistorySize = 20;

        private void OnRemoteChangesApplied(Session s, ChangeSet changes)
        {
            _history.Insert(0, Tuple.Create(DateTime.Now, s, changes, s.Link.LastRemoteBy));
            if (_history.Count > HistorySize) _history.RemoveAt(_history.Count - 1);
            if (!Settings.NotifyRemoteChanges) return;
            if (WindowHidden)
            {
                Tuple<ChangeSet, string> pending;
                string who = s.Link.LastRemoteBy;
                if (_pending.TryGetValue(s, out pending))
                    _pending[s] = Tuple.Create(Merge(pending.Item1, changes), pending.Item2 == who ? who : null);
                else
                    _pending[s] = Tuple.Create(changes, who);
                return;
            }
            ShowChanges(s, changes, s.Link.LastRemoteBy);
        }

        /// <summary>Gesammelte Änderungen, solange KeePass minimiert oder im Infobereich ist (je Datenbank; Person null = mehrere)</summary>
        private readonly Dictionary<Session, Tuple<ChangeSet, string>> _pending = new Dictionary<Session, Tuple<ChangeSet, string>>();
        private Action _pendingProblem;

        private bool WindowHidden
        {
            get { return !_host.MainWindow.Visible || _host.MainWindow.WindowState == FormWindowState.Minimized; }
        }

        private void ShowPending()
        {
            if (WindowHidden) return;
            if (_pending.Count == 1)
            {
                var only = _pending.First();
                _pending.Clear();
                ShowChanges(only.Key, only.Value.Item1, only.Value.Item2);
            }
            else if (_pending.Count > 1)
            {
                var lines = _pending.Select(kv => kv.Key.Link.Name + ":  " + Counts(kv.Value.Item1)).ToList();
                _pending.Clear();
                Toast.Show(_host.MainWindow, Text.T("Änderungen in {0} Datenbanken", "Changes in {0} databases", lines.Count),
                    string.Join("\n", lines) + "\n" + Text.T("Einzelheiten: Statusleiste → Letzte Änderungen", "Details: status bar → Recent changes"));
            }
            if (_pendingProblem != null)
            {
                var p = _pendingProblem;
                _pendingProblem = null;
                p();
            }
        }

        /// <summary>Zwei Abgleiche zusammenfassen; was später gelöscht wurde, zählt nicht mehr als neu oder geändert</summary>
        private static ChangeSet Merge(ChangeSet a, ChangeSet b)
        {
            var m = new ChangeSet();
            var removed = new HashSet<KeePassLib.PwUuid>(a.Removed.Concat(b.Removed).Select(kv => kv.Key));
            var added = new HashSet<KeePassLib.PwUuid>();
            foreach (var kv in a.Added.Concat(b.Added))
                if (!removed.Contains(kv.Key) && added.Add(kv.Key)) m.Added.Add(kv);
            var restored = new HashSet<KeePassLib.PwUuid>();
            foreach (var kv in a.Restored.Concat(b.Restored))
                if (!removed.Contains(kv.Key) && !added.Contains(kv.Key) && restored.Add(kv.Key)) m.Restored.Add(kv);
            var changed = new HashSet<KeePassLib.PwUuid>();
            foreach (var kv in a.Changed.Concat(b.Changed))
                if (!removed.Contains(kv.Key) && !added.Contains(kv.Key) && !restored.Contains(kv.Key) && changed.Add(kv.Key)) m.Changed.Add(kv);
            var gone = new HashSet<KeePassLib.PwUuid>();
            var newlyAdded = new HashSet<KeePassLib.PwUuid>(a.Added.Concat(b.Added).Select(kv => kv.Key));
            foreach (var kv in a.Removed.Concat(b.Removed))
                if (!newlyAdded.Contains(kv.Key) && gone.Add(kv.Key)) m.Removed.Add(kv);
            return m;
        }

        private static string Counts(ChangeSet c)
        {
            var parts = new List<string>();
            if (c.Added.Count > 0) parts.Add(Text.T("{0} neu", "{0} new", c.Added.Count));
            if (c.Changed.Count > 0) parts.Add(Text.T("{0} geändert", "{0} changed", c.Changed.Count));
            if (c.Removed.Count > 0) parts.Add(Text.T("{0} gelöscht", "{0} deleted", c.Removed.Count));
            if (c.Restored.Count > 0) parts.Add(Text.T("{0} wiederhergestellt", "{0} restored", c.Restored.Count));
            return string.Join(", ", parts);
        }

        private List<KeyValuePair<string, Action>> RecentChanges()
        {
            var list = new List<KeyValuePair<string, Action>>();
            foreach (var h in _history)
            {
                var item = h;
                var me = Accounts.Find(h.Item2.Link.AccountId);
                string who = string.IsNullOrEmpty(h.Item4) || (me != null && h.Item4 == me.Name)
                    ? Text.T("du auf einem anderen Gerät", "you on another device") : h.Item4;
                string text = h.Item1.ToString("t", Text.Culture) + "  ·  " + h.Item2.Link.Name + "  ·  " + who + ":  " + Counts(h.Item3);
                list.Add(new KeyValuePair<string, Action>(text, () => ShowChanges(item.Item2, item.Item3, item.Item4)));
            }
            return list;
        }

        private void ShowChanges(Session s, ChangeSet changes, string who)
        {
            var me = Accounts.Find(s.Link.AccountId);
            string title = string.IsNullOrEmpty(who) || (me != null && who == me.Name)
                ? Text.T("Änderungen übernommen · {0}", "Changes applied · {0}", s.Link.Name)
                : Text.T("Änderungen von {0} · {1}", "Changes by {0} · {1}", who, s.Link.Name);
            var lines = new List<string>();
            if (changes.Added.Count > 0) lines.Add(Text.T("Neu: {0}", "New: {0}", Names(changes.Added)));
            if (changes.Changed.Count > 0) lines.Add(Text.T("Geändert: {0}", "Changed: {0}", Names(changes.Changed)));
            if (changes.Removed.Count > 0) lines.Add(Text.T("Gelöscht: {0}", "Deleted: {0}", Names(changes.Removed)));
            if (changes.Restored.Count > 0) lines.Add(Text.T("Wiederhergestellt: {0}", "Restored: {0}", Names(changes.Restored)));
            var showable = changes.Showable;
            bool show = showable.Count > 0;
            var t = Toast.Show(_host.MainWindow, title, string.Join("\n", lines),
                show ? Text.T("Zeigen", "Show") : null, null, show ? 10000 : 0);
            if (t != null && show) t.ActionClicked += () => _engine.ShowEntries(s, showable);
        }

        /// <summary>„Amazon, Sparkasse und 3 weitere“ – höchstens drei Titel</summary>
        private static string Names(List<KeyValuePair<KeePassLib.PwUuid, string>> entries)
        {
            var titles = entries.Select(kv => string.IsNullOrEmpty(kv.Value) ? Text.T("(ohne Titel)", "(untitled)") : Text.T("„{0}“", "\"{0}\"", kv.Value)).ToList();
            string shown = string.Join(", ", titles.Take(3));
            return titles.Count > 3 ? Text.T("{0} und {1} weitere", "{0} and {1} more", shown, titles.Count - 3) : shown;
        }

        private void OnProblem(Session s, string text)
        {
            if (WindowHidden)
            {
                _pendingProblem = () => OnProblem(s, text);
                return;
            }
            bool key = s.KeyChanged;
            var t = key
                ? Toast.Show(_host.MainWindow, Text.T("Neuer Hauptschlüssel für „{0}“", "New master key for \"{0}\"", s.Link.Name),
                    Text.T("Die Datenbank wurde auf einem anderen Gerät mit einem neuen Hauptschlüssel gespeichert. Öffne sie neu und gib den neuen Hauptschlüssel ein.",
                        "The database was saved on another device with a new master key. Reopen it and enter the new master key."),
                    Text.T("Neu öffnen", "Reopen"), Theme.Warn)
                : Toast.Show(_host.MainWindow, Text.T("„{0}“ konnte nicht abgeglichen werden", "\"{0}\" could not be synced", s.Link.Name),
                    Text.T("Deine Änderungen bleiben in der lokalen Kopie erhalten; der nächste Abgleich versucht es erneut. Ursache: {0}",
                        "Your changes are kept in the local copy; the next sync will try again. Cause: {0}", text),
                    Text.T("Zur Datenbank", "Go to database"), Theme.Bad);
            if (t == null) return;
            if (key) t.ActionClicked += () => _engine.ReopenAfterKeyChange(s);
            else t.ActionClicked += () => _engine.Activate(s);
        }

        private void OnSignInNeeded(string accountId)
        {
            var acc = Accounts.Find(accountId);
            string who = acc != null ? acc.Email : Text.T("deinem Microsoft-Konto", "your Microsoft account");
            var t = Toast.Show(_host.MainWindow, Text.T("Anmeldung abgelaufen", "Sign-in expired"),
                Text.T("Bitte bei {0} neu anmelden. Bis dahin arbeitest du mit der lokalen Kopie.",
                    "Please sign in to {0} again. Until then you work with the local copy.", who),
                Text.T("Jetzt anmelden", "Sign in now"), Theme.Warn);
            if (t != null) t.ActionClicked += () => ReSignIn(accountId);
        }

        private bool _signingIn;

        private async void ReSignIn(string accountId)
        {
            if (_signingIn) return;
            _signingIn = true;
            try
            {
                var expected = Accounts.Find(accountId);
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5)))
                {
                    var acc = await Accounts.SignIn(cts.Token);
                    if (acc.Id != accountId)
                    {
                        Log.Warn("Erneute Anmeldung mit anderem Konto: " + acc.Email);
                        MessageBox.Show(_host.MainWindow,
                            Text.T("Du hast dich mit „{0}“ angemeldet. Für diese Datenbank wird aber „{1}“ gebraucht.",
                                "You signed in as \"{0}\", but this database needs \"{1}\".",
                                acc.Email, expected != null ? expected.Email : "?"),
                            "KeeLiveSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                _engine.Resume(accountId);
                _host.MainWindow.Activate();
            }
            catch (Exception ex)
            {
                Log.Error("Erneute Anmeldung fehlgeschlagen", ex);
            }
            finally
            {
                _signingIn = false;
            }
        }

        private static void CleanTemp()
        {
            try
            {
                foreach (string f in Directory.GetFiles(Paths.Sub("temp")))
                    File.Delete(f);
            }
            catch (Exception ex)
            {
                Log.Warn("Temp-Ordner nicht aufgeräumt: " + Log.Describe(ex));
            }
        }

        public override void Terminate()
        {
            if (_engine != null) _engine.Shutdown();
            Log.Info("KeeLiveSync beendet");
        }
    }
}
