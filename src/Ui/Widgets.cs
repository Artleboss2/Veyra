using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Veyra
{
    internal class Hit
    {
        public string Id;
        public RectangleF Rect;
        public bool Enabled = true;

        public bool Contains(int x, int y)
        {
            return x >= Rect.X && x <= Rect.Right && y >= Rect.Y && y <= Rect.Bottom;
        }
    }

    internal class LogLine
    {
        public DateTime At = DateTime.Now;
        public string Text = "";
        public int Kind; // 0 info, 1 ok, 2 warn, 3 error, 4 detail (verbose only)

        public Color Tone
        {
            get
            {
                if (Kind == 1) return T.Good;
                if (Kind == 2) return T.Warn;
                if (Kind == 3) return T.Bad;
                if (Kind == 4) return T.TextMuted;
                return T.TextSub;
            }
        }
    }

    /// <summary>Flat, hand-rolled controls. Everything is 1px bordered and softly cornered.</summary>
    internal static class W
    {
        public static readonly float Rad = 6f;

        public static void Register(List<Hit> hits, string id, RectangleF r, bool enabled)
        {
            Hit h = new Hit();
            h.Id = id;
            h.Rect = r;
            h.Enabled = enabled;
            hits.Add(h);
        }

        public static void Label(Graphics g, string text, float x, float y)
        {
            T.Write(g, text.ToUpperInvariant(), T.F(7.3f, true), T.TextMuted, x, y);
        }

        public static void LabelRight(Graphics g, string text, float right, float y)
        {
            T.TextR(g, text.ToUpperInvariant(), T.F(7.3f, true), T.TextMuted, right, y + 6f);
        }

        /// <summary>styles: 0 primary, 1 secondary, 2 ghost, 3 danger ghost.</summary>
        public static void Button(Graphics g, List<Hit> hits, string hover, string id, RectangleF r,
            string label, int style, bool enabled)
        {
            bool hov = enabled && hover == id;
            float rad = Math.Min(Rad, r.Height / 2f);

            if (!enabled)
            {
                T.FillRound(g, r, rad, T.Panel);
                T.Stroke(g, r, rad, T.BorderSoft, 1f);
                T.TextC(g, label, T.F(8.5f, true), T.TextFaint, r);
                return;
            }

            if (style == 0)
            {
                T.FillRound(g, r, rad, hov ? T.AccentHover : T.Accent);
                T.TextC(g, label, T.F(8.5f, true), Color.White, r);
            }
            else if (style == 1)
            {
                T.FillRound(g, r, rad, hov ? T.Hover : T.PanelAlt);
                T.Stroke(g, r, rad, hov ? T.Border : T.BorderSoft, 1f);
                T.TextC(g, label, T.F(8.5f, true), T.Text, r);
            }
            else if (style == 3)
            {
                if (hov) T.FillRound(g, r, rad, T.Alpha(T.Bad, 26));
                T.Stroke(g, r, rad, T.Alpha(T.Bad, hov ? 150 : 90), 1f);
                T.TextC(g, label, T.F(8.5f, true), T.Bad, r);
            }
            else
            {
                if (hov) T.FillRound(g, r, rad, T.Hover);
                T.TextC(g, label, T.F(8.5f, false), hov ? T.Text : T.TextSub, r);
            }
            Register(hits, id, r, true);
        }

        /// <summary>Flat segmented selector: recessed track, selected segment filled.</summary>
        public static void Segmented(Graphics g, List<Hit> hits, string hover, string idPrefix,
            RectangleF r, string[] options, int selected)
        {
            T.FillRound(g, r, Rad, T.BgDeep);
            T.Stroke(g, r, Rad, T.BorderSoft, 1f);
            float pad = 2f;
            float each = (r.Width - pad * 2) / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                RectangleF o = new RectangleF(r.X + pad + i * each, r.Y + pad, each - 1, r.Height - pad * 2);
                string id = idPrefix + ":" + i;
                bool hov = hover == id;
                if (i == selected)
                {
                    T.FillRound(g, o, 3f, T.Border);
                    T.TextC(g, options[i], T.F(8.2f, true), T.Text, o);
                }
                else
                {
                    if (hov) T.FillRound(g, o, 3f, T.Hover);
                    T.TextC(g, options[i], T.F(8.2f, false), hov ? T.Text : T.TextSub, o);
                }
                Register(hits, id, o, true);
            }
        }

        public static void Toggle(Graphics g, List<Hit> hits, string hover, string id, RectangleF row, bool on)
        {
            bool hov = hover == id;
            float h = 16f;
            RectangleF track = new RectangleF(row.Right - 34, row.Y + (row.Height - h) / 2f, 34, h);
            T.FillRound(g, track, h / 2f, on ? T.Accent : T.Border);
            if (hov) T.Stroke(g, track, h / 2f, T.Alpha(Color.White, 45), 1f);
            float knob = 12f;
            float kx = on ? track.Right - knob - 2f : track.X + 2f;
            using (SolidBrush b = new SolidBrush(on ? Color.White : T.TextSub))
                g.FillEllipse(b, kx, track.Y + (track.Height - knob) / 2f, knob, knob);
            Register(hits, id, row, true);
        }

        public static void Check(Graphics g, float x, float y, bool on, bool hovered)
        {
            RectangleF box = new RectangleF(x, y, 13, 13);
            T.FillRound(g, box, 3f, on ? T.Accent : (hovered ? T.Hover : T.BgDeep));
            T.Stroke(g, box, 3f, on ? T.Accent : T.Border, 1f);
            if (!on) return;
            using (Pen p = new Pen(Color.White, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLines(p, new PointF[]
                {
                    new PointF(x + 3.2f, y + 6.8f),
                    new PointF(x + 5.6f, y + 9.2f),
                    new PointF(x + 10f, y + 4.2f)
                });
            }
        }

        /// <summary>Thin flat meter. No gradient, no rounding beyond a pixel.</summary>
        public static void Bar(Graphics g, RectangleF r, double ratio, Color c)
        {
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            T.FillRound(g, r, 1.5f, T.PanelAlt);
            if (ratio <= 0.0005) return;
            RectangleF f = new RectangleF(r.X, r.Y, (float)(r.Width * ratio), r.Height);
            if (f.Width < 2f) f.Width = 2f;
            T.FillRound(g, f, 1.5f, c);
        }

        public static void Dot(Graphics g, float x, float y, float size, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.FillEllipse(b, x, y, size, size);
        }

        public static void Divider(Graphics g, float x1, float y, float x2, Color c)
        {
            using (Pen p = new Pen(c, 1f))
                g.DrawLine(p, x1, (int)y + 0.5f, x2, (int)y + 0.5f);
        }

        /// <summary>16px line icons, drawn with a pen so they stay crisp at 100% scaling.</summary>
        public static void Glyph(Graphics g, RectangleF box, int kind, Color c)
        {
            using (Pen p = new Pen(c, 1.4f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float x = box.X, y = box.Y, w = box.Width, h = box.Height;
                switch (kind)
                {
                    case 0: // grid
                        g.DrawRectangle(p, x + 1.5f, y + 1.5f, w / 2 - 3f, h / 2 - 3f);
                        g.DrawRectangle(p, x + w / 2 + 1f, y + 1.5f, w / 2 - 3f, h / 2 - 3f);
                        g.DrawRectangle(p, x + 1.5f, y + h / 2 + 1f, w / 2 - 3f, h / 2 - 3f);
                        g.DrawRectangle(p, x + w / 2 + 1f, y + h / 2 + 1f, w / 2 - 3f, h / 2 - 3f);
                        break;
                    case 1: // package
                        g.DrawRectangle(p, x + 2, y + 4.5f, w - 4, h - 6);
                        g.DrawLine(p, x + 2, y + 8, x + w - 2, y + 8);
                        g.DrawLine(p, x + w / 2, y + 1.5f, x + w / 2, y + 8);
                        break;
                    case 2: // sliders
                        g.DrawLine(p, x + 1, y + 4, x + w - 1, y + 4);
                        g.DrawLine(p, x + 1, y + 8.5f, x + w - 1, y + 8.5f);
                        g.DrawLine(p, x + 1, y + 13, x + w - 1, y + 13);
                        using (SolidBrush b = new SolidBrush(c))
                        {
                            g.FillRectangle(b, x + w * 0.58f, y + 2.2f, 3.2f, 3.6f);
                            g.FillRectangle(b, x + w * 0.24f, y + 6.7f, 3.2f, 3.6f);
                            g.FillRectangle(b, x + w * 0.68f, y + 11.2f, 3.2f, 3.6f);
                        }
                        break;
                    case 3: // info
                        g.DrawEllipse(p, x + 1.5f, y + 1.5f, w - 3, h - 3);
                        g.DrawLine(p, x + w / 2, y + 7, x + w / 2, y + h - 4);
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillEllipse(b, x + w / 2 - 1, y + 4, 2f, 2f);
                        break;
                    case 4: // upload
                        g.DrawLine(p, x + w / 2, y + 3, x + w / 2, y + h - 4);
                        g.DrawLine(p, x + w / 2 - 4, y + 7, x + w / 2, y + 3);
                        g.DrawLine(p, x + w / 2 + 4, y + 7, x + w / 2, y + 3);
                        g.DrawLine(p, x + 3, y + h - 1, x + w - 3, y + h - 1);
                        break;
                    case 5: // cpu
                        g.DrawRectangle(p, x + 3, y + 3, w - 6, h - 6);
                        g.DrawRectangle(p, x + 6, y + 6, w - 12, h - 12);
                        for (int i = 0; i < 3; i++)
                        {
                            float yy = y + 5 + i * (h - 10) / 2f;
                            g.DrawLine(p, x, yy, x + 3, yy);
                            g.DrawLine(p, x + w - 3, yy, x + w, yy);
                        }
                        break;
                    case 6: // network
                        g.DrawArc(p, x + 1, y + 2, w - 2, h * 0.8f, 200, 140);
                        g.DrawArc(p, x + w * 0.24f, y + h * 0.36f, w * 0.52f, h * 0.5f, 200, 140);
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillEllipse(b, x + w / 2 - 1.5f, y + h - 4, 3f, 3f);
                        break;
                    case 7: // terminal
                        g.DrawRectangle(p, x + 1.5f, y + 2.5f, w - 3, h - 5);
                        g.DrawLine(p, x + 5, y + 6, x + 8, y + 8.5f);
                        g.DrawLine(p, x + 8, y + 8.5f, x + 5, y + 11);
                        g.DrawLine(p, x + 10.5f, y + 11, x + 14, y + 11);
                        break;
                    case 8: // play / launch
                        using (GraphicsPath path = new GraphicsPath())
                        {
                            path.AddPolygon(new PointF[]
                            {
                                new PointF(x + 4, y + 2),
                                new PointF(x + w - 3, y + h / 2),
                                new PointF(x + 4, y + h - 2)
                            });
                            path.CloseFigure();
                            g.DrawPath(p, path);
                        }
                        break;
                    case 9: // folder
                        g.DrawLines(p, new PointF[]
                        {
                            new PointF(x + 1.5f, y + 4),
                            new PointF(x + 1.5f, y + h - 2),
                            new PointF(x + w - 1.5f, y + h - 2),
                            new PointF(x + w - 1.5f, y + 5.5f),
                            new PointF(x + w / 2 - 1, y + 5.5f),
                            new PointF(x + w / 2 - 3, y + 3.5f),
                            new PointF(x + 4, y + 3.5f),
                            new PointF(x + 1.5f, y + 4)
                        });
                        break;
                    case 10: // refresh
                        g.DrawArc(p, x + 2, y + 2, w - 4, h - 4, 40, 280);
                        g.DrawLine(p, x + w - 3.5f, y + 2.5f, x + w - 1.5f, y + 6);
                        g.DrawLine(p, x + w - 3.5f, y + 2.5f, x + w - 7, y + 3.5f);
                        break;
                    case 11: // stop
                        g.DrawRectangle(p, x + 4, y + 4, w - 8, h - 8);
                        break;
                    case 12: // shield
                        using (GraphicsPath path = new GraphicsPath())
                        {
                            path.AddLines(new PointF[]
                            {
                                new PointF(x + w / 2, y + 1.5f),
                                new PointF(x + w - 2.5f, y + 4.5f),
                                new PointF(x + w - 3.5f, y + h * 0.6f),
                                new PointF(x + w / 2, y + h - 1.5f),
                                new PointF(x + 3.5f, y + h * 0.6f),
                                new PointF(x + 2.5f, y + 4.5f)
                            });
                            path.CloseFigure();
                            g.DrawPath(p, path);
                        }
                        break;
                }
            }
        }
    }
}
