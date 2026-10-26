using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace Veyra
{
    internal partial class MainForm
    {
        private const int ConsoleHeader = 30;

        private readonly List<RectangleF> bgHoles = new List<RectangleF>();
        private Region bgClip;
        private Bitmap auroraA, auroraB;
        private double animT;

        private int ConsoleH
        {
            get
            {
                int h = (int)(ClientSize.Height * 0.24f);
                if (h < 148) h = 148;
                if (h > 208) h = 208;
                return h;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Stopwatch clock = Stopwatch.StartNew();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (resizing)
            {
                g.Clear(T.Bg);
                if (frame != null) g.DrawImage(frame, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                LastPaintMs = clock.Elapsed.TotalMilliseconds;
                return;
            }

            if (frame == null || frameW != ClientSize.Width || frameH != ClientSize.Height)
            {
                if (frame != null) frame.Dispose();
                frame = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                frameW = ClientSize.Width;
                frameH = ClientSize.Height;
                frameDirty = true;
            }

            if (frameDirty)
            {
                BuildFrame();
                BuildBgClip();
                frameDirty = false;
            }

            g.DrawImageUnscaled(frame, 0, 0);
            PaintAurora(g);
            PaintHover(g);
            if (dragOver) PaintDropOverlay(g);
            LastPaintMs = clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// The whole window is cached. Only hover feedback is painted live, so moving the
        /// mouse costs a blit and two thin shapes instead of a full GDI redraw.
        /// </summary>
        private void BuildFrame()
        {
            Stopwatch build = Stopwatch.StartNew();
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Bitmap bed = Backdrop.Get(ClientSize.Width, ClientSize.Height);
                if (bed != null) g.DrawImageUnscaled(bed, 0, 0);
                else g.Clear(T.Bg);

                chromeHits.Clear();
                pageHits.Clear();
                bgHoles.Clear();

                string savedHover = hover;
                hover = "";
                PaintHeader(g);

            int ch = ConsoleH;
            consoleRect = new Rectangle(0, ClientSize.Height - ch, ClientSize.Width, ch);
            int top = HeaderH + 14;
            int bottom = consoleRect.Y - 14;
            contentRect = new Rectangle(Pad, top, Math.Max(240, ClientSize.Width - Pad * 2), Math.Max(120, bottom - top));

            g.SetClip(contentRect);
            if (page == Pg.Dashboard)
            {
                PaintDashboard(g);
                maxScroll = 0;
            }
            else
            {
                int offset = scroll[(int)page];
                int contentBottom;
                if (page == Pg.Runner) contentBottom = PaintRunner(g, contentRect.X, contentRect.Y - offset, contentRect.Width);
                else if (page == Pg.Settings) contentBottom = PaintSettings(g, contentRect.X, contentRect.Y - offset, contentRect.Width);
                else contentBottom = PaintAbout(g, contentRect.X, contentRect.Y - offset, contentRect.Width);

                int height = contentBottom + offset - contentRect.Y;
                maxScroll = Math.Max(0, height - contentRect.Height + 8);
                if (scroll[(int)page] > maxScroll) scroll[(int)page] = maxScroll;
            }
            g.ResetClip();

                if (maxScroll > 0) PaintScrollbar(g);
                PaintConsole(g);
                PaintToast(g);
                hover = savedHover;
            }
            LastBuildMs = build.Elapsed.TotalMilliseconds;
        }

        private void PaintHover(Graphics g)
        {
            if (string.IsNullOrEmpty(hover)) return;
            RectangleF? found = null;
            for (int i = chromeHits.Count - 1; i >= 0; i--)
                if (chromeHits[i].Id == hover) { found = chromeHits[i].Rect; break; }
            if (!found.HasValue)
            {
                for (int i = pageHits.Count - 1; i >= 0; i--)
                    if (pageHits[i].Id == hover) { found = pageHits[i].Rect; break; }
            }
            if (!found.HasValue) return;

            RectangleF r = found.Value;
            T.FillRound(g, r, 4f, Color.FromArgb(20, 255, 255, 255));
            T.Stroke(g, r, 4f, Color.FromArgb(76, 255, 255, 255), 1f);
        }

        // ------------------------------------------------------------------ chrome
        private void PaintHeader(Graphics g)
        {
            RectangleF logo = new RectangleF(16, 14, 20, 20);
            T.FillRound(g, logo, 4f, T.Accent);
            T.TextC(g, "V", T.F(9f, true), Color.White, new RectangleF(logo.X, logo.Y - 1, logo.Width, logo.Height + 2));

            float nameW = T.MW(g, "Veyra", T.F(10.5f, true));
            T.TextV(g, "Veyra", T.F(10.5f, true), T.Text, 45, 24);

            float chipX = 45 + nameW + 8;
            RectangleF chip = new RectangleF(chipX, 16, 66, 16);
            T.FillRound(g, chip, 3f, T.PanelAlt);
            T.Stroke(g, chip, 3f, T.BorderSoft, 1f);
            T.TextC(g, "v" + Version, T.F(7f, false), T.TextSub, chip);

            string[][] items = new string[][]
            {
                new string[] { "dashboard", "Dashboard" },
                new string[] { "runner", "Package" },
                new string[] { "settings", "Settings" },
                new string[] { "about", "About" }
            };

            float x = chip.Right + 22;
            float navY = 8;
            for (int i = 0; i < items.Length; i++)
            {
                string id = "nav:" + items[i][0];
                float tw = T.MW(g, items[i][1], T.F(9f, false)) + 22;
                RectangleF tab = new RectangleF(x, navY, tw, 31);
                bool active = (int)page == i;
                bool hov = hover == id;

                if (active) T.FillRound(g, new RectangleF(tab.X + 4, tab.Y, tab.Width - 8, tab.Height), 4f, T.PanelAlt);
                else if (hov) T.FillRound(g, new RectangleF(tab.X + 4, tab.Y, tab.Width - 8, tab.Height), 4f, T.Panel);

                T.TextC(g, items[i][1], T.F(9f, active), active ? T.Text : (hov ? T.Text : T.TextSub), tab);
                if (active)
                {
                    using (SolidBrush b = new SolidBrush(T.Accent))
                        g.FillRectangle(b, tab.X + 4, HeaderH - 2, tab.Width - 8, 2);
                }
                W.Register(chromeHits, id, tab, true);
                x += tw + 2;
            }

            // right cluster: primary action, rescan, status
            float right = ClientSize.Width - 16;
            float selectW = T.MW(g, "Select APK", T.F(8.5f, true)) + 26;
            RectangleF select = new RectangleF(right - selectW, 11, selectW, 26);
            W.Button(g, chromeHits, hover, "btn:browse", select, "Select APK", 0, true);
            right = select.X - 16;

            string state = LocalVm.Running
                ? (LocalVm.UsingWhpx ? "VM running \u00b7 WHPX" : "VM running \u00b7 software")
                : (vmBusy ? "VM booting\u2026" : "VM stopped");
            Color dot = LocalVm.Running ? T.Good : (vmBusy ? T.Warn : T.TextFaint);
            float sw = T.MW(g, state, T.F(7.8f, false));
            T.TextV(g, state, T.F(7.8f, false), T.TextSub, right - sw, 24);
            W.Dot(g, right - sw - 11, 21.5f, 6, dot);
            right = right - sw - 22;

            if (sys.RamTotalMb > 0)
            {
                string hw = T.N(sys.RamTotalMb / 1024.0) + " GB \u00b7 " + sys.Cores + " cores";
                float hwW = T.MW(g, hw, T.F(7.6f, false));
                T.TextV(g, hw, T.F(7.6f, false), T.TextMuted, right - hwW, 24);
            }

            W.Divider(g, 16, HeaderH, ClientSize.Width - 16, T.BorderSoft);
        }

        private void PaintConsole(Graphics g)
        {
            Rectangle r = consoleRect;
            using (SolidBrush b = new SolidBrush(T.Terminal)) g.FillRectangle(b, r);
            using (SolidBrush b = new SolidBrush(T.Bg)) g.FillRectangle(b, r.X, r.Y, r.Width, ConsoleHeader);

            W.Label(g, "Console", 16, r.Y + 10);
            string meta = consoleScroll > 0 ? "scrolled \u00b7 " + consoleScroll + " back" : logLines.Count + " lines";
            T.TextV(g, meta, T.F(7.2f, false), consoleScroll > 0 ? T.Warn : T.TextFaint,
                16 + T.MW(g, "CONSOLE", T.F(7.3f, true)) + 10, r.Y + 16);

            float right = r.Right - 16;
            RectangleF clear = new RectangleF(right - 50, r.Y + 5, 50, 20);
            W.Button(g, chromeHits, hover, "btn:clearLog", clear, "Clear", 2, true);
            right = clear.X - 10;

            RectangleF verb = new RectangleF(right - 78, r.Y + 5, 78, 20);
            bool vhov = hover == "btn:verbose";
            if (vhov) T.FillRound(g, verb, 3f, T.Panel);
            W.Check(g, verb.X + 2, verb.Y + 4, cfg.Verbose, vhov);
            T.TextV(g, "Verbose", T.F(7.8f, false), cfg.Verbose ? T.TextSub : T.TextMuted, verb.X + 20, verb.Y + verb.Height / 2f);
            W.Register(chromeHits, "btn:verbose", verb, true);
            right = verb.X - 16;

            // process memory readout vs the 500 MB ceiling
            long ws = sys.ProcWorkingSetMb;
            double ratio = ws / 500.0;
            Color tone = T.GoodBad(ratio);
            string mem = ws + "/500 MB";
            float memW = T.MW(g, mem, T.F(7.4f, false));
            T.TextV(g, mem, T.F(7.4f, false), T.TextMuted, right - memW, r.Y + 15);
            RectangleF bar = new RectangleF(right - memW - 54, r.Y + 13.5f, 46, 3);
            W.Bar(g, bar, ratio, tone);

            W.Divider(g, 0, r.Y, r.Width, T.Border);

            // transcript
            List<LogLine> shown = new List<LogLine>();
            for (int i = 0; i < logLines.Count; i++)
                if (cfg.Verbose || logLines[i].Kind != 4) shown.Add(logLines[i]);

            int lineH = 14;
            int top = r.Y + ConsoleHeader + 7;
            int rows = Math.Max(1, (r.Bottom - top - 5) / lineH);
            int maxBack = Math.Max(0, shown.Count - rows);
            if (consoleScroll > maxBack) consoleScroll = maxBack;
            int end = shown.Count - consoleScroll;
            int start = Math.Max(0, end - rows);

            if (shown.Count == 0)
            {
                T.Write(g, "No output yet. Inspect a package to see the engine transcript here.", T.Mono(8f), T.TextFaint, 16, top);
                return;
            }

            for (int i = start; i < end; i++)
            {
                LogLine l = shown[i];
                float ly = top + (i - start) * lineH;
                T.Write(g, l.At.ToString("HH:mm:ss"), T.Mono(7.4f), T.TextFaint, 16, ly);
                T.WriteClip(g, l.Text, T.Mono(8f), l.Tone,
                    new RectangleF(66, ly - 1, r.Width - 82, lineH));
            }
        }

        private void PaintToast(Graphics g)
        {
            if (string.IsNullOrEmpty(toast) || toastUntil == DateTime.MinValue) return;
            float w = T.MW(g, toast, T.F(8.4f, false)) + 24;
            RectangleF r = new RectangleF(ClientSize.Width - 16 - w, consoleRect.Y - 12 - 28, w, 28);
            T.FillRound(g, r, 4f, T.PanelAlt);
            T.Stroke(g, r, 4f, T.Border, 1f);
            using (SolidBrush b = new SolidBrush(T.Accent)) g.FillRectangle(b, r.X, r.Y + 1, 2, r.Height - 2);
            T.TextV(g, toast, T.F(8.4f, false), T.Text, r.X + 12, r.Y + r.Height / 2f);
        }

        private void PaintDropOverlay(Graphics g)
        {
            Rectangle r = new Rectangle(6, 6, ClientSize.Width - 12, ClientSize.Height - 12);
            using (SolidBrush b = new SolidBrush(T.Alpha(T.Accent, 26))) g.FillRectangle(b, r);
            using (Pen p = new Pen(T.Accent, 1f)) g.DrawRectangle(p, r);
            string msg = "Release to inspect the package";
            float w = T.MW(g, msg, T.F(10f, true)) + 40;
            RectangleF box = new RectangleF((ClientSize.Width - w) / 2, ClientSize.Height / 2 - 22, w, 44);
            T.FillRound(g, box, 4f, T.PanelAlt);
            T.Stroke(g, box, 4f, T.Border, 1f);
            T.TextC(g, msg, T.F(10f, true), T.Text, box);
        }

        private void PaintScrollbar(Graphics g)
        {
            int trackH = contentRect.Height;
            RectangleF track = new RectangleF(contentRect.Right - 2, contentRect.Y, 3, trackH);
            T.FillRound(g, track, 1.5f, T.Panel);
            double visible = (double)contentRect.Height / (contentRect.Height + maxScroll);
            float thumbH = Math.Max(24f, (float)(trackH * visible));
            float progress = maxScroll <= 0 ? 0 : (float)scroll[(int)page] / maxScroll;
            float thumbY = contentRect.Y + progress * (trackH - thumbH);
            T.FillRound(g, new RectangleF(track.X, thumbY, 3, thumbH), 1.5f, T.Border);
        }

        // ------------------------------------------------------------------ dashboard
        private void PaintDashboard(Graphics g)
        {
            float x = contentRect.X, w = contentRect.Width;
            float y = contentRect.Y;

            T.Write(g, "System", T.F(12.5f, true), T.Text, x, y);
            string sub = sys.ScanMs > 0 ? "Scanned in " + sys.ScanMs + " ms \u00b7 all values are read from this PC" : "Reading this PC\u2026";
            T.Write(g, sub, T.F(8f, false), T.TextMuted, x, y + 21);

            RectangleF rescan = new RectangleF(x + w - 78, y - 3, 78, 26);
            W.Button(g, pageHits, hover, "btn:rescan", rescan, "Re-scan", 1, true);

            y += 38;

            int gap = 10;
            float cw = (w - gap * 3) / 4f;
            float cardsH = 84;
            MetricCard(g, new RectangleF(x, y, cw, cardsH), "Processor",
                ShortCpu(), sys.Cores + "C / " + sys.Threads + "T \u00b7 " + (sys.Mhz > 0 ? (sys.Mhz / 1000.0).ToString("0.00") + " GHz" : "clock unknown"),
                sys.CpuLoad / 100.0, T.TextSub);
            double ramUsed = sys.RamTotalMb > 0 ? (double)(sys.RamTotalMb - sys.RamFreeMb) / sys.RamTotalMb : 0;
            MetricCard(g, new RectangleF(x + (cw + gap), y, cw, cardsH), "Memory",
                T.N(sys.RamFreeMb / 1024.0) + " GB free",
                sys.RamTotalMb + " MB installed \u00b7 " + (ramUsed * 100).ToString("0") + "% used",
                ramUsed, T.TextSub);
            MetricCard(g, new RectangleF(x + (cw + gap) * 2, y, cw, cardsH), "Graphics",
                ShortGpu(), "driver " + (string.IsNullOrEmpty(sys.GpuDriver) ? "unknown" : sys.GpuDriver),
                -1, T.TextSub);
            double diskUsed = sys.DiskTotalGb > 0 ? 1.0 - (double)sys.DiskFreeGb / sys.DiskTotalGb : 0;
            MetricCard(g, new RectangleF(x + (cw + gap) * 3, y, cw, cardsH), "Storage",
                sys.DiskFreeGb + " GB free", "of " + sys.DiskTotalGb + " GB on " + sys.DiskLetter,
                diskUsed, T.TextSub);

            y += cardsH + 10;

            float sideW = 292;
            float rowH = contentRect.Bottom - y;
            if (rowH < 200) rowH = 200;
            float leftW = w - sideW - 12;

            PaintPackage(g, new RectangleF(x, y, leftW, rowH));

            float rx = x + leftW + 12;
            float netH = 72;
            float runtimeH = rowH - netH - 10;
            if (runtimeH < 178) runtimeH = 178;
            PaintRuntime(g, new RectangleF(rx, y, sideW, runtimeH));
            PaintNetwork(g, new RectangleF(rx, y + runtimeH + 10, sideW, netH));
        }

        private string ShortCpu()
        {
            string n = sys.CpuName;
            for (int i = 0; i < 3; i++)
            {
                n = n.Replace("Intel(R)", "").Replace("Core(TM)", "").Replace("CPU", "").Replace("@", "");
                n = n.Replace("AMD", "").Replace("  ", " ").Trim();
            }
            return T.Trunc(n, 30);
        }

        private string ShortGpu()
        {
            string n = sys.GpuName.Replace("Intel(R)", "").Replace("  ", " ").Trim();
            return T.Trunc(n, 30);
        }

        private void MetricCard(Graphics g, RectangleF r, string label, string value, string sub, double ratio, Color tone)
        {
            Panel(g, r);
            W.Label(g, label, r.X + 12, r.Y + 9);
            T.WriteClip(g, value, T.F(11f, true), T.Text, new RectangleF(r.X + 12, r.Y + 22, r.Width - 24, 20));
            T.WriteClip(g, sub, T.F(7.4f, false), T.TextMuted, new RectangleF(r.X + 12, r.Y + 46, r.Width - 24, 14));
            if (ratio >= 0)
            {
                Color barTone = ratio > 0.88 ? T.Bad : (ratio > 0.7 ? T.Warn : tone);
                W.Bar(g, new RectangleF(r.X + 12, r.Bottom - 14, r.Width - 24, 3), ratio, barTone);
            }
        }

        // ------------------------------------------------------------------ package
        private void PaintPackage(Graphics g, RectangleF r)
        {
            Panel(g, r);
            W.Label(g, "Package", r.X + 14, r.Y + 11);
            string state = busy ? "INSPECTING\u2026" : (apk == null ? "NO PACKAGE LOADED" : "LOCAL \u00b7 NOT UPLOADED");
            T.TextR(g, state, T.F(7.2f, false), busy ? T.Warn : T.TextMuted, r.Right - 14, r.Y + 17);
            W.Divider(g, r.X, r.Y + 28, r.Right, T.BorderSoft);

            if (apk == null)
            {
                RectangleF drop = new RectangleF(r.X + 14, r.Y + 38, r.Width - 28, r.Height - 52);
                bool hot = hover == "btn:drop" || dragOver;
                T.FillRound(g, drop, T.Radius, T.BgDeep);
                using (Pen p = new Pen(hot ? T.Accent : T.Border, 1f))
                {
                    p.DashStyle = DashStyle.Dash;
                    p.DashPattern = new float[] { 4f, 3f };
                    using (GraphicsPath path = T.Round(new RectangleF(drop.X + 0.5f, drop.Y + 0.5f, drop.Width - 1, drop.Height - 1), T.Radius))
                        g.DrawPath(p, path);
                }

                float cy = drop.Y + drop.Height / 2f;
                W.Glyph(g, new RectangleF(drop.X + drop.Width / 2f - 11, cy - 48, 22, 22), 4, hot ? T.Accent : T.TextSub);
                T.TextC(g, "Drop an APK here", T.F(11f, true), T.Text, new RectangleF(drop.X, cy - 18, drop.Width, 22));
                T.TextC(g, "or browse this PC \u2014 files never leave the machine", T.F(8f, false), T.TextMuted,
                    new RectangleF(drop.X, cy + 4, drop.Width, 18));
                float bw = 104;
                RectangleF browse = new RectangleF(drop.X + (drop.Width - bw) / 2f, cy + 28, bw, 28);
                W.Button(g, pageHits, hover, "btn:browse", browse, "Select APK", 0, true);
                W.Register(pageHits, "btn:drop", drop, true);
                return;
            }

            ApkInfo a = apk;
            float x = r.X + 14, w = r.Width - 28;
            float y = r.Y + 38;
            T.WriteClip(g, a.DisplayName, T.F(12f, true), T.Text, new RectangleF(x, y, w, 20));
            y += 22;
            T.WriteClip(g, a.PackageName, T.Mono(7.8f), T.TextMuted, new RectangleF(x, y, w, 15));

            y += 22;
            float px = x;
            int demand = a.Verdict == null ? 50 : a.Verdict.Demand;
            Color verdictTone = demand <= 40 ? T.Good : (demand <= 65 ? T.Warn : T.Bad);
            px = Pill(g, px, y, (demand <= 40 ? "light" : (demand <= 65 ? "demanding" : "heavy")) + " \u00b7 " + demand + "/100", verdictTone);
            px = Pill(g, px, y, "minSdk " + a.MinSdk, T.TextMuted);
            px = Pill(g, px, y, T.Bytes(a.SizeBytes), T.TextMuted);
            Pill(g, px, y, a.Abis.Count == 0 ? "pure bytecode" : string.Join(" + ", a.Abis.ToArray()), a.Abis.Count == 0 ? T.Good : T.Warn);

            float gridY = y + 26;
            float actionsY = r.Bottom - 42;
            float colW = w / 2f;
            int rows = (int)Math.Max(2, Math.Min(6, (actionsY - gridY - 6) / 21f));
            string[][] kv = new string[][]
            {
                new string[] { "Version", a.VersionName + " (" + a.VersionCode + ")" },
                new string[] { "DEX", a.DexCount + " files \u00b7 " + a.ClassCount + " classes" },
                new string[] { "Methods", a.MethodCount + " \u00b7 " + a.FieldCount + " fields" },
                new string[] { "ABIs", a.Abis.Count == 0 ? "none (pure bytecode)" : string.Join(", ", a.Abis.ToArray()) },
                new string[] { "Launcher", a.MainActivity },
                new string[] { "Signature", a.Signature }
            };
            for (int i = 0; i < rows; i++)
            {
                float cx = x + (i % 2) * colW;
                float cyy = gridY + (i / 2) * 21;
                T.Write(g, kv[i][0], T.F(7.6f, false), T.TextMuted, cx, cyy + 2);
                T.WriteClip(g, kv[i][1], i == 4 || i == 5 ? T.Mono(7.2f) : T.F(7.8f, false), T.TextSub,
                    new RectangleF(cx + 62, cyy - 1, colW - 70, 16));
            }

            W.Divider(g, r.X, actionsY - 10, r.Right, T.BorderSoft);

            float bx = x;
            RectangleF run = new RectangleF(bx, actionsY, 104, 28);
            W.Button(g, pageHits, hover, "btn:runvm", run, vmBusy ? "Starting\u2026" : "Run in VM", 0, CanRunInVm());
            bx += 112;
            W.Button(g, pageHits, hover, "btn:bridge", new RectangleF(bx, actionsY, 74, 28), "Bridge", 1, true);
            bx += 82;
            W.Button(g, pageHits, hover, "btn:export", new RectangleF(bx, actionsY, 62, 28), "Report", 2, true);
            bx += 70;
            W.Button(g, pageHits, hover, "btn:details", new RectangleF(bx, actionsY, 64, 28), "Details", 2, true);

            if (r.Width > 430)
            {
                RectangleF folder = new RectangleF(r.Right - 14 - 30, actionsY, 30, 28);
                T.FillRound(g, folder, 4f, hover == "btn:openFolder" ? T.Hover : T.PanelAlt);
                T.Stroke(g, folder, 4f, T.BorderSoft, 1f);
                W.Glyph(g, new RectangleF(folder.X + 7, folder.Y + 6, 16, 16), 9, hover == "btn:openFolder" ? T.Text : T.TextSub);
                W.Register(pageHits, "btn:openFolder", folder, true);
            }
        }

        private float Pill(Graphics g, float x, float y, string text, Color tone)
        {
            Font f = T.F(7.2f, false);
            float w = T.MW(g, text, f) + 16;
            RectangleF r = new RectangleF(x, y, w, 18);
            T.FillRound(g, r, 3f, T.Alpha(tone, 22));
            T.Stroke(g, r, 3f, T.Alpha(tone, 70), 1f);
            T.TextC(g, text, f, T.Mix(tone, Color.White, 0.3f), r);
            return r.Right + 6;
        }

        // ------------------------------------------------------------------ runtime
        private string AccelLabel()
        {
            if (!LocalVm.ComponentsPresent()) return "not installed";
            if (!LocalVm.WhpxAvailable()) return "software";
            if (!LocalVm.AccelKnown) return "checking\u2026";
            if (LocalVm.ProbeState != 1) return "software";
            return cfg.VmForceTcg ? "WHPX (software pinned)" : "WHPX";
        }

        private void PaintRuntime(Graphics g, RectangleF r)
        {
            Panel(g, r);
            W.Label(g, "Runtime", r.X + 14, r.Y + 11);

            bool ready = LocalVm.ComponentsPresent();
            string status = !ready ? "NOT INSTALLED" : (LocalVm.Running ? "RUNNING" : (vmBusy ? "STARTING" : "IDLE"));
            Color tone = !ready ? T.Warn : (LocalVm.Running ? T.Good : (vmBusy ? T.Warn : T.TextMuted));
            float sw = T.MW(g, status, T.F(7.2f, false));
            T.TextR(g, status, T.F(7.2f, false), tone, r.Right - 14, r.Y + 17);
            W.Dot(g, r.Right - 14 - sw - 9, r.Y + 13.5f, 5, tone);

            // Acceleration is the one property worth a glance: it decides whether the boot is usable.
            if (ready)
            {
                string accel = AccelLabel();
                float tagX = r.X + 14 + T.MW(g, "RUNTIME", T.F(7.3f, true)) + 10;
                T.TextV(g, accel, T.F(7.2f, false),
                    accel.IndexOf("WHPX", StringComparison.Ordinal) >= 0 ? T.TextSub : T.Warn, tagX, r.Y + 17);
            }
            W.Divider(g, r.X, r.Y + 28, r.Right, T.BorderSoft);

            float x = r.X + 14, w = r.Width - 28;
            float y = r.Y + 36;

            Row(g, x, ref y, w, "Engine", "QEMU 11.1 \u00b7 x86_64");
            Row(g, x, ref y, w, "Image", "Android-x86 9.0 (bundled)");

            y += 4;
            T.Write(g, "Storage", T.F(7.8f, false), T.TextMuted, x, y + 2);
            RectangleF open = new RectangleF(r.Right - 14 - 42, y - 3, 42, 20);
            W.Button(g, pageHits, hover, "btn:openRuntime", open, "Open", 2, true);
            WritePath(g, LocalVm.Root(), T.Mono(7f), T.TextMuted, new RectangleF(x, y + 17, w - 46, 14));
            y += 37;

            T.Write(g, "Memory", T.F(7.8f, false), T.TextMuted, x, y + 2);
            int mi = cfg.VmRamMb <= 1536 ? 0 : (cfg.VmRamMb <= 2048 ? 1 : (cfg.VmRamMb <= 2560 ? 2 : 3));
            W.Segmented(g, pageHits, hover, "seg:vmram", new RectangleF(x, y + 17, w, 24),
                new string[] { "1.5 GB", "2 GB", "2.5 GB", "3 GB" }, mi);
            y += 49;

            // When the window is tall enough, use the spare room for the runner's own budget.
            if (r.Bottom - y > 96)
            {
                W.Divider(g, r.X + 14, y, r.Right - 14, T.BorderSoft);
                y += 16;
                long ws = sys.ProcWorkingSetMb;
                double ratio = ws / 500.0;
                Color guard = T.GoodBad(ratio);
                T.Write(g, "Runner memory", T.F(7.8f, false), T.TextMuted, x, y);
                T.Write(g, ws + " MB", T.F(12f, true), T.Text, x, y + 14);
                T.TextR(g, "/ 500 MB", T.F(7.2f, false), T.TextMuted, r.Right - 14, y + 23);
                W.Bar(g, new RectangleF(x, y + 36, w, 3), ratio, guard);
                T.WriteClip(g, "peak " + Math.Max(ramPeak, ws) + " MB \u00b7 guard active", T.F(7.1f, false), T.TextMuted,
                    new RectangleF(x, y + 43, w, 14));
                y += 60;
            }

            if (!ready)
            {
                T.WriteClip(g, "Run VeyraSetup.exe to add the Android runtime.", T.F(7.4f, false), T.Warn,
                    new RectangleF(x, y, w, 16));
            }
            else if (LocalVm.Running)
            {
                W.Button(g, pageHits, hover, "btn:stopvm", new RectangleF(x, y, w, 26), "Stop VM", 3, true);
            }
            else if (y + 24 < r.Bottom - 10)
            {
                T.WriteClip(g, "Select a package and press Run in VM.", T.F(7.4f, false), T.TextMuted,
                    new RectangleF(x, y + 4, w, 16));
            }
        }

        private void WritePath(Graphics g, string path, Font f, Color c, RectangleF box)
        {
            string p = path ?? "";
            bool cut = false;
            while (p.Length > 3 && T.MW(g, "\u2026\\" + p, f) > box.Width)
            {
                int slash = p.IndexOf('\\');
                p = slash >= 0 ? p.Substring(slash + 1) : p.Substring(1);
                cut = true;
            }
            T.WriteClip(g, (cut ? "\u2026\\" : "") + p, f, c, box);
        }

        private void Row(Graphics g, float x, ref float y, float w, string label, string value)
        {
            T.Write(g, label, T.F(7.8f, false), T.TextMuted, x, y + 2);
            T.WriteClip(g, value, T.F(7.8f, false), T.TextSub, new RectangleF(x + 84, y, w - 84, 16));
            y += 18;
        }

        private void PaintNetwork(Graphics g, RectangleF r)
        {
            Panel(g, r);
            W.Label(g, "Network", r.X + 14, r.Y + 9);
            W.Button(g, pageHits, hover, "btn:net", new RectangleF(r.Right - 14 - 46, r.Y + 5, 46, 20),
                netBusy ? "\u2026" : "Test", 2, !netBusy);

            string value = net == null ? (netBusy ? "Measuring\u2026" : "\u2014") :
                T.N(net.DownMbps) + " Mbps down";
            string detail = net == null
                ? (netBusy ? netStatus : "Run a probe to measure the downlink")
                : (net.LatencyMs + " ms \u00b7 " + net.Adapter);
            T.WriteClip(g, value, T.F(10f, true), T.Text, new RectangleF(r.X + 14, r.Y + 27, r.Width - 28, 18));
            T.WriteClip(g, detail, T.F(7.2f, false), T.TextMuted, new RectangleF(r.X + 14, r.Y + 48, r.Width - 28, 14));
        }

        // ------------------------------------------------------------------ runner
        private int PaintRunner(Graphics g, int x, int y, int w)
        {
            if (apk == null)
            {
                T.Write(g, "Package", T.F(12.5f, true), T.Text, x, y);
                T.Write(g, "No package loaded. Drop an APK on the window or use Select APK.", T.F(8f, false), T.TextMuted, x, y + 21);

                RectangleF empty = new RectangleF(x, y + 48, w, 180);
                Panel(g, empty);
                W.Glyph(g, new RectangleF(x + 20, y + 66, 18, 18), 1, T.TextMuted);
                T.Write(g, "Nothing inspected yet", T.F(10f, true), T.Text, x + 48, y + 65);
                T.Write(g, "PC-side audit: manifest, resources, DEX, ABIs, signature and engine fingerprint.", T.F(8f, false), T.TextMuted, x + 48, y + 84);
                W.Button(g, pageHits, hover, "btn:browse", new RectangleF(x + 48, y + 110, 104, 28), "Select APK", 0, true);
                return y + 248;
            }

            ApkInfo a = apk;
            T.WriteClip(g, a.DisplayName, T.F(13f, true), T.Text, new RectangleF(x, y, w - 360, 22));
            T.WriteClip(g, a.PackageName, T.Mono(7.8f), T.TextMuted, new RectangleF(x, y + 22, w - 360, 15));

            float bx = x + w;
            bx -= 104;
            W.Button(g, pageHits, hover, "btn:runvm", new RectangleF(bx, y - 2, 104, 28), vmBusy ? "Starting\u2026" : "Run in VM", 0, CanRunInVm());
            bx -= 70;
            W.Button(g, pageHits, hover, "btn:export", new RectangleF(bx, y - 2, 62, 28), "Report", 1, true);
            bx -= 66;
            W.Button(g, pageHits, hover, "btn:openFolder", new RectangleF(bx, y - 2, 58, 28), "Folder", 2, true);

            y += 44;
            int sideW = 300;
            int leftW = w - sideW - 12;
            int rightX = x + leftW + 12;

            // identity
            int ly = y;
            RectangleF id = new RectangleF(x, ly, leftW, 160);
            Panel(g, id);
            PanelHead(g, id, "Identity");
            float ry = id.Y + 33;
            Row(g, id.X + 14, ref ry, id.Width - 28, "Version", a.VersionName + " (" + a.VersionCode + ")");
            Row(g, id.X + 14, ref ry, id.Width - 28, "Size", T.Bytes(a.SizeBytes) + " \u00b7 " + a.EntryCount + " entries");
            Row(g, id.X + 14, ref ry, id.Width - 28, "SDKs", "min " + a.MinSdk + " \u00b7 target " + a.TargetSdk + " \u00b7 compile " + a.CompileSdk);
            Row(g, id.X + 14, ref ry, id.Width - 28, "Launcher", a.MainActivity);
            Row(g, id.X + 14, ref ry, id.Width - 28, "Signature", a.Signature);
            Row(g, id.X + 14, ref ry, id.Width - 28, "SHA-256", a.Fingerprint);
            Row(g, id.X + 14, ref ry, id.Width - 28, "Debuggable", a.Debuggable ? "yes" : "no");
            ly += 168;

            // code
            RectangleF code = new RectangleF(x, ly, leftW, 160);
            Panel(g, code);
            PanelHead(g, code, "Code");
            ry = code.Y + 33;
            Row(g, code.X + 14, ref ry, code.Width - 28, "DEX files", a.DexCount + " \u00b7 " + string.Join(", ", a.DexFiles.ToArray()));
            Row(g, code.X + 14, ref ry, code.Width - 28, "Classes", a.ClassCount.ToString());
            Row(g, code.X + 14, ref ry, code.Width - 28, "Methods", a.MethodCount.ToString());
            Row(g, code.X + 14, ref ry, code.Width - 28, "Fields", a.FieldCount.ToString());
            Row(g, code.X + 14, ref ry, code.Width - 28, "Strings", a.StringCount.ToString());
            Row(g, code.X + 14, ref ry, code.Width - 28, "Native", a.Abis.Count == 0 ? "none \u2014 pure bytecode" : string.Join(", ", a.Abis.ToArray()));
            Row(g, code.X + 14, ref ry, code.Width - 28, "Engines", a.Frameworks.Count == 0 ? "none detected" : string.Join(", ", a.Frameworks.ToArray()));
            ly += 168;

            // permissions
            int permH = Math.Max(64, contentRect.Bottom - ly);
            RectangleF perm = new RectangleF(x, ly, leftW, permH);
            Panel(g, perm);
            PanelHead(g, perm, "Permissions \u00b7 " + a.Permissions.Count);
            string joined = a.Permissions.Count == 0 ? "none declared" : string.Join("   \u00b7   ", a.Permissions.ToArray());
            T.TextWrap(g, joined, T.Mono(7.4f), T.TextSub, new RectangleF(perm.X + 14, perm.Y + 36, perm.Width - 28, permH - 44), 6);
            ly += permH + 12;

            // verdict
            float ryy = y;
            float hostH = 146;
            float verdictH = contentRect.Bottom - ryy - 8 - hostH;
            if (verdictH > 320) verdictH = 320;
            if (verdictH < 200) verdictH = 200;
            RectangleF verdict = new RectangleF(rightX, ryy, sideW, verdictH);
            Panel(g, verdict);
            PanelHead(g, verdict, "Verdict");
            int demand = a.Verdict == null ? 50 : a.Verdict.Demand;
            Color tone = demand <= 40 ? T.Good : (demand <= 65 ? T.Warn : T.Bad);
            T.Write(g, demand.ToString(), T.F(26f, true), T.Text, verdict.X + 14, verdict.Y + 38);
            float dw = T.MW(g, demand.ToString(), T.F(26f, true));
            T.Write(g, "/100", T.F(9f, false), T.TextMuted, verdict.X + 18 + dw, verdict.Y + 56);
            T.TextR(g, "runtime demand", T.F(7.2f, false), T.TextMuted, verdict.Right - 14, verdict.Y + 55);
            W.Bar(g, new RectangleF(verdict.X + 14, verdict.Y + 82, verdict.Width - 28, 4), demand / 100.0, tone);

            float vy = verdict.Y + 96;
            T.WriteClip(g, a.Verdict == null ? "No verdict" : a.Verdict.Headline, T.F(9.4f, true), T.Text,
                new RectangleF(verdict.X + 14, vy - 1, verdict.Width - 28, 18));
            vy += 20;
            T.TextWrap(g, a.Verdict == null ? "" : a.Verdict.Detail, T.F(7.8f, false), T.TextSub,
                new RectangleF(verdict.X + 14, vy, verdict.Width - 28, 48), 3);
            vy += 54;

            if (a.Verdict != null)
            {
                for (int i = 0; i < a.Verdict.Reasons.Count && i < 4; i++)
                {
                    if (vy > verdict.Bottom - 26) break;
                    using (SolidBrush b = new SolidBrush(T.TextFaint))
                        g.FillRectangle(b, verdict.X + 15, vy + 6, 3, 3);
                    T.WriteClip(g, a.Verdict.Reasons[i], T.F(7.6f, false), T.TextMuted,
                        new RectangleF(verdict.X + 24, vy, verdict.Width - 40, 15));
                    vy += 18;
                }
            }
            if (vy < verdict.Bottom - 30)
            {
                W.Divider(g, verdict.X, verdict.Bottom - 30, verdict.Right, T.BorderSoft);
                T.Write(g, "local Android VM \u00b7 x86 bytecode", T.F(7.2f, false), T.TextMuted, verdict.X + 14, verdict.Bottom - 21);
            }
            ryy += verdictH + 8;

            RectangleF host = new RectangleF(rightX, ryy, sideW, hostH);
            Panel(g, host);
            PanelHead(g, host, "Host fit");
            float hy = host.Y + 33;
            Row(g, host.X + 14, ref hy, host.Width - 28, "Profile", prof == null ? "\u2014" : prof.Name);
            Row(g, host.X + 14, ref hy, host.Width - 28, "Render", cfg.Resolution + " \u00b7 " + cfg.Orientation);
            Row(g, host.X + 14, ref hy, host.Width - 28, "CPU", ShortCpu());
            Row(g, host.X + 14, ref hy, host.Width - 28, "RAM", sys.RamTotalMb + " MB \u00b7 " + T.N(sys.RamFreeMb / 1024.0) + " GB free");
            Row(g, host.X + 14, ref hy, host.Width - 28, "GPU", ShortGpu());
            Row(g, host.X + 14, ref hy, host.Width - 28, "OS", sys.OsName);
            ryy += hostH + 12;

            return (int)Math.Max(ly, ryy);
        }

        private void PanelHead(Graphics g, RectangleF r, string title)
        {
            W.Label(g, title, r.X + 14, r.Y + 11);
            W.Divider(g, r.X, r.Y + 28, r.Right, T.BorderSoft);
        }

        // ------------------------------------------------------------------ settings
        private int PaintSettings(Graphics g, int x, int y, int w)
        {
            T.Write(g, "Settings", T.F(12.5f, true), T.Text, x, y);
            T.Write(g, "Stored in veyra.cfg next to Veyra.exe \u00b7 changes apply immediately", T.F(8f, false), T.TextMuted, x, y + 21);
            y += 44;

            int gap = 12;
            int colW = (w - gap) / 2;
            int leftX = x, rightX = x + colW + gap;

            // DISPLAY
            RectangleF display = new RectangleF(leftX, y, colW, 172);
            Panel(g, display);
            PanelHead(g, display, "Display");
            float ry = display.Y + 38;
            SegRow(g, display.X, display.Width, ref ry, "Resolution", "seg:res",
                new string[] { "480p", "720p", "1080p" }, cfg.Resolution == "480p" ? 0 : (cfg.Resolution == "720p" ? 1 : 2));
            SegRow(g, display.X, display.Width, ref ry, "Orientation", "seg:orient",
                new string[] { "Portrait", "Landscape" }, cfg.Orientation == "Portrait" ? 0 : 1);
            SegRow(g, display.X, display.Width, ref ry, "Frame cap", "seg:fps",
                new string[] { "30 fps", "60 fps" }, cfg.FpsCap >= 60 ? 1 : 0);
            T.Write(g, "The adaptive profile can override these after a rescan.", T.F(7.2f, false), T.TextMuted, display.X + 14, ry + 2);

            // RUNTIME
            RectangleF runtime = new RectangleF(leftX, y + 182, colW, 190);
            Panel(g, runtime);
            PanelHead(g, runtime, "Runtime");
            ry = runtime.Y + 38;
            T.TextV(g, "VM memory", T.F(8.2f, false), T.Text, runtime.X + 14, ry + 12);
            int mi = cfg.VmRamMb <= 1536 ? 0 : (cfg.VmRamMb <= 2048 ? 1 : (cfg.VmRamMb <= 2560 ? 2 : 3));
            W.Segmented(g, pageHits, hover, "seg:vmram", new RectangleF(runtime.X + 14, ry + 22, runtime.Width - 28, 24),
                new string[] { "1.5 GB", "2 GB", "2.5 GB", "3 GB" }, mi);
            ry += 54;
            ToggleRow(g, runtime.X, runtime.Width, ref ry, "Adaptive profile",
                "Pick resolution and frame cap from the detected hardware", "btn:autoprofile", cfg.AutoProfile);
            ry += 4;
            ToggleRow(g, runtime.X, runtime.Width, ref ry, "Hardware acceleration",
                "Off pins QEMU to software emulation on the next start", "btn:whpx", !cfg.VmForceTcg);

            // INTERFACE
            RectangleF ui = new RectangleF(rightX, y, colW, 138);
            Panel(g, ui);
            PanelHead(g, ui, "Interface");
            ry = ui.Y + 38;
            SegRow(g, ui.X, ui.Width, ref ry, "Telemetry", "seg:tick",
                new string[] { "1 s", "2 s", "5 s" }, cfg.TelemetryMs <= 1000 ? 0 : (cfg.TelemetryMs <= 2000 ? 1 : 2));
            ry += 4;
            ToggleRow(g, ui.X, ui.Width, ref ry, "Verbose console",
                "Show engine and adb detail lines in the transcript", "btn:verbose", cfg.Verbose);

            // BRIDGE
            RectangleF bridge = new RectangleF(rightX, y + 148, colW, 224);
            Panel(g, bridge);
            PanelHead(g, bridge, "Device bridge");
            ry = bridge.Y + 38;
            T.Write(g, "ADB path", T.F(7.6f, false), T.TextMuted, bridge.X + 16, ry + 3);
            string adb = LocalVm.AdbPath(cfg.AdbPath);
            bool bundled = !string.IsNullOrEmpty(adb) && adb.StartsWith(LocalVm.Root(), StringComparison.OrdinalIgnoreCase);
            T.WriteClip(g, adb ?? "not found", T.Mono(7.2f), string.IsNullOrEmpty(adb) ? T.Bad : T.TextSub,
                new RectangleF(bridge.X + 16, ry + 18, bridge.Width - 104, 15));
            W.Button(g, pageHits, hover, "btn:locateAdb", new RectangleF(bridge.Right - 16 - 74, ry - 2, 74, 22), "Locate", 1, true);
            ry += 46;
            T.TextWrap(g, bundled
                ? "Bundled with the app. A device is optional \u2014 the local machine runs packages without one."
                : "Point this at adb.exe to install on a phone or tablet over USB.",
                T.F(7.4f, false), T.TextMuted, new RectangleF(bridge.X + 16, ry, bridge.Width - 32, 42), 3);

            long diskMb = LocalVm.DataDiskBytes / (1024 * 1024);
            T.Write(g, "Android data", T.F(7.6f, false), T.TextMuted, bridge.X + 16, ry + 48);
            T.TextR(g, diskMb > 0 ? diskMb + " MB used" : "created on first run", T.F(7.1f, false), T.TextMuted,
                bridge.Right - 16, ry + 49);
            WritePath(g, LocalVm.DataDir, T.Mono(6.9f), T.TextSub,
                new RectangleF(bridge.X + 16, ry + 62, bridge.Width - 32, 14));

            float by = bridge.Bottom - 40;
            W.Button(g, pageHits, hover, "btn:openRuntime", new RectangleF(bridge.X + 16, by, 116, 26), "Runtime folder", 1, true);
            W.Button(g, pageHits, hover, "btn:openProject", new RectangleF(bridge.X + 140, by, 108, 26), "App folder", 2, true);

            return (int)(y + 384);
        }

        private void SegRow(Graphics g, float x, float w, ref float y, string label, string id, string[] options, int selected)
        {
            T.TextV(g, label, T.F(8.2f, false), T.Text, x + 14, y + 12);
            float sw = Math.Min(w * 0.52f, 174);
            W.Segmented(g, pageHits, hover, id, new RectangleF(x + w - 14 - sw, y, sw, 24), options, selected);
            y += 34;
        }

        private void ToggleRow(Graphics g, float x, float w, ref float y, string label, string sub, string id, bool on)
        {
            RectangleF row = new RectangleF(x + 8, y, w - 16, 40);
            bool hov = hover == id;
            if (hov) T.FillRound(g, row, 3f, T.PanelAlt);
            T.Write(g, label, T.F(8.2f, false), T.Text, x + 14, y + 4);
            T.WriteClip(g, sub, T.F(7.1f, false), T.TextMuted, new RectangleF(x + 14, y + 18, w - 82, 14));
            W.Toggle(g, pageHits, hover, id, row, on);
            y += 44;
        }

        // ------------------------------------------------------------------ about
        private int PaintAbout(Graphics g, int x, int y, int w)
        {
            T.Write(g, "About", T.F(12.5f, true), T.Text, x, y);
            T.Write(g, "A small Windows host for Android packages, built for machines with little RAM.", T.F(8f, false), T.TextMuted, x, y + 21);
            y += 44;

            RectangleF head = new RectangleF(x, y, w, 104);
            Panel(g, head);
            RectangleF logo = new RectangleF(head.X + 18, head.Y + 18, 34, 34);
            T.FillRound(g, logo, 4f, T.Accent);
            T.TextC(g, "V", T.F(15f, true), Color.White, logo);
            T.Write(g, "Veyra", T.F(14f, true), T.Text, head.X + 64, head.Y + 16);
            T.Write(g, "v" + Version + "  \u00b7  native Win32 shell  \u00b7  no AI, no telemetry, no accounts",
                T.F(7.8f, false), T.TextMuted, head.X + 64, head.Y + 38);
            T.TextWrap(g, "Inspect an APK, read its manifest and bytecode, then run it in a bundled Android-x86 machine on x86 hardware. The window stays under 500 MB and never uploads a file.",
                T.F(8f, false), T.TextSub, new RectangleF(head.X + 64, head.Y + 58, w - 90, 42), 2);
            y += 116;

            int gap = 12;
            int colW = (w - gap) / 2;

            RectangleF pc = new RectangleF(x, y, colW, 186);
            Panel(g, pc);
            PanelHead(g, pc, "This PC");
            float ry = pc.Y + 36;
            Row(g, pc.X + 14, ref ry, pc.Width - 28, "CPU", sys.CpuName);
            Row(g, pc.X + 14, ref ry, pc.Width - 28, "Cores", sys.Cores + " physical \u00b7 " + sys.Threads + " logical");
            Row(g, pc.X + 14, ref ry, pc.Width - 28, "Memory", sys.RamTotalMb + " MB installed");
            Row(g, pc.X + 14, ref ry, pc.Width - 28, "GPU", sys.GpuName);
            Row(g, pc.X + 14, ref ry, pc.Width - 28, "OS", sys.OsName + " \u00b7 " + sys.Arch);
            Row(g, pc.X + 14, ref ry, pc.Width - 28, "Display", sys.ScreenW + "\u00d7" + sys.ScreenH);
            Row(g, pc.X + 14, ref ry, pc.Width - 28, "Java", sys.Java);

            RectangleF rt = new RectangleF(x + colW + gap, y, colW, 186);
            Panel(g, rt);
            PanelHead(g, rt, "Runtime");
            ry = rt.Y + 36;
            Row(g, rt.X + 14, ref ry, rt.Width - 28, "Engine", "QEMU 11.1 (x86_64 only)");
            Row(g, rt.X + 14, ref ry, rt.Width - 28, "Android", "Android-x86 9.0-r2 \u00b7 API 28");
            Row(g, rt.X + 14, ref ry, rt.Width - 28, "ADB", LocalVm.AdbPath(cfg.AdbPath) ?? "not found");
            Row(g, rt.X + 14, ref ry, rt.Width - 28, "Accel", AccelLabel());
            Row(g, rt.X + 14, ref ry, rt.Width - 28, "Data disk", LocalVm.DataDiskBytes > 0 ? (LocalVm.DataDiskBytes / (1024 * 1024)) + " MB \u00b7 persistent" : "created on first run");
            Row(g, rt.X + 14, ref ry, rt.Width - 28, "Local VM", LocalVm.Running ? "running" : "stopped");
            Row(g, rt.X + 14, ref ry, rt.Width - 28, "Guard", "500 MB working-set ceiling");
            y += 198;

            RectangleF principles = new RectangleF(x, y, w, 104);
            Panel(g, principles);
            PanelHead(g, principles, "Principles");
            string[] lines = new string[]
            {
                "Local first \u2014 the inspector and the machine run on this PC; no file is ever uploaded.",
                "Small by design \u2014 native GDI painting, no webview, no Electron, no background service.",
                "Honest limits \u2014 ARM-only native code cannot run in an x86 machine and is reported before launch."
            };
            float py = principles.Y + 38;
            for (int i = 0; i < lines.Length; i++)
            {
                T.Write(g, "\u2014", T.F(7.8f, false), T.TextFaint, principles.X + 16, py);
                T.WriteClip(g, lines[i], T.F(7.8f, false), T.TextSub, new RectangleF(principles.X + 34, py - 1, principles.Width - 50, 16));
                py += 22;
            }
            y += 116;

            W.Button(g, pageHits, hover, "btn:openProject", new RectangleF(x, y, 116, 28), "Project folder", 1, true);
            W.Button(g, pageHits, hover, "btn:openRuntime", new RectangleF(x + 124, y, 116, 28), "Runtime folder", 2, true);

            return y + 28;
        }

        // ------------------------------------------------------------------ misc
        private void Panel(Graphics g, RectangleF r)
        {
            bgHoles.Add(r);
            T.FillRound(g, r, T.Radius, T.Panel);
            T.Stroke(g, r, T.Radius, T.BorderSoft, 1f);
        }

        /// <summary>Everything except the opaque cards: the aurora is clipped to this.</summary>
        private void BuildBgClip()
        {
            if (bgClip != null) bgClip.Dispose();
            bgClip = new Region(new Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height)));
            for (int i = 0; i < bgHoles.Count; i++) bgClip.Exclude(Rectangle.Round(bgHoles[i]));
            if (consoleRect.Width > 0) bgClip.Exclude(consoleRect);
        }

        private static Bitmap MakeAurora(int size, int alpha, Color tint)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(1, 1, size - 2, size - 2);
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddEllipse(r);
                    using (PathGradientBrush b = new PathGradientBrush(p))
                    {
                        b.CenterColor = Color.FromArgb(alpha, tint);
                        b.SurroundColors = new Color[] { Color.FromArgb(0, tint) };
                        b.SetSigmaBellShape(0.62f);
                        g.FillEllipse(b, r);
                    }
                }
            }
            return bmp;
        }

        /// <summary>
        /// Two soft blue lights drifting behind the cards. They are premultiplied sprites,
        /// so a frame costs two small blits clipped to the exposed background, not a
        /// full-window gradient rebuild.
        /// </summary>
        private void PaintAurora(Graphics g)
        {
            if (bgClip == null) return;
            if (auroraA == null)
            {
                auroraA = MakeAurora(760, 34, T.Hex("5B77C6"));
                auroraB = MakeAurora(520, 26, T.Hex("3E5AA0"));
            }

            int w = ClientSize.Width, h = ClientSize.Height;
            double t = animT;
            int x1 = (int)(w * (0.15 + 0.55 * (0.5 + 0.5 * Math.Sin(t * 0.100))));
            int y1 = (int)(h * (0.02 + 0.42 * (0.5 + 0.5 * Math.Sin(t * 0.073 + 1.3))));
            int x2 = (int)(w * (0.10 + 0.62 * (0.5 + 0.5 * Math.Sin(t * 0.061 + 2.4))));
            int y2 = (int)(h * (0.35 + 0.50 * (0.5 + 0.5 * Math.Sin(t * 0.047 + 0.4))));

            g.SetClip(bgClip, CombineMode.Intersect);
            g.DrawImageUnscaled(auroraA, x1 - auroraA.Width / 2, y1 - auroraA.Height / 2);
            g.DrawImageUnscaled(auroraB, x2 - auroraB.Width / 2, y2 - auroraB.Height / 2);
            g.ResetClip();
        }
    }
}
