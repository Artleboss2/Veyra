using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Veyra;

namespace VeyraSetup
{
    internal static class Program
    {
        private const string PayloadName = "Veyra.exe";
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Veyra";

        internal static string InstallDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Veyra");
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); } catch { }
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
            try { SetProcessDPIAware(); } catch { }

            string extract = Value(args, "--extract");
            if (extract != null && extract.Length > 0) return ExtractPayload(extract);

            if (Has(args, "--check"))
            {
                string outPath = Value(args, "--check-out");
                if (outPath == null || outPath.Length == 0) outPath = Path.Combine("build", "installer-check.json");
                return CheckPayload(outPath);
            }

            string shot = Value(args, "--shot");
            if (shot != null && shot.Length > 0) return RenderShot(shot);

            bool uninstall = Has(args, "--uninstall");
            Application.Run(new SetupForm(uninstall));
            return 0;
        }

        private const string FooterMagic = "VYRAZIP1";
        private static bool footerRead;
        private static long footerOffset;
        private static long footerLength;

        internal static long PayloadLength
        {
            get { ReadFooter(); return footerLength; }
        }

        private static void ReadFooter()
        {
            if (footerRead) return;
            footerRead = true;
            try
            {
                using (FileStream fs = File.OpenRead(Application.ExecutablePath))
                {
                    if (fs.Length < 24) return;
                    fs.Seek(-24, SeekOrigin.End);
                    byte[] tail = new byte[24];
                    int got = fs.Read(tail, 0, 24);
                    if (got != 24) return;
                    string magic = Encoding.ASCII.GetString(tail, 0, 8);
                    if (magic != FooterMagic) return;
                    long off = BitConverter.ToInt64(tail, 8);
                    long len = BitConverter.ToInt64(tail, 16);
                    if (off <= 0 || len <= 0 || off + len > fs.Length - 24) return;
                    footerOffset = off;
                    footerLength = len;
                }
            }
            catch { }
        }

        internal static ZipArchive OpenPayload()
        {
            ReadFooter();
            if (footerLength <= 0) throw new InvalidOperationException("This installer has no runtime payload appended.");
            FileStream fs = new FileStream(Application.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RangeStream range = new RangeStream(fs, footerOffset, footerLength);
            return new ZipArchive(range, ZipArchiveMode.Read, false);
        }

        internal static void ExtractAll(string dir, Action<double, string> report)
        {
            string root = Path.GetFullPath(dir);
            if (!root.EndsWith("\\")) root += "\\";
            Directory.CreateDirectory(root);

            using (ZipArchive zip = OpenPayload())
            {
                long total = 0, done = 0;
                for (int i = 0; i < zip.Entries.Count; i++) total += zip.Entries[i].Length;
                if (total <= 0) total = 1;

                for (int i = 0; i < zip.Entries.Count; i++)
                {
                    ZipArchiveEntry e = zip.Entries[i];
                    string dest = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('/', '\\')));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                    if (e.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    using (Stream input = e.Open())
                    using (FileStream output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
                    {
                        byte[] buf = new byte[128 * 1024];
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            output.Write(buf, 0, n);
                            done += n;
                        }
                    }
                    if (report != null)
                        report(Math.Min(0.999, (double)done / total), "Installing " + e.FullName + "\u2026");
                }
            }
        }

        private static int RenderShot(string path)
        {
            try
            {
                using (SetupForm f = new SetupForm(false))
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-3500, -2000);
                    f.Show();
                    Application.DoEvents();
                    Thread.Sleep(120);
                    Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    f.Close();
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err", ex.ToString()); } catch { }
                return 6;
            }
        }

        private static int CheckPayload(string outPath)
        {
            try
            {
                long bytes = PayloadLength;
                int entries = 0;
                long unpacked = 0;
                using (ZipArchive zip = OpenPayload())
                {
                    entries = zip.Entries.Count;
                    for (int i = 0; i < zip.Entries.Count; i++) unpacked += zip.Entries[i].Length;
                }
                string json = "{\n  \"ok\": " + (bytes > 0 ? "true" : "false") + ",\n  \"payloadBytes\": " + bytes +
                              ",\n  \"entries\": " + entries + ",\n  \"unpackedBytes\": " + unpacked +
                              ",\n  \"installDir\": \"" + InstallDir.Replace("\\", "\\\\") + "\"\n}\n";
                string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, json, Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outPath + ".err", ex.ToString()); } catch { }
                return 2;
            }
        }

        private static int ExtractPayload(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                ExtractAll(dir, null);
                return 0;
            }
            catch { return 3; }
        }

        internal static string Value(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return null;
        }

        internal static bool Has(string[] args, string flag)
        {
            return Array.IndexOf(args, flag) >= 0;
        }

        internal static void EnableDarkTitle(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch { }
        }
    }

    /// <summary>Minimal install/remove window, custom painted with the same design tokens as Veyra.</summary>
    internal class SetupForm : Form
    {
        private readonly bool uninstallMode;
        private bool busy;
        private bool done;
        private double progress;
        private string status = "Ready.";
        private string error = "";
        private bool desktopShortcut = true;
        private string hoverId = "";

        private RectangleF btnPrimary;
        private RectangleF btnSecondary;
        private RectangleF toggleRect;

        private readonly Size design = new Size(760, 500);

        public SetupForm(bool uninstall)
        {
            uninstallMode = uninstall;
            Text = uninstall ? "Remove Veyra" : "Veyra Setup";
            BackColor = T.BgTop;
            ForeColor = T.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = design;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Program.EnableDarkTitle(this);
        }

        private string TargetExe { get { return Path.Combine(Program.InstallDir, "Veyra.exe"); } }
        private string StartMenuLnk
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Veyra.lnk");
            }
        }
        private string DesktopLnk
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Veyra.lnk");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            Bitmap bg = Backdrop.Get(ClientSize.Width, ClientSize.Height);
            if (bg != null) g.DrawImageUnscaled(bg, 0, 0);

            RectangleF card = new RectangleF(36, 34, ClientSize.Width - 72, ClientSize.Height - 68);
            T.Glass(g, card, 6f, false, false);

            T.FillRound(g, new RectangleF(64, 62, 46, 46), 4f, T.Accent);
            T.TextC(g, "V", T.F(19f, true), Color.White, new RectangleF(64, 61, 46, 48));

            T.Write(g, "Veyra", T.F(14f, true), T.Text, 124, 63);
            T.Write(g, uninstallMode ? "Remove the application from this PC" : "Android package runner for Windows",
                T.F(8.4f, false), T.TextMuted, 125, 88);
            T.Write(g, "v" + T.Version, T.F(7.4f, false), T.TextMuted, 125, 105);

            T.Write(g, uninstallMode ? "This removes Veyra, its Android runtime and its settings." :
                "Veyra, adb and a compact Android 9 x86 runtime. No admin rights, no background service.",
                T.F(9f, false), T.TextSub, 64, 136);

            // Destination
            T.Write(g, "INSTALL LOCATION", T.F(7.4f, true), T.TextMuted, 64, 178);
            T.FillRound(g, new RectangleF(64, 196, ClientSize.Width - 128, 38), 4f, T.Panel);
            T.Stroke(g, new RectangleF(64, 196, ClientSize.Width - 128, 38), 4f, T.BorderSoft, 1f);
            T.WriteClip(g, Program.InstallDir, T.F(8.2f, false), T.TextSub,
                new RectangleF(78, 206, ClientSize.Width - 156, 20));

            int payloadMb = 0;
            try { payloadMb = (int)(Program.PayloadLength / (1024 * 1024)); } catch { }
            T.Write(g, (payloadMb > 0 ? payloadMb + " MB on disk  \u00b7  " : "") + "per-user install  \u00b7  uninstall from Settings \u2192 Apps",
                T.F(7.6f, false), T.TextMuted, 65, 242);

            if (!uninstallMode)
            {
                toggleRect = new RectangleF(64, 274, ClientSize.Width - 128, 30);
                T.Write(g, "Create a desktop shortcut", T.F(9f, false), T.TextSub, 64, 280);
                RectangleF track = new RectangleF(ClientSize.Width - 98, 278, 34, 18);
                T.FillRound(g, track, 9f, desktopShortcut ? T.Accent : T.Border);
                float knob = 12f;
                float kx = desktopShortcut ? track.Right - knob - 3f : track.X + 3f;
                using (SolidBrush b = new SolidBrush(desktopShortcut ? Color.White : T.TextSub))
                    g.FillEllipse(b, kx, track.Y + (track.Height - knob) / 2f, knob, knob);
            }

            // Progress / status
            float py = 330;
            T.FillRound(g, new RectangleF(64, py, ClientSize.Width - 128, 6), 1.5f, T.PanelAlt);
            if (progress > 0)
                WBar(g, new RectangleF(64, py, ClientSize.Width - 128, 6), progress,
                    string.IsNullOrEmpty(error) ? T.Accent : T.Bad);
            T.Write(g, string.IsNullOrEmpty(error) ? status : error, T.F(8.2f, false),
                string.IsNullOrEmpty(error) ? T.TextSub : T.Bad, 65, py + 18);

            // Buttons
            float bw = 150, bh = 38;
            btnPrimary = new RectangleF(ClientSize.Width - 64 - bw, ClientSize.Height - 106, bw, bh);
            btnSecondary = new RectangleF(ClientSize.Width - 64 - bw * 2 - 12, ClientSize.Height - 106, bw, bh);

            if (!done)
            {
                DrawButton(g, btnSecondary, "Cancel", false, !busy);
                DrawButton(g, btnPrimary, uninstallMode ? "Uninstall" : "Install", true, !busy);
            }
            else
            {
                DrawButton(g, btnSecondary, "Close", false, true);
                DrawButton(g, btnPrimary, "Launch Veyra", true, !uninstallMode && File.Exists(TargetExe));
            }

            T.Write(g, "Veyra " + T.Version + "  \u00b7  no telemetry  \u00b7  Android 9 x86 runtime included",
                T.F(7.2f, false), T.TextMuted, 64, ClientSize.Height - 62);
        }

        private void WBar(Graphics g, RectangleF r, double ratio, Color tone)
        {
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            RectangleF f = new RectangleF(r.X, r.Y, Math.Max(4f, (float)(r.Width * ratio)), r.Height);
            T.FillRound(g, f, 1.5f, tone);
        }

        private void DrawButton(Graphics g, RectangleF r, string label, bool primary, bool enabled)
        {
            string id = label;
            bool hov = enabled && hoverId == id;
            if (primary)
            {
                T.FillRound(g, r, 4f, !enabled ? T.PanelAlt : (hov ? T.AccentHover : T.Accent));
                T.TextC(g, label, T.F(8.8f, true), enabled ? Color.White : T.TextFaint, r);
            }
            else
            {
                T.FillRound(g, r, 4f, hov ? T.Hover : T.PanelAlt);
                T.Stroke(g, r, 4f, T.BorderSoft, 1f);
                T.TextC(g, label, T.F(8.8f, true), enabled ? T.Text : T.TextMuted, r);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string id = HitId(e.X, e.Y);
            if (id != hoverId) { hoverId = id; Cursor = id.Length > 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        private string HitId(int x, int y)
        {
            if (busy) return "";
            if (btnPrimary.Contains(x, y)) return done ? "Launch Veyra" : (uninstallMode ? "Uninstall" : "Install");
            if (btnSecondary.Contains(x, y)) return done ? "Close" : "Cancel";
            if (!uninstallMode && !done && toggleRect.Contains(x, y)) return "desktop";
            return "";
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (busy) return;
            if (btnSecondary.Contains(e.X, e.Y)) { Close(); return; }
            if (toggleRect.Contains(e.X, e.Y) && !uninstallMode && !done)
            {
                desktopShortcut = !desktopShortcut;
                Invalidate();
                return;
            }
            if (!btnPrimary.Contains(e.X, e.Y)) return;
            if (done) { Launch(); return; }
            if (uninstallMode) StartUninstall();
            else StartInstall();
        }

        private void Launch()
        {
            try
            {
                if (File.Exists(TargetExe)) Process.Start(TargetExe);
            }
            catch (Exception ex) { error = "Could not launch: " + ex.Message; Invalidate(); }
        }

        private void StartInstall()
        {
            busy = true;
            error = "";
            progress = 0.02;
            status = "Preparing\u2026";
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Report(0.10, "Creating application folder\u2026");
                    Directory.CreateDirectory(Program.InstallDir);

                    Report(0.05, "Extracting Veyra and the Android runtime\u2026");
                    Program.ExtractAll(Program.InstallDir, delegate(double fraction, string message)
                    {
                        Report(0.05 + fraction * 0.88, message);
                    });

                    if (!File.Exists(TargetExe))
                        throw new InvalidOperationException("The Veyra.exe payload is missing from the archive.");

                    Report(0.93, "Adding bundled adb.exe to your PATH\u2026");
                    EnsureUserPath(Path.Combine(Program.InstallDir, "runtime", "adb"));

                    Report(0.94, "Creating shortcuts\u2026");
                    ShellShortcut.Create(StartMenuLnk, TargetExe, "", "Veyra \u2014 APK intelligence", Program.InstallDir);
                    if (desktopShortcut)
                        ShellShortcut.Create(DesktopLnk, TargetExe, "", "Veyra \u2014 APK intelligence", Program.InstallDir);

                    Report(0.97, "Registering in Apps & features\u2026");
                    WriteRegistry();

                    Report(1.0, "Installed successfully.");
                    SafeDone(null);
                }
                catch (Exception ex)
                {
                    SafeDone(ex);
                }
            });
        }

        private void StartUninstall()
        {
            if (MessageBox.Show("Remove Veyra and its settings from this PC?", "Remove Veyra",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            busy = true;
            error = "";
            progress = 0.05;
            status = "Removing\u2026";
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Report(0.20, "Removing shortcuts\u2026");
                    TryDelete(StartMenuLnk);
                    TryDelete(DesktopLnk);

                    Report(0.45, "Removing registry entry\u2026");
                    try { Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, false); } catch { }

                    Report(0.62, "Removing adb from your PATH\u2026");
                    RemoveUserPath(Path.Combine(Program.InstallDir, "runtime", "adb"));

                    Report(0.70, "Removing files\u2026");
                    TryDelete(Path.Combine(Program.InstallDir, "Veyra.exe"));
                    TryDelete(Path.Combine(Program.InstallDir, "veyra.cfg"));
                    TryDelete(Path.Combine(Program.InstallDir, "veyra-crash.log"));
                    TryDelete(Path.Combine(Program.InstallDir, "README.md"));

                    Report(0.95, "Finishing\u2026");
                    try
                    {
                        string self = Application.ExecutablePath;
                        foreach (string f in Directory.GetFiles(Program.InstallDir))
                            if (!string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
                        Directory.Delete(Program.InstallDir, true);
                    }
                    catch
                    {
                        // The uninstaller is still running from the folder; let cmd remove it after exit.
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                                "/c choice /t 2 /d y >nul & rmdir /s /q \"" + Program.InstallDir + "\"");
                            psi.CreateNoWindow = true;
                            psi.UseShellExecute = false;
                            Process.Start(psi);
                        }
                        catch { }
                    }

                    Report(1.0, "Removed. You can close this window.");
                    SafeDone(null);
                }
                catch (Exception ex) { SafeDone(ex); }
            });
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Appends a folder to the per-user PATH without touching existing entries.</summary>
        private static void EnsureUserPath(string dir)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Environment", true))
                {
                    if (key == null) return;
                    string current = (key.GetValue("Path") as string) ?? "";
                    foreach (string part in current.Split(';'))
                        if (string.Equals(part.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            return;
                    string next = current.Length == 0 ? dir : current.TrimEnd(';') + ";" + dir;
                    key.SetValue("Path", next, RegistryValueKind.ExpandString);
                }
                BroadcastEnvironmentChange();
            }
            catch { }
        }

        private static void RemoveUserPath(string dir)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Environment", true))
                {
                    if (key == null) return;
                    string current = (key.GetValue("Path") as string) ?? "";
                    List<string> keep = new List<string>();
                    foreach (string part in current.Split(';'))
                    {
                        if (part.Trim().Length == 0) continue;
                        if (string.Equals(part.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                        keep.Add(part.Trim());
                    }
                    key.SetValue("Path", string.Join(";", keep.ToArray()), RegistryValueKind.ExpandString);
                }
                BroadcastEnvironmentChange();
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            string lParam, uint flags, uint timeout, out IntPtr result);

        private static void BroadcastEnvironmentChange()
        {
            try
            {
                IntPtr result;
                SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out result);
            }
            catch { }
        }

        private void Report(double p, string text)
        {
            SafeInvoke(delegate { progress = p; status = text; Invalidate(); });
        }

        private void SafeDone(Exception ex)
        {
            SafeInvoke(delegate
            {
                busy = false;
                done = true;
                if (ex != null)
                {
                    error = "Failed: " + ex.Message;
                    progress = 0;
                }
                else
                {
                    progress = 1.0;
                    error = "";
                }
                Invalidate();
            });
        }

        private void SafeInvoke(Action a)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        private void WriteRegistry()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
            {
                if (k == null) throw new InvalidOperationException("Could not open the uninstall registry key.");
                long kb = Math.Min(int.MaxValue, Program.PayloadLength / 1024 + 16);
                k.SetValue("DisplayName", "Veyra");
                k.SetValue("DisplayVersion", "1.1.0");
                k.SetValue("Publisher", "Veyra");
                k.SetValue("DisplayIcon", TargetExe + ",0");
                k.SetValue("InstallLocation", Program.InstallDir);
                k.SetValue("UninstallString", "\"" + Path.Combine(Program.InstallDir, "Uninstall.exe") + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + Path.Combine(Program.InstallDir, "Uninstall.exe") + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }

    /// <summary>Read-only window over the appended ZIP payload.</summary>
    internal sealed class RangeStream : Stream
    {
        private readonly FileStream inner;
        private readonly long start;
        private readonly long length;
        private long position;

        public RangeStream(FileStream inner, long start, long length)
        {
            this.inner = inner;
            this.start = start;
            this.length = length;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position
        {
            get { return position; }
            set { position = value; }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length) return 0;
            long left = length - position;
            if (count > left) count = (int)left;
            inner.Position = start + position;
            int n = inner.Read(buffer, offset, count);
            position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = position;
            if (origin == SeekOrigin.Begin) next = offset;
            else if (origin == SeekOrigin.Current) next += offset;
            else if (origin == SeekOrigin.End) next = length + offset;
            if (next < 0) next = 0;
            if (next > length) next = length;
            position = next;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Shell link creation through the built-in WScript.Shell COM object.</summary>
    internal static class ShellShortcut
    {
        public static void Create(string lnkPath, string target, string args, string description, string workingDir)
        {
            string dir = Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { lnkPath });
            Type lt = link.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
            lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { args });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workingDir });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
    }
}
