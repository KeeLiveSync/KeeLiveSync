using System;
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
            get { return "https://raw.githubusercontent.com/blacksun80/KeeLiveSync/main/keepass.version"; }
        }

        public override Image SmallIcon
        {
            get { return Theme.Logo(Theme.ScaleScreen(16)); }
        }

        public override bool Initialize(IPluginHost host)
        {
            if (host == null) return false;
            _host = host;
            // KeePass-Update-Prüfung: keepass.version ist mit dem Release-Schlüssel signiert (signing/sign-version.ps1);
            // unsignierte oder veränderte Dateien zeigt KeePass als Fehler statt einer falschen Version
            KeePass.Util.UpdateCheckEx.SetFileSigKey(UpdateUrl, ReleaseKey.PublicKeyXml);
            Log.Info("KeeLiveSync " + typeof(KeeLiveSyncExt).Assembly.GetName().Version.ToString(3) + " gestartet (KeePass " +
                     KeePassLib.PwDefs.VersionString + ", Debug " + (Log.DebugEnabled ? "an" : "aus") + ")");
            CleanTemp();

            _engine = new SyncEngine(host.MainWindow);
            _engine.RemoteChangesApplied += OnRemoteChangesApplied;
            _badge = new StatusBadge(host.MainWindow, _engine);
            _badge.OptionsRequested += ShowOptions;
            _badge.SignInRequested += ReSignIn;
            _engine.SignInNeeded += OnSignInNeeded;

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

        private void OnRemoteChangesApplied(Session s, string summary)
        {
            if (!Settings.NotifyRemoteChanges) return;
            var me = Accounts.Find(s.Link.AccountId);
            string who = s.Link.LastRemoteBy;
            if (me != null && who == me.Name) who = Text.T("Von einem anderen Gerät", "From another device");
            Toast.Show(_host.MainWindow, Text.T("Änderungen übernommen", "Changes applied"),
                (string.IsNullOrEmpty(who) ? "" : who + ": ") + summary + "  ·  " + s.Link.Name);
        }

        private void OnSignInNeeded(string accountId)
        {
            var acc = Accounts.Find(accountId);
            string who = acc != null ? acc.Email : Text.T("deinem Microsoft-Konto", "your Microsoft account");
            var t = Toast.Show(_host.MainWindow, Text.T("Anmeldung abgelaufen", "Sign-in expired"),
                Text.T("Bitte bei " + who + " neu anmelden. Bis dahin arbeitest du mit der lokalen Kopie.",
                    "Please sign in to " + who + " again. Until then you work with the local copy."),
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
                            Text.T("Du hast dich mit „" + acc.Email + "“ angemeldet. Für diese Datenbank wird aber „" +
                                   (expected != null ? expected.Email : "?") + "“ gebraucht.",
                                "You signed in as \"" + acc.Email + "\", but this database needs \"" +
                                (expected != null ? expected.Email : "?") + "\"."),
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
