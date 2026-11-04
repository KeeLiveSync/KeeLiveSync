using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace KeeLiveSync.UI
{
    /// <summary>Farben, Schriften und zur Laufzeit gezeichnete Symbole (scharf bei jeder DPI-Stufe)</summary>
    internal static class Theme
    {
        internal static readonly Color Accent = Color.FromArgb(11, 122, 117);
        internal static readonly Color AccentLight = Color.FromArgb(20, 160, 152);
        internal static readonly Color AccentSoft = Color.FromArgb(230, 245, 244);
        internal static readonly Color Ink = Color.FromArgb(31, 41, 51);
        internal static readonly Color Muted = Color.FromArgb(96, 108, 120);
        internal static readonly Color Line = Color.FromArgb(226, 231, 236);
        internal static readonly Color Surface = Color.FromArgb(247, 249, 251);
        internal static readonly Color Ok = Color.FromArgb(18, 128, 92);
        internal static readonly Color Warn = Color.FromArgb(196, 120, 0);
        internal static readonly Color Bad = Color.FromArgb(179, 38, 30);
        internal static readonly Color Idle = Color.FromArgb(140, 150, 160);

        internal static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Segoe UI", size, style);
        }

        internal static Font SemiboldFont(float size)
        {
            try
            {
                var f = new Font("Segoe UI Semibold", size);
                if (f.Name == "Segoe UI Semibold") return f;
            }
            catch (ArgumentException)
            {
            }
            return new Font("Segoe UI", size, FontStyle.Bold);
        }

        internal static int Scale(Control c, int px)
        {
            using (var g = c.CreateGraphics()) return (int)Math.Round(px * g.DpiX / 96f);
        }

        internal static int ScaleScreen(int px)
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return (int)Math.Round(px * g.DpiX / 96f);
        }

        private static Graphics Prepare(Bitmap bmp)
        {
            var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            return g;
        }

        private static GraphicsPath Cloud(RectangleF r)
        {
            var p = new GraphicsPath();
            float w = r.Width, h = r.Height;
            p.AddEllipse(r.X + w * 0.05f, r.Y + h * 0.42f, w * 0.40f, h * 0.48f);
            p.AddEllipse(r.X + w * 0.22f, r.Y + h * 0.14f, w * 0.48f, h * 0.62f);
            p.AddEllipse(r.X + w * 0.52f, r.Y + h * 0.34f, w * 0.43f, h * 0.56f);
            p.AddRectangle(new RectangleF(r.X + w * 0.25f, r.Y + h * 0.60f, w * 0.52f, h * 0.30f));
            return p;
        }

        /// <summary>Logo: Wolke mit kreisendem Pfeil auf Verlaufsfläche</summary>
        internal static Bitmap Logo(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Prepare(bmp))
            {
                var r = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
                float radius = size * 0.24f;
                using (var bg = Rounded(r, radius))
                using (var brush = new LinearGradientBrush(r, AccentLight, Accent, 60f))
                    g.FillPath(brush, bg);
                var cr = new RectangleF(size * 0.14f, size * 0.20f, size * 0.72f, size * 0.62f);
                using (var cloud = Cloud(cr))
                using (var white = new SolidBrush(Color.White))
                {
                    var region = new Region(cloud);
                    g.FillRegion(white, region);
                }
                float pw = Math.Max(1.2f, size * 0.085f);
                var ar = new RectangleF(size * 0.37f, size * 0.42f, size * 0.28f, size * 0.28f);
                using (var pen = new Pen(Accent, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(pen, ar, 200, 250);
                    float cx = ar.X + ar.Width / 2, cy = ar.Y + ar.Height / 2, rr = ar.Width / 2;
                    double a = (200 + 250) * Math.PI / 180;
                    var tip = new PointF(cx + (float)(rr * Math.Cos(a)), cy + (float)(rr * Math.Sin(a)));
                    float hs = size * 0.09f;
                    g.DrawLine(pen, tip, new PointF(tip.X - hs, tip.Y - hs * 0.15f));
                    g.DrawLine(pen, tip, new PointF(tip.X + hs * 0.1f, tip.Y - hs));
                }
            }
            return bmp;
        }

        internal static Icon LogoIcon(int size)
        {
            using (var bmp = Logo(size)) return Icon.FromHandle(bmp.GetHicon());
        }

        internal static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        internal static Color StateColor(SyncState state, bool push)
        {
            switch (state)
            {
                case SyncState.Synced: return push ? Ok : Warn;
                case SyncState.Syncing: return AccentLight;
                case SyncState.Offline: return Idle;
                case SyncState.SignInRequired: return Warn;
                default: return Bad;
            }
        }

        /// <summary>Kleine Wolke in Statusfarbe für die Statusleiste</summary>
        internal static Bitmap StatusIcon(int size, Color color, bool slash)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Prepare(bmp))
            {
                using (var cloud = Cloud(new RectangleF(0, size * 0.1f, size, size * 0.8f)))
                using (var b = new SolidBrush(color))
                    g.FillRegion(b, new Region(cloud));
                if (slash)
                    using (var pen = new Pen(Color.White, Math.Max(1.5f, size * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(pen, size * 0.28f, size * 0.36f, size * 0.72f, size * 0.80f);
            }
            return bmp;
        }

        /// <summary>Runder Kreis mit Initialen</summary>
        internal static Bitmap Avatar(string initials, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Prepare(bmp))
            {
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, size, size), AccentLight, Accent, 60f))
                    g.FillEllipse(b, 0.5f, 0.5f, size - 1, size - 1);
                using (var f = SemiboldFont(size * 0.30f))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(initials, f, Brushes.White, new RectangleF(0, 1, size, size), sf);
            }
            return bmp;
        }

        internal static Bitmap FolderIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Prepare(bmp))
            {
                float s = size;
                using (var back = Rounded(new RectangleF(s * 0.06f, s * 0.18f, s * 0.88f, s * 0.66f), s * 0.08f))
                using (var b = new SolidBrush(Color.FromArgb(232, 170, 66)))
                    g.FillPath(b, back);
                using (var tab = Rounded(new RectangleF(s * 0.06f, s * 0.12f, s * 0.38f, s * 0.2f), s * 0.06f))
                using (var b = new SolidBrush(Color.FromArgb(232, 170, 66)))
                    g.FillPath(b, tab);
                using (var front = Rounded(new RectangleF(s * 0.06f, s * 0.30f, s * 0.88f, s * 0.56f), s * 0.08f))
                using (var b = new LinearGradientBrush(new RectangleF(0, s * 0.3f, s, s * 0.56f), Color.FromArgb(255, 205, 102), Color.FromArgb(245, 184, 74), 90f))
                    g.FillPath(b, front);
            }
            return bmp;
        }

        /// <summary>Datenbank-Datei: Blatt mit Schlüssel</summary>
        internal static Bitmap KdbxIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Prepare(bmp))
            {
                float s = size;
                using (var page = Rounded(new RectangleF(s * 0.16f, s * 0.06f, s * 0.68f, s * 0.88f), s * 0.1f))
                {
                    using (var b = new SolidBrush(AccentSoft)) g.FillPath(b, page);
                    using (var p = new Pen(Accent, Math.Max(1f, s * 0.05f))) g.DrawPath(p, page);
                }
                using (var pen = new Pen(Accent, Math.Max(1.2f, s * 0.08f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawEllipse(pen, s * 0.33f, s * 0.24f, s * 0.24f, s * 0.24f);
                    g.DrawLine(pen, s * 0.45f, s * 0.48f, s * 0.45f, s * 0.78f);
                    g.DrawLine(pen, s * 0.45f, s * 0.66f, s * 0.57f, s * 0.66f);
                    g.DrawLine(pen, s * 0.45f, s * 0.76f, s * 0.55f, s * 0.76f);
                }
            }
            return bmp;
        }

        internal static Bitmap UpIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Prepare(bmp))
            using (var pen = new Pen(Muted, Math.Max(1.4f, size * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float s = size;
                g.DrawLines(pen, new[] { new PointF(s * 0.3f, s * 0.45f), new PointF(s * 0.5f, s * 0.25f), new PointF(s * 0.7f, s * 0.45f) });
                g.DrawLine(pen, s * 0.5f, s * 0.27f, s * 0.5f, s * 0.72f);
            }
            return bmp;
        }

        /// <summary>Kreispfeil für „Aktualisieren“</summary>
        internal static Bitmap RefreshIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Prepare(bmp))
            using (var pen = new Pen(Muted, Math.Max(1.4f, size * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float s = size, r = s * 0.30f, c = s * 0.5f;
                const float start = 20f, sweep = 280f;
                g.DrawArc(pen, c - r, c - r, 2 * r, 2 * r, start, sweep);
                // gefüllte Pfeilspitze am Bogenende, in Laufrichtung (im Uhrzeigersinn)
                double a = (start + sweep) * Math.PI / 180;
                float ex = c + r * (float)Math.Cos(a), ey = c + r * (float)Math.Sin(a);
                float tx = -(float)Math.Sin(a), ty = (float)Math.Cos(a);
                float h = s * 0.22f, w = s * 0.16f;
                using (var brush = new SolidBrush(Muted))
                    g.FillPolygon(brush, new[]
                    {
                        new PointF(ex + tx * h * 0.6f, ey + ty * h * 0.6f),
                        new PointF(ex - tx * h * 0.4f + ty * w, ey - ty * h * 0.4f - tx * w),
                        new PointF(ex - tx * h * 0.4f - ty * w, ey - ty * h * 0.4f + tx * w)
                    });
            }
            return bmp;
        }

        /// <summary>Flacher Hauptknopf in Akzentfarbe</summary>
        internal static void Primary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = AccentLight;
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(8, 96, 92);
            b.Font = SemiboldFont(9.75f);
            b.Cursor = Cursors.Hand;
            b.EnabledChanged += (s, e) =>
            {
                b.BackColor = b.Enabled ? Accent : Color.FromArgb(180, 205, 203);
            };
        }

        /// <summary>Flacher Nebenknopf mit Rahmen</summary>
        internal static void Secondary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 208, 216);
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = Color.White;
            b.ForeColor = Ink;
            b.FlatAppearance.MouseOverBackColor = Surface;
            b.Font = UiFont(9.75f);
            b.Cursor = Cursors.Hand;
        }
    }
}
