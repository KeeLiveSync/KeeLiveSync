using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace KeeLiveSync.UI
{
    /// <summary>Extras → KeeLiveSync-Optionen: Konten, verknüpfte Datenbanken, Hinweise, Protokoll</summary>
    internal sealed class OptionsForm : Form
    {
        private readonly SyncEngine _engine;
        private readonly ListView _accounts;
        private readonly ListView _links;
        private readonly Button _signOut;
        private readonly Button _unlink;
        private readonly CheckBox _notify;
        private readonly CheckBox _autoSave;
        private readonly CheckBox _debug;
        private readonly Label _debugInfo;

        internal static void OpenLogFolder()
        {
            Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true });
        }

        internal OptionsForm(SyncEngine engine)
        {
            _engine = engine;
            Text = Text_("KeeLiveSync – Optionen", "KeeLiveSync – Options");
            Icon = Theme.LogoIcon(32);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(620, 668);
            Font = Theme.UiFont(9.75f);
            BackColor = Color.White;

            var logo = new PictureBox { Location = new Point(22, 18), Size = new Size(42, 42), SizeMode = PictureBoxSizeMode.Zoom, Image = Theme.Logo(Theme.ScaleScreen(42)) };
            var title = new Label { AutoSize = true, Location = new Point(76, 16), Font = Theme.SemiboldFont(13f), ForeColor = Theme.Ink, Text = "KeeLiveSync" };
            var subtitle = new Label
            {
                AutoSize = true, Location = new Point(78, 44), ForeColor = Theme.Muted,
                Text = Text_("Live-Synchronisation für KeePass · works with Microsoft OneDrive", "Live synchronization for KeePass · works with Microsoft OneDrive")
            };

            var hAccounts = Section(Text_("Microsoft-Konten", "Microsoft accounts"), 84);
            _accounts = NewList(new Point(22, 112), new Size(460, 92));
            int avatarPx = Theme.ScaleScreen(28);
            _accounts.SmallImageList = new ImageList { ImageSize = new Size(avatarPx, avatarPx), ColorDepth = ColorDepth.Depth32Bit };
            _accounts.Columns.Add(Text_("Konto", "Account"), 440);
            _accounts.SelectedIndexChanged += (s, e) => _signOut.Enabled = _accounts.SelectedItems.Count == 1;
            var addAccount = new Button { Location = new Point(494, 112), Size = new Size(104, 32), Text = Text_("Anmelden …", "Sign in …") };
            Theme.Secondary(addAccount);
            addAccount.Click += async (s, e) =>
            {
                addAccount.Enabled = false;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5))) await Accounts.SignIn(cts.Token);
                    Activate();
                }
                catch (Exception ex)
                {
                    Log.Error("Anmeldung fehlgeschlagen", ex);
                    MessageBox.Show(this, ex.Message, "KeeLiveSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                addAccount.Enabled = true;
                Fill();
            };
            _signOut = new Button { Location = new Point(494, 150), Size = new Size(104, 32), Text = Text_("Abmelden", "Sign out"), Enabled = false };
            Theme.Secondary(_signOut);
            _signOut.Click += (s, e) => SignOut();
            // Knopfbreite nach dem längsten Text (Übersetzungen sind oft länger), Liste rückt entsprechend
            int buttonWidth = Math.Max(104, Math.Max(TextRenderer.MeasureText(addAccount.Text, addAccount.Font).Width,
                TextRenderer.MeasureText(_signOut.Text, _signOut.Font).Width) + 32);
            addAccount.Width = _signOut.Width = buttonWidth;
            addAccount.Left = _signOut.Left = 598 - buttonWidth;
            _accounts.Width = addAccount.Left - 12 - 22;
            _accounts.Columns[0].Width = _accounts.Width - 20;

            var hLinks = Section(Text_("Verknüpfte Datenbanken", "Linked databases"), 222);
            _links = NewList(new Point(22, 250), new Size(576, 130));
            _links.Columns.Add(Text_("Datenbank", "Database"), 170);
            _links.Columns.Add(Text_("Konto", "Account"), 170);
            _links.Columns.Add(Text_("Abgeglichen", "Synced"), 130);
            _links.Columns.Add(Text_("Status", "Status"), 100);
            _links.SelectedIndexChanged += (s, e) => _unlink.Enabled = _links.SelectedItems.Count == 1;
            _unlink = new Button { Location = new Point(22, 388), Size = new Size(170, 32), Text = Text_("Verknüpfung lösen …", "Unlink …"), Enabled = false };
            Theme.Secondary(_unlink);
            _unlink.Click += (s, e) => Unlink();

            var hNotify = Section(Text_("Abgleich", "Sync"), 436);
            _autoSave = new CheckBox
            {
                AutoSize = true, Location = new Point(24, 464), Checked = Settings.AutoSave,
                Text = Text_("Änderungen sofort speichern und hochladen", "Save and upload changes immediately")
            };
            _autoSave.CheckedChanged += (s, e) => Settings.AutoSave = _autoSave.Checked;
            _notify = new CheckBox
            {
                AutoSize = true, Location = new Point(24, 492), Checked = Settings.NotifyRemoteChanges,
                Text = Text_("Einblenden, wenn Änderungen von anderen übernommen wurden", "Show a notice when changes from others were applied")
            };
            _notify.CheckedChanged += (s, e) => Settings.NotifyRemoteChanges = _notify.Checked;

            var hDebug = Section(Text_("Fehlersuche", "Troubleshooting"), 532);
            _debug = new CheckBox
            {
                AutoSize = true, Location = new Point(24, 560), Checked = Log.DebugEnabled,
                Text = Text_("Ausführliches Protokoll (schaltet sich nach 24 Stunden selbst ab)", "Detailed log (switches itself off after 24 hours)")
            };
            _debug.CheckedChanged += (s, e) =>
            {
                Settings.DebugUntil = _debug.Checked ? DateTime.UtcNow.AddHours(24) : DateTime.MinValue;
                Log.Info("Ausführliches Protokoll " + (_debug.Checked ? "eingeschaltet" : "ausgeschaltet"));
                UpdateDebugInfo();
            };
            _debugInfo = new Label { AutoSize = false, Location = new Point(42, 584), Size = new Size(400, 20), ForeColor = Theme.Muted };
            var openLog = new Button { Location = new Point(448, 566), Size = new Size(150, 32), Text = Text_("Protokollordner", "Log folder") };
            Theme.Secondary(openLog);
            openLog.Click += (s, e) => OpenLogFolder();

            var footer = new Panel { Location = new Point(0, 614), Size = new Size(620, 54), BackColor = Theme.Surface };
            var version = new Label
            {
                AutoSize = true, Location = new Point(22, 18), ForeColor = Theme.Muted,
                Text = "Version " + typeof(OptionsForm).Assembly.GetName().Version.ToString(3)
            };
            var close = new Button { Location = new Point(490, 10), Size = new Size(108, 34), Text = Text_("Schließen", "Close"), DialogResult = DialogResult.OK };
            Theme.Primary(close);
            footer.Controls.AddRange(new Control[] { version, close });
            AcceptButton = close;
            CancelButton = close;

            Controls.AddRange(new Control[]
            {
                logo, title, subtitle, hAccounts, _accounts, addAccount, _signOut, hLinks, _links, _unlink,
                hNotify, _autoSave, _notify, hDebug, _debug, _debugInfo, openLog, footer
            });
            Fill();
            UpdateDebugInfo();
        }

        private static string Text_(string de, string en, params object[] args)
        {
            if (args.Length > 0) return KeeLiveSync.Text.T(de, en, args);
            return KeeLiveSync.Text.T(de, en);
        }

        private static Label Section(string text, int top)
        {
            return new Label { AutoSize = true, Location = new Point(20, top), Font = Theme.SemiboldFont(10.5f), ForeColor = Theme.Accent, Text = text };
        }

        private static ListView NewList(Point location, Size size)
        {
            return new ListView
            {
                Location = location, Size = size, View = View.Details, FullRowSelect = true, HideSelection = false,
                MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.FixedSingle
            };
        }

        private void UpdateDebugInfo()
        {
            _debugInfo.Text = Log.DebugEnabled
                ? Text_("aktiv bis {0}", "active until {0}", KeeLiveSync.Text.DateAndTime(Settings.DebugUntil.ToLocalTime()))
                : Text_("Fehler und wichtige Ereignisse werden immer protokolliert.", "Errors and important events are always logged.");
        }

        private void Fill()
        {
            _accounts.Items.Clear();
            _accounts.SmallImageList.Images.Clear();
            foreach (var a in Accounts.All)
            {
                _accounts.SmallImageList.Images.Add(a.Id, Theme.Avatar(a.Initials, _accounts.SmallImageList.ImageSize.Width));
                _accounts.Items.Add(new ListViewItem(a.Name + "  ·  " + a.Email + (a.DriveType == "business" ? "  ·  Business" : ""), a.Id) { Tag = a });
            }
            _links.Items.Clear();
            foreach (var l in Links.All.OrderBy(x => x.Name))
            {
                var acc = Accounts.Find(l.AccountId);
                bool open = _engine.Sessions.Any(s => s.Link.Key == l.Key);
                var lvi = new ListViewItem(l.Name) { Tag = l };
                lvi.SubItems.Add(acc != null ? acc.Email : Text_("(abgemeldet)", "(signed out)"));
                lvi.SubItems.Add(KeeLiveSync.Text.Ago(l.LastSync));
                lvi.SubItems.Add(l.LocalDirty ? Text_("Upload ausstehend", "Upload pending") : open ? Text_("geöffnet", "open") : "");
                _links.Items.Add(lvi);
            }
            _signOut.Enabled = false;
            _unlink.Enabled = false;
        }

        private void SignOut()
        {
            var a = _accounts.SelectedItems.Count == 1 ? _accounts.SelectedItems[0].Tag as Account : null;
            if (a == null) return;
            if (MessageBox.Show(this, Text_("„{0}“ abmelden? Verknüpfte Datenbanken dieses Kontos werden erst wieder abgeglichen, wenn du dich erneut anmeldest.",
                        "Sign out \"{0}\"? Linked databases of this account will sync again after you sign in again.", a.Email),
                    "KeeLiveSync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Accounts.Remove(a.Id);
            Fill();
        }

        private void Unlink()
        {
            var l = _links.SelectedItems.Count == 1 ? _links.SelectedItems[0].Tag as Link : null;
            if (l == null) return;
            if (_engine.Sessions.Any(s => s.Link.Key == l.Key))
            {
                MessageBox.Show(this, Text_("Bitte die Datenbank zuerst schließen.", "Please close the database first."), "KeeLiveSync",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string warn = l.LocalDirty
                ? Text_("\n\nAchtung: Die lokale Kopie enthält Änderungen, die noch nicht hochgeladen sind. Sie bleibt als Datei erhalten.",
                    "\n\nNote: the local copy contains changes that have not been uploaded yet. It is kept as a file.")
                : "";
            if (MessageBox.Show(this, Text_("Verknüpfung zu „{0}“ lösen? Die Datei in OneDrive bleibt unverändert.",
                        "Unlink \"{0}\"? The file in OneDrive stays unchanged.", l.Name) + warn,
                    "KeeLiveSync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Links.Remove(l);
            if (!l.LocalDirty)
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(l.CachePath), true);
                }
                catch (Exception ex)
                {
                    Log.Warn("Cache-Ordner nicht gelöscht: " + Log.Describe(ex));
                }
            }
            Log.Info("Verknüpfung gelöst: " + l.Name);
            Fill();
        }
    }
}
