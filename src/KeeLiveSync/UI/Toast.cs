using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KeeLiveSync.UI
{
    /// <summary>
    /// Dezente Einblendung unten rechts im KeePass-Fenster, ohne den Fokus zu nehmen.
    /// Ohne Aktion verschwindet sie nach einigen Sekunden; mit Aktion bleibt sie, bis man klickt oder sie schließt.
    /// </summary>
    internal sealed class Toast : Form
    {
        private readonly Timer _close;
        private readonly string _title;
        private readonly string _body;
        private readonly string _actionText;
        private readonly Color _accent;
        private Rectangle _actionRect;
        private Rectangle _closeRect;

        internal event Action ActionClicked;

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
                var cp = base.CreateParams;
                cp.ExStyle |= WsExNoActivate | WsExToolWindow;
                cp.ClassStyle |= 0x00020000; // Schatten
                return cp;
            }
        }

        private Toast(string title, string body, string actionText, Color accent)
        {
            _title = title;
            _body = body;
            _actionText = actionText;
            _accent = accent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.None;
            Width = (int)(380 * DeviceDpi / 96f);
            Font = Theme.UiFont(9.5f);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            _close = new Timer { Interval = 6000 };
            _close.Tick += (s, e) => Close();
            MouseClick += (s, e) =>
            {
                if (_closeRect.Contains(e.Location) || _actionText == null)
                {
                    Close();
                    return;
                }
                Close();
                var h = ActionClicked;
                if (h != null) h();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float k = DeviceDpi / 96f;
            using (var accent = new SolidBrush(_accent)) g.FillRectangle(accent, 0, 0, 4 * k, Height);
            using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            int icon = (int)(32 * k);
            using (var logo = Theme.Logo(icon)) g.DrawImage(logo, 16 * k, 16 * k);
            using (var f = Theme.SemiboldFont(10f))
            using (var ink = new SolidBrush(Theme.Ink))
                g.DrawString(_title, f, ink, new RectangleF(60 * k, 14 * k, Width - 90 * k, 22 * k));
            using (var muted = new SolidBrush(Theme.Muted))
                g.DrawString(_body, Font, muted, new RectangleF(60 * k, 36 * k, Width - 72 * k, BodyHeight(g, k)));
            _closeRect = new Rectangle((int)(Width - 28 * k), (int)(10 * k), (int)(18 * k), (int)(18 * k));
            using (var pen = new Pen(Theme.Muted, 1.4f * k))
            {
                float m = 5 * k;
                g.DrawLine(pen, _closeRect.Left + m, _closeRect.Top + m, _closeRect.Right - m, _closeRect.Bottom - m);
                g.DrawLine(pen, _closeRect.Right - m, _closeRect.Top + m, _closeRect.Left + m, _closeRect.Bottom - m);
            }
            if (_actionText != null)
            {
                using (var f = Theme.SemiboldFont(9.5f))
                {
                    var size = TextRenderer.MeasureText(_actionText, f);
                    _actionRect = new Rectangle((int)(60 * k), (int)(Height - 40 * k), size.Width + (int)(28 * k), (int)(28 * k));
                    using (var path = Theme.Rounded(_actionRect, 5 * k))
                    using (var b = new SolidBrush(_accent))
                        g.FillPath(b, path);
                    TextRenderer.DrawText(g, _actionText, f, _actionRect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        private float BodyHeight(Graphics g, float k)
        {
            return (float)Math.Ceiling(g.MeasureString(_body, Font, (int)(Width - 72 * k)).Height);
        }

        /// <summary>Höhe an den Text anpassen (lange Texte, mehrere Zeilen, Aktionsknopf)</summary>
        private void FitHeight()
        {
            float k = DeviceDpi / 96f;
            using (var g = CreateGraphics())
            {
                float h = 36 * k + BodyHeight(g, k) + (_actionText == null ? 14 * k : 50 * k);
                Height = Math.Max((int)(78 * k), (int)Math.Ceiling(h));
            }
        }

        /// <summary>Unten rechts im Fenster von owner einblenden</summary>
        internal static Toast Show(Form owner, string title, string body, string actionText = null, Color? accent = null, int autoCloseMs = 0)
        {
            if (owner == null || owner.IsDisposed || !owner.Visible) return null;
            var t = new Toast(title, body, actionText, accent ?? Theme.Accent);
            t.FitHeight();
            var area = owner.WindowState == FormWindowState.Minimized
                ? Screen.FromControl(owner).WorkingArea
                : owner.RectangleToScreen(owner.ClientRectangle);
            int margin = Theme.ScaleScreen(16);
            t.Location = new Point(area.Right - t.Width - margin, area.Bottom - t.Height - margin - Theme.ScaleScreen(24));
            t.Show(owner);
            if (actionText == null || autoCloseMs > 0)
            {
                if (autoCloseMs > 0) t._close.Interval = autoCloseMs;
                t._close.Start();
            }
            return t;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _close.Dispose();
            base.OnFormClosed(e);
        }
    }
}
