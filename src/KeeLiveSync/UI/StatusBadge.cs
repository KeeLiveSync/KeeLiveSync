using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using KeePass.Forms;

namespace KeeLiveSync.UI
{
    /// <summary>
    /// Anzeige in der KeePass-Statusleiste für die aktive Datenbank: farbige Wolke + kurzer Text
    /// („Live · synchron“, „Lade hoch …“, „Offline“ …). Klick öffnet ein kleines Menü.
    /// </summary>
    internal sealed class StatusBadge
    {
        private readonly MainForm _main;
        private readonly SyncEngine _engine;
        private readonly ToolStripStatusLabel _label;
        private readonly Timer _tick;

        internal event Action OptionsRequested;
        internal event Action<string> SignInRequested;

        internal StatusBadge(MainForm main, SyncEngine engine)
        {
            _main = main;
            _engine = engine;
            _label = new ToolStripStatusLabel
            {
                Visible = false,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Margin = new Padding(8, 3, 4, 2),
                IsLink = false
            };
            _label.Click += (s, e) => ShowMenu();
            var strip = main.Controls.Find("m_statusMain", true).FirstOrDefault() as StatusStrip;
            if (strip != null) strip.Items.Add(_label);
            else Log.Warn("Statusleiste von KeePass nicht gefunden");
            engine.StateChanged += Update;
            main.UIStateUpdated += (s, e) => Update();
            _tick = new Timer { Interval = 30000 };
            _tick.Tick += (s, e) => Update();
            _tick.Start();
        }

        private Session Active
        {
            get { return _engine.Find(_main.ActiveDatabase); }
        }

        internal void Update()
        {
            var s = Active;
            if (s == null)
            {
                _label.Visible = false;
                return;
            }
            bool push = _engine.PushConnected(s);
            string text;
            switch (s.State)
            {
                case SyncState.Synced:
                    text = push ? Text.T("Live · synchron", "Live · in sync") : Text.T("Synchron · Live-Verbindung wird aufgebaut", "In sync · connecting live");
                    break;
                case SyncState.Syncing:
                    text = s.StateText;
                    break;
                default:
                    text = s.StateText;
                    break;
            }
            int px = Theme.ScaleScreen(16);
            var old = _label.Image;
            _label.Image = Theme.StatusIcon(px, Theme.StateColor(s.State, push), s.State == SyncState.Offline);
            if (old != null) old.Dispose();
            _label.Text = text;
            _label.ToolTipText = "KeeLiveSync – " + s.Link.Name + "\n" +
                                 Text.T("Zuletzt abgeglichen: ", "Last synced: ") + Text.Ago(s.Link.LastSync) +
                                 (string.IsNullOrEmpty(s.Link.LastRemoteBy) ? "" : "\n" + Text.T("Zuletzt geändert von: ", "Last changed by: ") + s.Link.LastRemoteBy) +
                                 (string.IsNullOrEmpty(s.Link.OwnerName) ? "" : "\n" + Text.T("Freigegeben von: ", "Shared by: ") + s.Link.OwnerName) +
                                 "\n" + (push ? Text.T("Live-Verbindung aktiv", "Live connection active") : Text.T("Keine Live-Verbindung", "No live connection"));
            _label.Visible = true;
        }

        private void ShowMenu()
        {
            var s = Active;
            if (s == null) return;
            var menu = new ContextMenuStrip();
            if (s.State == SyncState.SignInRequired)
            {
                var item = menu.Items.Add(Text.T("Jetzt bei Microsoft anmelden …", "Sign in to Microsoft now …"), null, (o, e) =>
                {
                    var h = SignInRequested;
                    if (h != null) h(s.Link.AccountId);
                });
                item.Font = Theme.SemiboldFont(9f);
            }
            menu.Items.Add(Text.T("Jetzt abgleichen", "Sync now"), null, (o, e) => _engine.Check(s, "manuell"));
            menu.Items.Add(Text.T("Protokoll öffnen", "Open log"), null, (o, e) => OptionsForm.OpenLogFolder());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Text.T("KeeLiveSync-Optionen …", "KeeLiveSync options …"), null, (o, e) =>
            {
                var h = OptionsRequested;
                if (h != null) h();
            });
            var strip = _label.Owner;
            menu.Show(strip, new Point(_label.Bounds.Left, 0), ToolStripDropDownDirection.AboveRight);
        }
    }
}
