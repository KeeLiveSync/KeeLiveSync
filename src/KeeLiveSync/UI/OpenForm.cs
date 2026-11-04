using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KeeLiveSync.UI
{
    /// <summary>
    /// „Aus der Cloud öffnen“: Konto wählen bzw. anmelden, dann eine .kdbx im eigenen OneDrive suchen
    /// oder einen Freigabelink einfügen (Datei eines anderen Kontos).
    /// </summary>
    internal sealed class OpenForm : Form
    {
        private readonly SyncEngine _engine;
        private Account _account;
        private DriveItem _selected;
        private readonly Stack<DriveItem> _path = new Stack<DriveItem>();
        /// <summary>Oberste sichtbare Zeile je Ordner (Ordner-ID, "" = Stammordner), für die Rückkehr mit „Zurück“</summary>
        private readonly Dictionary<string, string> _topByFolder = new Dictionary<string, string>();
        private string _selectAfterLoad;
        private CancellationTokenSource _signInCancel;
        private int _loadToken;

        private readonly Panel _accountBar;
        private readonly PictureBox _avatar;
        private readonly Label _accountName;
        private readonly Label _accountMail;
        private readonly Button _signIn;
        private readonly LinkLabel _switchAccount;
        private readonly Label _tabBrowse;
        private readonly Label _tabLink;
        private readonly Panel _tabLine;
        private readonly Panel _browsePage;
        private readonly Panel _linkPage;
        private readonly Button _up;
        private readonly Button _refresh;
        private readonly Label _crumb;
        private readonly ListView _list;
        private readonly Label _empty;
        private readonly TextBox _linkBox;
        private readonly Button _linkCheck;
        private readonly Panel _linkChip;
        private readonly Label _linkChipText;
        private readonly System.Windows.Forms.Timer _linkPasteTimer;
        private readonly Panel _linkCard;
        private readonly Label _linkCardTitle;
        private readonly Label _linkCardInfo;
        private readonly ProgressBar _busy;
        private readonly Label _status;
        private readonly Button _open;
        private readonly Button _cancel;

        internal Link ResultLink { get; private set; }

        internal OpenForm(SyncEngine engine)
        {
            _engine = engine;
            Text = "KeeLiveSync";
            Icon = Theme.LogoIcon(32);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(660, 580);
            Font = Theme.UiFont(9.75f);
            BackColor = Color.White;
            KeyPreview = true;

            // Kopf
            var header = new Panel { Location = new Point(0, 0), Size = new Size(660, 78), BackColor = Color.White };
            var logo = new PictureBox { Location = new Point(22, 18), Size = new Size(42, 42), SizeMode = PictureBoxSizeMode.Zoom, Image = Theme.Logo(Theme.ScaleScreen(42)) };
            var title = new Label
            {
                AutoSize = true, Location = new Point(76, 16), Font = Theme.SemiboldFont(13f), ForeColor = Theme.Ink,
                Text = Text_("Datenbank aus der Cloud öffnen", "Open database from the cloud")
            };
            var subtitle = new Label
            {
                AutoSize = false, Location = new Point(78, 44), Size = new Size(560, 22), ForeColor = Theme.Muted, AutoEllipsis = true,
                Text = Text_("Öffnet sofort aus dem Zwischenspeicher und hält die Datei live mit OneDrive abgeglichen.",
                    "Opens instantly from the local cache and keeps the file in sync with OneDrive live.")
            };
            header.Controls.AddRange(new Control[] { logo, title, subtitle });
            _busy = new ProgressBar { Location = new Point(0, 78), Size = new Size(660, 3), Style = ProgressBarStyle.Marquee, Visible = false };

            // Konto
            _accountBar = new Panel { Location = new Point(0, 81), Size = new Size(660, 62), BackColor = Theme.Surface };
            _accountBar.Paint += (s, e) =>
            {
                using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, _accountBar.Height - 1, _accountBar.Width, _accountBar.Height - 1);
            };
            _avatar = new PictureBox { Location = new Point(22, 13), Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom };
            _accountName = new Label { AutoSize = true, Location = new Point(68, 12), Font = Theme.SemiboldFont(10f), ForeColor = Theme.Ink };
            _accountMail = new Label { AutoSize = true, Location = new Point(68, 32), ForeColor = Theme.Muted };
            _switchAccount = new LinkLabel
            {
                AutoSize = true, Location = new Point(520, 22), LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentLight,
                LinkBehavior = LinkBehavior.HoverUnderline, Text = Text_("Konto wechseln", "Switch account")
            };
            _switchAccount.LinkClicked += (s, e) => ShowAccountMenu();
            _signIn = new Button { Location = new Point(22, 13), Size = new Size(230, 36), Text = Text_("Mit Microsoft anmelden", "Sign in with Microsoft") };
            Theme.Primary(_signIn);
            _signIn.Click += async (s, e) => await SignIn();
            _accountBar.Controls.AddRange(new Control[] { _avatar, _accountName, _accountMail, _switchAccount, _signIn });

            // Reiter
            _tabBrowse = NewTab(Text_("Mein OneDrive", "My OneDrive"), 22);
            _tabLink = NewTab(Text_("Freigabelink", "Sharing link"), 22 + _tabBrowse.PreferredWidth + 28);
            _tabLine = new Panel { BackColor = Theme.Accent, Size = new Size(10, 3) };
            _tabBrowse.Click += (s, e) => ShowPage(true);
            _tabLink.Click += (s, e) => ShowPage(false);

            // Seite: Durchsuchen
            _browsePage = new Panel { Location = new Point(0, 190), Size = new Size(660, 318) };
            _up = new Button { Location = new Point(22, 4), Size = new Size(30, 28), Image = Theme.UpIcon(Theme.ScaleScreen(18)) };
            Theme.Secondary(_up);
            _up.Click += (s, e) => GoUp();
            var tips = new ToolTip();
            tips.SetToolTip(_up, Text_("Übergeordneter Ordner", "Parent folder"));
            _refresh = new Button { Location = new Point(608, 4), Size = new Size(30, 28), Image = Theme.RefreshIcon(Theme.ScaleScreen(18)) };
            Theme.Secondary(_refresh);
            _refresh.Click += (s, e) => RefreshFolder();
            tips.SetToolTip(_refresh, Text_("Aktualisieren (F5)", "Refresh (F5)"));
            _crumb = new Label { AutoSize = false, Location = new Point(60, 4), Size = new Size(540, 28), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, AutoEllipsis = true };
            var listFrame = new Panel { Location = new Point(22, 40), Size = new Size(616, 272), BackColor = Theme.Line, Padding = new Padding(1) };
            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            int iconPx = Theme.ScaleScreen(24);
            var images = new ImageList { ImageSize = new Size(iconPx, iconPx), ColorDepth = ColorDepth.Depth32Bit };
            images.Images.Add("folder", Theme.FolderIcon(iconPx));
            images.Images.Add("kdbx", Theme.KdbxIcon(iconPx));
            _list.SmallImageList = images;
            _list.Columns.Add(Text_("Name", "Name"), 330);
            _list.Columns.Add(Text_("Geändert", "Modified"), 170);
            _list.Columns.Add(Text_("Größe", "Size"), 90, HorizontalAlignment.Right);
            _list.SelectedIndexChanged += (s, e) => OnListSelection();
            _list.ItemActivate += (s, e) => OnListActivate();
            listFrame.Controls.Add(_list);
            _empty = new Label
            {
                AutoSize = false, Location = new Point(23, 120), Size = new Size(614, 60), TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.Muted, BackColor = Color.White, Visible = false,
                Text = Text_("Keine KeePass-Datenbanken (.kdbx) in diesem Ordner.", "No KeePass databases (.kdbx) in this folder.")
            };
            _browsePage.Controls.AddRange(new Control[] { _empty, _up, _refresh, _crumb, listFrame });
            _empty.BringToFront();

            // Seite: Freigabelink
            _linkPage = new Panel { Location = new Point(0, 190), Size = new Size(660, 318), Visible = false };
            var linkHint = new Label
            {
                AutoSize = false, Location = new Point(22, 6), Size = new Size(616, 40), ForeColor = Theme.Muted,
                Text = Text_("Für eine Datenbank, die dir jemand freigegeben hat: Link aus OneDrive („Teilen“ → „Link kopieren“) hier einfügen.",
                    "For a database someone shared with you: paste the link from OneDrive (\"Share\" → \"Copy link\") here.")
            };
            _linkBox = new TextBox { Location = new Point(22, 52), Size = new Size(500, 30), Font = Theme.UiFont(10.5f) };
            _linkBox.HandleCreated += (s, e) => SetCueBanner(_linkBox, Text_("https://1drv.ms/… hier einfügen", "Paste https://1drv.ms/… here"));
            // eingefügter Link: kurz warten, dann selbst prüfen
            _linkPasteTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _linkPasteTimer.Tick += async (s, e) =>
            {
                _linkPasteTimer.Stop();
                await CheckLink();
            };
            _linkBox.TextChanged += (s, e) =>
            {
                _linkPasteTimer.Stop();
                if (_linkBox.Text.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase) && _linkBox.Text.Trim().Length > 20) _linkPasteTimer.Start();
            };
            // geprüfter Link kompakt als „Chip“ statt der langen Adresse
            _linkChip = new Panel { Location = new Point(22, 51), Size = new Size(500, 32), BackColor = Theme.Surface, Visible = false };
            _linkChip.Paint += (s, e) =>
            {
                using (var p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, _linkChip.Width - 1, _linkChip.Height - 1);
            };
            var chipIcon = new Label { AutoSize = false, Location = new Point(8, 5), Size = new Size(22, 22), Text = "🔗", Font = new Font("Segoe UI Emoji", 10f), TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent };
            _linkChipText = new Label { AutoSize = false, Location = new Point(34, 7), Size = new Size(456, 20), AutoEllipsis = true, ForeColor = Theme.Ink, BackColor = Color.Transparent };
            _linkChip.Controls.AddRange(new Control[] { chipIcon, _linkChipText });
            _linkCheck = new Button { Location = new Point(530, 50), Size = new Size(108, 32), Text = Text_("Prüfen", "Check") };
            Theme.Secondary(_linkCheck);
            _linkCheck.Click += async (s, e) =>
            {
                if (_linkChip.Visible) EditLink();
                else await CheckLink();
            };
            _linkBox.KeyDown += async (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                await CheckLink();
            };
            _linkCard = new Panel { Location = new Point(22, 100), Size = new Size(616, 76), BackColor = Theme.AccentSoft, Visible = false };
            var cardIcon = new PictureBox { Location = new Point(16, 18), Size = new Size(40, 40), SizeMode = PictureBoxSizeMode.Zoom, Image = Theme.KdbxIcon(Theme.ScaleScreen(40)) };
            _linkCardTitle = new Label { AutoSize = true, Location = new Point(68, 16), Font = Theme.SemiboldFont(10.5f), ForeColor = Theme.Ink };
            _linkCardInfo = new Label { AutoSize = false, Location = new Point(68, 40), Size = new Size(536, 22), ForeColor = Theme.Muted, AutoEllipsis = true };
            _linkCard.Controls.AddRange(new Control[] { cardIcon, _linkCardTitle, _linkCardInfo });
            _linkPage.Controls.AddRange(new Control[] { linkHint, _linkChip, _linkBox, _linkCheck, _linkCard });

            // Fuß
            var footer = new Panel { Location = new Point(0, 516), Size = new Size(660, 64), BackColor = Theme.Surface };
            footer.Paint += (s, e) =>
            {
                using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };
            _status = new Label { AutoSize = false, Location = new Point(22, 14), Size = new Size(370, 36), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, AutoEllipsis = true };
            _open = new Button { Location = new Point(530, 14), Size = new Size(108, 36), Text = Text_("Öffnen", "Open"), Enabled = false };
            Theme.Primary(_open);
            _open.BackColor = Color.FromArgb(180, 205, 203);
            _open.Click += async (s, e) => await OpenSelected();
            _cancel = new Button { Location = new Point(412, 14), Size = new Size(108, 36), Text = Text_("Abbrechen", "Cancel") };
            Theme.Secondary(_cancel);
            _cancel.Click += (s, e) =>
            {
                if (_signInCancel != null) _signInCancel.Cancel();
                else Close();
            };
            footer.Controls.AddRange(new Control[] { _status, _open, _cancel });

            Controls.AddRange(new Control[] { header, _busy, _accountBar, _tabBrowse, _tabLink, _tabLine, _browsePage, _linkPage, footer });
            AcceptButton = _open;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) _cancel.PerformClick();
                if (e.KeyCode == Keys.Back && _browsePage.Visible && !_linkBox.Focused) GoUp();
                if (e.KeyCode == Keys.F5 && _browsePage.Visible) RefreshFolder();
            };
        }

        private static string Text_(string de, string en, params object[] args)
        {
            if (args.Length > 0) return KeeLiveSync.Text.T(de, en, args);
            return KeeLiveSync.Text.T(de, en);
        }

        private Label NewTab(string text, int left)
        {
            return new Label
            {
                AutoSize = true, Location = new Point(left, 156), Text = text, Font = Theme.SemiboldFont(10f),
                ForeColor = Theme.Muted, Cursor = Cursors.Hand
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var last = KeePass.Program.Config.CustomConfig.GetString("KeeLiveSync.LastAccount", "");
            _account = Accounts.Find(last) ?? Accounts.All.FirstOrDefault();
            ShowPage(true);
            UpdateAccount();
        }

        private void ShowPage(bool browse)
        {
            _browsePage.Visible = browse;
            _linkPage.Visible = !browse;
            _tabBrowse.ForeColor = browse ? Theme.Accent : Theme.Muted;
            _tabLink.ForeColor = browse ? Theme.Muted : Theme.Accent;
            var active = browse ? _tabBrowse : _tabLink;
            _tabLine.Location = new Point(active.Left, active.Bottom + 4);
            _tabLine.Width = active.Width;
            SetSelected(null);
            if (!browse) _linkBox.Focus();
        }

        private void SetBusy(bool busy, string text)
        {
            _busy.Visible = busy;
            _status.ForeColor = Theme.Muted;
            _status.Text = text ?? "";
            UseWaitCursor = busy && _signInCancel == null;
        }

        private void ShowError(string text)
        {
            _busy.Visible = false;
            UseWaitCursor = false;
            _status.ForeColor = Theme.Bad;
            _status.Text = text;
        }

        private void SetSelected(DriveItem item)
        {
            _selected = item != null && item.IsKdbx ? item : null;
            _open.Enabled = _selected != null;
        }

        // ---------- Konto ----------

        private void UpdateAccount()
        {
            bool has = _account != null;
            _signIn.Visible = !has;
            _avatar.Visible = _accountName.Visible = _accountMail.Visible = _switchAccount.Visible = has;
            _tabBrowse.Enabled = _tabLink.Enabled = has;
            _browsePage.Enabled = _linkPage.Enabled = has;
            if (!has)
            {
                _list.Items.Clear();
                _crumb.Text = "";
                SetBusy(false, Text_("Melde dich an, um deine Datenbanken zu sehen.", "Sign in to see your databases."));
                return;
            }
            _avatar.Image = Theme.Avatar(_account.Initials, Theme.ScaleScreen(36));
            _accountName.Text = _account.Name;
            _accountMail.Text = _account.Email + (_account.DriveType == "business" ? "  ·  OneDrive for Business" : "");
            _switchAccount.Left = _accountBar.ClientSize.Width - _switchAccount.Width - Theme.Scale(this, 22);
            KeePass.Program.Config.CustomConfig.SetString("KeeLiveSync.LastAccount", _account.Id);
            _path.Clear();
            LoadFolder(null);
        }

        private void ShowAccountMenu()
        {
            var menu = new ContextMenuStrip { Font = Font, ShowImageMargin = true };
            foreach (var a in Accounts.All)
            {
                var acc = a;
                var item = new ToolStripMenuItem(a.Name + "  <" + a.Email + ">", Theme.Avatar(a.Initials, Theme.ScaleScreen(20)));
                // aktuelles Konto fett (ein Häkchen wäre neben dem Avatar nicht sichtbar)
                if (_account != null && a.Id == _account.Id) item.Font = new Font(Font, FontStyle.Bold);
                item.Click += (s, e) =>
                {
                    _account = acc;
                    UpdateAccount();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Text_("Weiteres Konto anmelden …", "Sign in another account …"), null, async (s, e) => await SignIn());
            var signOut = new ToolStripMenuItem(Text_("Konto abmelden", "Sign out account")) { Enabled = Accounts.All.Count > 0 };
            foreach (var a in Accounts.All)
            {
                var acc = a;
                signOut.DropDownItems.Add(a.Email, Theme.Avatar(a.Initials, Theme.ScaleScreen(20)), (s, e) => SignOutAccount(acc));
            }
            menu.Items.Add(signOut);
            menu.Show(_switchAccount, new Point(0, _switchAccount.Height));
        }

        /// <summary>Konto nach Rückfrage abmelden (Anmeldung und Token werden gelöscht; verknüpfte Datenbanken bleiben lokal erhalten)</summary>
        private void SignOutAccount(Account acc)
        {
            if (MessageBox.Show(this, Text_("„{0}“ abmelden? Verknüpfte Datenbanken dieses Kontos werden erst wieder abgeglichen, wenn du dich erneut anmeldest.",
                        "Sign out \"{0}\"? Linked databases of this account will sync again after you sign in again.", acc.Email),
                    "KeeLiveSync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Log.Info("Konto abgemeldet: " + acc.Email);
            Accounts.Remove(acc.Id);
            if (_account == null || _account.Id == acc.Id)
            {
                _account = Accounts.All.FirstOrDefault();
                UpdateAccount();
            }
        }

        private async Task SignIn()
        {
            _signInCancel = new CancellationTokenSource();
            SetBusy(true, Text_("Anmeldung im Browser läuft … (Abbrechen beendet das Warten)", "Signing in in your browser … (Cancel stops waiting)"));
            _signIn.Enabled = false;
            try
            {
                _account = await Accounts.SignIn(_signInCancel.Token);
                Activate();
                SetBusy(false, "");
                UpdateAccount();
            }
            catch (OperationCanceledException)
            {
                SetBusy(false, Text_("Anmeldung abgebrochen.", "Sign-in cancelled."));
            }
            catch (Exception ex)
            {
                Log.Error("Anmeldung fehlgeschlagen", ex);
                ShowError(Text_("Anmeldung fehlgeschlagen: ", "Sign-in failed: ") + ex.Message);
            }
            finally
            {
                _signInCancel = null;
                _signIn.Enabled = true;
            }
        }

        // ---------- Durchsuchen ----------

        private async void LoadFolder(DriveItem folder)
        {
            int token = ++_loadToken;
            SetSelected(null);
            _list.Items.Clear();
            _empty.Visible = false;
            _up.Enabled = _path.Count > 0;
            _crumb.Text = "OneDrive" + string.Concat(_path.Reverse().Select(f => "  ›  " + f.Name));
            SetBusy(true, Text_("Lade Ordner …", "Loading folder …"));
            try
            {
                var items = await Graph.Children(_account.Id, _account.DriveId, folder != null ? folder.Id : null);
                if (token != _loadToken) return;
                // Freigegebene Ordner/Verknüpfungen liegen auf einem anderen Laufwerk
                var shown = items.Where(i => i.IsFolder || i.IsKdbx)
                    .OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                _list.BeginUpdate();
                foreach (var i in shown)
                {
                    var lvi = new ListViewItem(i.Name, i.IsFolder ? "folder" : "kdbx") { Tag = i };
                    lvi.SubItems.Add(i.Modified == DateTime.MinValue ? "" : KeeLiveSync.Text.Ago(i.Modified) +
                        (string.IsNullOrEmpty(i.ModifiedBy) || i.IsFolder ? "" : " · " + i.ModifiedBy.Split(' ')[0]));
                    lvi.SubItems.Add(i.IsFolder ? KeeLiveSync.Text.Plural(i.ChildCount, "{0} Element", "{0} Elemente", "{0} item", "{0} items") : KeeLiveSync.Text.Size(i.Size));
                    if (i.IsKdbx) lvi.Font = Theme.SemiboldFont(9.75f);
                    _list.Items.Add(lvi);
                }
                _list.EndUpdate();
                RestoreScroll(folder != null ? folder.Id : "");
                _empty.Visible = !shown.Any(i => i.IsKdbx) && !shown.Any(i => i.IsFolder);
                int dbs = shown.Count(i => i.IsKdbx);
                SetBusy(false, dbs > 0
                    ? KeeLiveSync.Text.Plural(dbs, "{0} Datenbank in diesem Ordner", "{0} Datenbanken in diesem Ordner", "{0} database in this folder", "{0} databases in this folder")
                    : "");
            }
            catch (SignInRequiredException ex)
            {
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                if (token != _loadToken) return;
                Log.Error("Ordner laden fehlgeschlagen", ex);
                ShowError(Text_("Ordner konnte nicht geladen werden: ", "Could not load folder: ") + ex.Message);
            }
        }

        /// <summary>Aktuellen Ordner neu laden; Scrollposition und Markierung bleiben</summary>
        private async void RefreshFolder()
        {
            if (_account == null) return;
            RememberScroll();
            var current = _path.Count > 0 ? _path.Peek() : null;
            var selected = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as DriveItem : null;
            if (selected != null) _selectAfterLoad = selected.Id;
            if (current != null && current.DriveId != null && current.DriveId != _account.DriveId)
            {
                // freigegebener Ordner auf fremdem Laufwerk
                _path.Pop();
                await LoadRemoteFolder(current);
            }
            else LoadFolder(current);
        }

        private void GoUp()
        {
            if (_path.Count == 0) return;
            _selectAfterLoad = _path.Pop().Id;
            LoadFolder(_path.Count > 0 ? _path.Peek() : null);
        }

        private void RememberScroll()
        {
            string key = _path.Count > 0 ? _path.Peek().Id : "";
            var top = _list.TopItem != null ? _list.TopItem.Tag as DriveItem : null;
            if (top != null) _topByFolder[key] = top.Id;
        }

        /// <summary>Scrollposition des Ordners wiederherstellen und den Ordner markieren, aus dem man zurückkommt</summary>
        private void RestoreScroll(string key)
        {
            string topId, select = _selectAfterLoad;
            _selectAfterLoad = null;
            _topByFolder.TryGetValue(key, out topId);
            foreach (ListViewItem lvi in _list.Items)
            {
                var item = (DriveItem)lvi.Tag;
                if (topId != null && item.Id == topId) _list.TopItem = lvi;
            }
            foreach (ListViewItem lvi in _list.Items)
            {
                if (select == null || ((DriveItem)lvi.Tag).Id != select) continue;
                lvi.Selected = true;
                lvi.Focused = true;
                lvi.EnsureVisible();
            }
        }

        private void OnListSelection()
        {
            var item = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as DriveItem : null;
            SetSelected(item);
        }

        private async void OnListActivate()
        {
            var item = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as DriveItem : null;
            if (item == null) return;
            if (item.IsFolder)
            {
                if (item.DriveId != null && item.DriveId != _account.DriveId)
                {
                    // Verknüpfung auf einen freigegebenen Ordner: Inhalt liegt auf dem Laufwerk des Besitzers
                    await LoadRemoteFolder(item);
                    return;
                }
                RememberScroll();
                _path.Push(item);
                LoadFolder(item);
            }
            else if (item.IsKdbx)
            {
                await OpenSelected();
            }
        }

        private async Task LoadRemoteFolder(DriveItem folder)
        {
            SetBusy(true, Text_("Lade freigegebenen Ordner …", "Loading shared folder …"));
            try
            {
                var items = await Graph.Children(_account.Id, folder.DriveId, folder.Id);
                RememberScroll();
                _path.Push(folder);
                _crumb.Text = "OneDrive" + string.Concat(_path.Reverse().Select(f => "  ›  " + f.Name));
                _up.Enabled = true;
                _list.Items.Clear();
                foreach (var i in items.Where(i => i.IsKdbx))
                {
                    var lvi = new ListViewItem(i.Name, "kdbx") { Tag = i, Font = Theme.SemiboldFont(9.75f) };
                    lvi.SubItems.Add(KeeLiveSync.Text.Ago(i.Modified));
                    lvi.SubItems.Add(KeeLiveSync.Text.Size(i.Size));
                    _list.Items.Add(lvi);
                }
                _empty.Visible = _list.Items.Count == 0;
                SetBusy(false, Text_("Freigegeben von {0}", "Shared by {0}", folder.OwnerName ?? "?"));
            }
            catch (Exception ex)
            {
                Log.Error("Freigegebenen Ordner laden fehlgeschlagen", ex);
                ShowError(ex.Message);
            }
        }

        // ---------- Freigabelink ----------

        private async Task CheckLink()
        {
            string link = _linkBox.Text.Trim();
            _linkCard.Visible = false;
            SetSelected(null);
            if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(Text_("Bitte einen vollständigen Link (https://…) einfügen.", "Please paste a complete link (https://…)."));
                return;
            }
            SetBusy(true, Text_("Prüfe Link …", "Checking link …"));
            try
            {
                var item = await Graph.ResolveLink(_account.Id, link);
                ShowLinkChip(link, item);
                if (item.IsFolder)
                {
                    // Link auf einen freigegebenen Ordner: dessen Datenbanken im Reiter „Mein OneDrive“ zeigen
                    ShowPage(true);
                    _path.Clear();
                    await LoadRemoteFolder(item);
                    return;
                }
                if (!item.IsKdbx)
                {
                    ShowError(Text_("Der Link zeigt nicht auf eine KeePass-Datenbank (.kdbx).", "The link does not point to a KeePass database (.kdbx)."));
                    return;
                }
                _linkCardTitle.Text = item.Name;
                var info = new List<string>();
                if (!string.IsNullOrEmpty(item.OwnerName)) info.Add(Text_("geteilt von {0}", "shared by {0}", item.OwnerName));
                info.Add(string.IsNullOrEmpty(item.ModifiedBy)
                    ? Text_("geändert {0}", "modified {0}", KeeLiveSync.Text.Ago(item.Modified))
                    : Text_("geändert {0} von {1}", "modified {0} by {1}", KeeLiveSync.Text.Ago(item.Modified), item.ModifiedBy));
                info.Add(KeeLiveSync.Text.Size(item.Size));
                _linkCardInfo.Text = string.Join("  ·  ", info);
                _linkCard.Visible = true;
                SetSelected(item);
                SetBusy(false, Text_("Gefunden – mit „Öffnen“ verknüpfen.", "Found – press \"Open\" to link it."));
            }
            catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound || ex.Status == System.Net.HttpStatusCode.Forbidden)
            {
                ShowError(Text_("Kein Zugriff: Ist die Datei für „{0}“ freigegeben?", "No access: is the file shared with \"{0}\"?", _account.Email));
            }
            catch (Exception ex)
            {
                Log.Error("Freigabelink prüfen fehlgeschlagen", ex);
                ShowError(ex.Message);
            }
        }

        /// <summary>Geprüften Link kurz anzeigen: „Freigabelink · 1drv.ms/f/c/e09c…ZPLgm · KeePass-Synctest“</summary>
        private void ShowLinkChip(string link, DriveItem item)
        {
            string shortLink = link;
            Uri uri;
            if (Uri.TryCreate(link, UriKind.Absolute, out uri))
            {
                string path = uri.AbsolutePath.TrimEnd('/');
                if (path.Length > 22) path = path.Substring(0, 12) + "…" + path.Substring(path.Length - 6);
                shortLink = uri.Host + path;
            }
            _linkChipText.Text = Text_("Freigabelink", "Shared link") + "  ·  " + shortLink + "  ·  " + item.Name;
            _linkChip.Visible = true;
            _linkBox.Visible = false;
            _linkCheck.Text = Text_("Ändern", "Change");
        }

        private void EditLink()
        {
            _linkChip.Visible = false;
            _linkBox.Visible = true;
            _linkCheck.Text = Text_("Prüfen", "Check");
            _linkCard.Visible = false;
            SetSelected(null);
            _linkBox.Focus();
            _linkBox.SelectAll();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string text)
        {
            const int EmSetCueBanner = 0x1501;
            SendMessage(box.Handle, EmSetCueBanner, IntPtr.Zero, text);
        }

        // ---------- Öffnen ----------

        private async Task OpenSelected()
        {
            if (_selected == null) return;
            _open.Enabled = false;
            SetBusy(true, Text_("Öffne „{0}“ …", "Opening \"{0}\" …", _selected.Name));
            try
            {
                ResultLink = await _engine.PrepareCache(_account, _selected, text => SetBusy(true, text));
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Log.Error("Öffnen fehlgeschlagen", ex);
                ShowError(Text_("Öffnen fehlgeschlagen: ", "Open failed: ") + ex.Message);
                _open.Enabled = _selected != null;
            }
        }
    }
}
