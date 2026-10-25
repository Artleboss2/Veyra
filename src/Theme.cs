using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Veyra
{
    /// <summary>
    /// Design tokens for a compact desktop tool: neutral surfaces, hairline borders,
    /// one muted accent, native fonts, and a slow blue-black backdrop behind it all.
    /// </summary>
    internal static class T
    {
        // ---- surface --------------------------------------------------------
        public static readonly Color Bg = Hex("18181B");
        public static readonly Color BgDeep = Hex("141416");
        public static readonly Color Panel = Hex("1F1F23");
        public static readonly Color PanelAlt = Hex("27272A");
        public static readonly Color Hover = Hex("323238");
        public static readonly Color Border = Hex("3F3F46");
        public static readonly Color BorderSoft = Hex("2D2D30");
        public static readonly Color Terminal = Hex("0D0D0D");

        // ---- text -----------------------------------------------------------
        public static readonly Color Text = Hex("E4E4E7");
        public static readonly Color TextSub = Hex("A1A1AA");
        public static readonly Color TextMuted = Hex("71717A");
        public static readonly Color TextFaint = Hex("52525B");

        // ---- accent + status -------------------------------------------------
        public static readonly Color Accent = Hex("5865F2");
        public static readonly Color AccentHover = Hex("6B76F5");
        public static readonly Color Good = Hex("3FB950");
        public static readonly Color Warn = Hex("D29922");
        public static readonly Color Bad = Hex("F85149");

        // Legacy names kept so the installer and older call sites still compile.
        public static readonly Color BgTop = Bg;
        public static readonly Color BgMid = Bg;
        public static readonly Color BgBottom = BgDeep;
        public static readonly Color Violet = Accent;
        public static readonly Color VioletSoft = AccentHover;
        public static readonly Color Magenta = Accent;
        public static readonly Color Pink = AccentHover;
        public static readonly Color Aqua = Good;
        public static readonly Color GlassEdge = Border;
        public static readonly Color GlassTop = Panel;
        public static readonly Color GlassBottom = Panel;

        // Diagnostic switches used by --bench to isolate paint costs (inert in normal runs).
        public static readonly bool NoText = Environment.GetEnvironmentVariable("VEYRA_NO_TEXT") == "1";
        public static readonly bool NoGlass = Environment.GetEnvironmentVariable("VEYRA_NO_GLASS") == "1";
        public static readonly bool NoBackdrop = Environment.GetEnvironmentVariable("VEYRA_NO_BACKDROP") == "1";

        public const float Radius = 8f;
        public const string Version = "0.1.0-alpha";

        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();
        private static readonly Dictionary<string, Size> MeasureCache = new Dictionary<string, Size>();
        private static readonly SolidBrush TextBrush = new SolidBrush(Color.Black);
        private static readonly StringFormat Fmt = new StringFormat(StringFormat.GenericTypographic);
        private static readonly StringFormat FmtClip = new StringFormat(StringFormat.GenericTypographic);

        static T()
        {
            Fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.LineLimit;
            FmtClip.FormatFlags |= StringFormatFlags.NoWrap;
            FmtClip.Trimming = StringTrimming.EllipsisCharacter;
        }

        private static Size Measure(Graphics g, string s, Font f)
        {
            string key = f.Name + ((int)(f.SizeInPoints * 10f)).ToString() + (f.Bold ? "b" : "n") + "|" + s;
            Size sz;
            if (MeasureCache.TryGetValue(key, out sz)) return sz;
            SizeF m = g.MeasureString(s, f, new PointF(0, 0), Fmt);
            sz = new Size((int)Math.Ceiling(m.Width), (int)Math.Ceiling(m.Height));
            if (MeasureCache.Count > 800) MeasureCache.Clear();
            MeasureCache[key] = sz;
            return sz;
        }

        public static Color Hex(string s)
        {
            if (s.StartsWith("#")) s = s.Substring(1);
            int v = int.Parse(s, System.Globalization.NumberStyles.HexNumber);
            if (s.Length == 6) return Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
            return Color.FromArgb((v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        public static Color Alpha(Color c, int a)
        {
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Font F(float pt, bool bold)
        {
            string key = pt.ToString("0.##") + (bold ? "b" : "n");
            Font f;
            if (FontCache.TryGetValue(key, out f)) return f;
            f = new Font("Segoe UI", pt, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            FontCache[key] = f;
            return f;
        }

        public static Font F(float pt, FontStyle style)
        {
            string key = pt.ToString("0.##") + "_" + (int)style;
            Font f;
            if (FontCache.TryGetValue(key, out f)) return f;
            f = new Font("Segoe UI", pt, style, GraphicsUnit.Point);
            FontCache[key] = f;
            return f;
        }

        public static Font Mono(float pt)
        {
            string key = "m" + pt.ToString("0.##");
            Font f;
            if (FontCache.TryGetValue(key, out f)) return f;
            f = new Font("Consolas", pt, FontStyle.Regular, GraphicsUnit.Point);
            FontCache[key] = f;
            return f;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 1f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, RectangleF r, float rad, Color c)
        {
            if (c.A == 0) return;
            using (SolidBrush b = new SolidBrush(c))
            {
                // Nearly-square corners use the cheap rectangular path; anything visibly
                // rounded gets a real path. Panels are cached, so this runs once per change.
                if (rad <= 1.5f) g.FillRectangle(b, r);
                else using (GraphicsPath p = Round(r, rad)) g.FillPath(b, p);
            }
        }

        public static void FillRound(Graphics g, RectangleF r, float rad, Brush b)
        {
            using (GraphicsPath p = Round(r, rad))
                g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            if (c.A == 0) return;
            if (rad <= 1.5f)
            {
                using (Pen pen = new Pen(c, w))
                    g.DrawRectangle(pen, (int)r.X, (int)r.Y, (int)r.Width - 1, (int)r.Height - 1);
                return;
            }
            // Half-pixel inset keeps a 1px stroke crisp instead of straddling two rows.
            RectangleF rr = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
            using (GraphicsPath p = Round(rr, rad))
            using (Pen pen = new Pen(c, w))
                g.DrawPath(pen, p);
        }

        /// <summary>Flat panel: one neutral fill, one hairline border. No shadow, no sheen.</summary>
        public static void Glass(Graphics g, RectangleF r, float rad, bool hover, bool accentGlow)
        {
            FillRound(g, r, Math.Min(rad, 6f), hover ? PanelAlt : Panel);
            Stroke(g, r, Math.Min(rad, 6f), hover ? Border : BorderSoft, 1f);
        }

        /// <summary>GDI text is several times faster than GDI+ DrawString on low-end CPUs.</summary>
        public static void Write(Graphics g, string s, Font f, Color c, float x, float y)
        {
            if (string.IsNullOrEmpty(s) || NoText) return;
            TextBrush.Color = c;
            g.DrawString(s, f, TextBrush, x, y, Fmt);
        }

        /// <summary>Draws text vertically centred on y.</summary>
        public static void TextV(Graphics g, string s, Font f, Color c, float x, float yCenter)
        {
            if (string.IsNullOrEmpty(s)) return;
            Size sz = Measure(g, s, f);
            Write(g, s, f, c, x, yCenter - sz.Height / 2f);
        }

        public static void TextC(Graphics g, string s, Font f, Color c, RectangleF box)
        {
            if (string.IsNullOrEmpty(s) || NoText) return;
            Size sz = Measure(g, s, f);
            float x = box.X + (box.Width - sz.Width) / 2f;
            float y = box.Y + (box.Height - sz.Height) / 2f;
            if (sz.Width > box.Width) WriteClip(g, s, f, c, box);
            else Write(g, s, f, c, x, y);
        }

        public static void TextR(Graphics g, string s, Font f, Color c, float right, float yCenter)
        {
            if (string.IsNullOrEmpty(s) || NoText) return;
            Size sz = Measure(g, s, f);
            Write(g, s, f, c, right - sz.Width, yCenter - sz.Height / 2f);
        }

        /// <summary>Single line clipped to a rect with an ellipsis — safe for narrow panels.</summary>
        public static void WriteClip(Graphics g, string s, Font f, Color c, RectangleF box)
        {
            if (string.IsNullOrEmpty(s) || NoText) return;
            TextBrush.Color = c;
            g.DrawString(s, f, TextBrush, box, FmtClip);
        }

        public static void TextWrap(Graphics g, string s, Font f, Color c, RectangleF box, int maxLines)
        {
            if (string.IsNullOrEmpty(s) || NoText) return;
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisWord;
                sf.FormatFlags = StringFormatFlags.LineLimit;
                using (SolidBrush b = new SolidBrush(c))
                    g.DrawString(s, f, b, box, sf);
            }
        }

        public static float MW(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            return Measure(g, s, f).Width;
        }

        /// <summary>Fixed-point formatting with a dot, independent of the Windows locale.</summary>
        public static string N(double v)
        {
            return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string Bytes(long b)
        {
            if (b <= 0) return "0 B";
            string[] u = new string[] { "B", "KB", "MB", "GB", "TB" };
            double v = b;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(v >= 100 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + u[i];
        }

        public static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "\u2026";
        }

        public static Color GoodBad(double ratio)
        {
            if (ratio < 0.55) return Good;
            if (ratio < 0.8) return Warn;
            return Bad;
        }
    }

    /// <summary>
    /// Blue-black gradient bed, rendered once per window size and reused by every frame.
    /// A slow aurora is composited over it live by the window; the gradient itself never
    /// costs anything after the first build.
    /// </summary>
    internal static class Backdrop
    {
        private static Bitmap cache;
        private static int cacheW, cacheH;

        public static Bitmap Get(int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            if (cache != null && cacheW == w && cacheH == h) return cache;

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle all = new Rectangle(0, 0, w, h);

                using (LinearGradientBrush b = new LinearGradientBrush(all, T.Hex("07090F"), T.Hex("16233C"), 68f))
                {
                    ColorBlend cb = new ColorBlend();
                    cb.Colors = new Color[]
                    {
                        T.Hex("07090F"), T.Hex("0A0F1C"), T.Hex("101A2E"),
                        T.Hex("16233C"), T.Hex("0C1322"), T.Hex("080A10")
                    };
                    cb.Positions = new float[] { 0f, 0.18f, 0.40f, 0.60f, 0.82f, 1f };
                    b.InterpolationColors = cb;
                    g.FillRectangle(b, all);
                }

                // A cool cast that fades out before the middle of the window. It covers the
                // full width on purpose: a smaller rectangle would cut a visible seam.
                using (LinearGradientBrush b = new LinearGradientBrush(all,
                    T.Alpha(T.Hex("3E5490"), 34), T.Alpha(T.Hex("3E5490"), 0), 90f))
                {
                    g.FillRectangle(b, all);
                }
            }

            if (cache != null) cache.Dispose();
            cache = bmp;
            cacheW = w;
            cacheH = h;
            return cache;
        }
    }
}
