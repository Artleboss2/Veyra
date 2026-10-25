using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Veyra
{
    internal partial class MainForm : Form
    {
        internal const string Version = T.Version;

        private enum Pg { Dashboard = 0, Runner = 1, Settings = 2, About = 3 }

        private SysInfo sys = new SysInfo();
        private Profile prof;
        private Settings cfg;
        private ApkInfo apk;
        private Pg page = Pg.Dashboard;

        private readonly List<Hit> chromeHits = new List<Hit>();
        private readonly List<Hit> pageHits = new List<Hit>();
        private readonly List<LogLine> logLines = new List<LogLine>();
        private string hover = "";

        private readonly int[] scroll = new int[4];
        private int maxScroll;
        private int consoleScroll;
        private Rectangle contentRect;
        private Rectangle consoleRect;
        private const int HeaderH = 48;
        private const int Pad = 16;

        private System.Windows.Forms.Timer tick;
        private System.Windows.Forms.Timer anim;
        private readonly Stopwatch animClock = Stopwatch.StartNew();
        private long lastAnimMs;

        private bool busy;
        private bool vmBusy;
        private bool resizing;
        private bool dragOver;
        private bool closing;
        private string pendingApk = "";

        private NetResult net;
        private string netStatus = "Not measured";
        private bool netBusy;

        private Bitmap frame;
        private int frameW, frameH;
        private bool frameDirty = true;

        private string toast = "";
        private DateTime toastUntil = DateTime.MinValue;
        private int ramPeak;
        private bool ramWarned;
        private bool ramBreach;
        private bool lastRamBreach;

        internal bool SuppressBoot;
        internal static double LastPaintMs;
        internal static double LastBuildMs;

        internal void ForceFullPaint()
        {
            frameDirty = true;
        }

        public MainForm(string preload)
        {
            pendingApk = preload == null ? "" : preload;
            cfg = Settings.Load();
            if (!string.IsNullOrEmpty(cfg.AdbPath) && File.Exists(cfg.AdbPath)) Hardware.AdbOverride = cfg.AdbPath;

            Text = "Veyra \u2014 Android package runner";
            BackColor = T.Bg;
            ForeColor = T.Text;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 680);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            AllowDrop = true;
            KeyPreview = true;

            int w = Math.Min(1220, Screen.PrimaryScreen.WorkingArea.Width - 60);
            int h = Math.Min(820, Screen.PrimaryScreen.WorkingArea.Height - 60);
            Size = new Size(Math.Max(1000, w), Math.Max(680, h));

            try { Icon = MakeIcon(); } catch { }

            Log("Veyra " + Version + " \u00b7 local Android runtime for low-power Windows", 1);
            Log("Drop an APK to inspect it, then launch it in the bundled Android machine.", 0);
            Log("Local only \u00b7 no accounts \u00b7 no telemetry \u00b7 no cloud calls.", 4);

            tick = new System.Windows.Forms.Timer();
            tick.Interval = Math.Max(400, cfg.TelemetryMs);
            tick.Tick += OnTick;
            tick.Start();

            anim = new System.Windows.Forms.Timer();
            anim.Interval = 90;
            anim.Tick += OnAnimTick;
            anim.Start();

            Shown += delegate
            {
                if (SuppressBoot) return;
                BeginScan();
                if (!string.IsNullOrEmpty(pendingApk) && File.Exists(pendingApk)) LoadApk(pendingApk);
                System.Windows.Forms.Timer autoNet = new System.Windows.Forms.Timer();
                autoNet.Interval = 1400;
                autoNet.Tick += delegate { autoNet.Stop(); autoNet.Dispose(); if (net == null) StartNetTest(); };
                autoNet.Start();
            };
        }

        /// <summary>Used by --shot: scans and loads synchronously so a bitmap is deterministic.</summary>
        internal void PrepareForShot(string apkPath, string pageName)
        {
            sys = Hardware.Scan();
            InitProfile();
            if (!string.IsNullOrEmpty(apkPath) && File.Exists(apkPath))
                apk = ApkInspector.Inspect(apkPath, delegate (string m) { Log(m, 0); });
            if (pageName == "runner") page = Pg.Runner;
            else if (pageName == "settings") page = Pg.Settings;
            else if (pageName == "about") page = Pg.About;
            else page = Pg.Dashboard;
            LocalVm.WhpxViable();
            Hardware.UpdateLive(sys);

            // Deterministic backdrop phase for screenshots and documentation.
            double at;
            if (double.TryParse(Environment.GetEnvironmentVariable("VEYRA_ANIM_T"), out at)) animT = at;
        }

        private void InitProfile()
        {
            prof = Profile.Compute(sys);
            if (cfg.AutoProfile)
            {
                cfg.Resolution = prof.RenderH <= 480 ? "480p" : (prof.RenderH <= 720 ? "720p" : "1080p");
                cfg.FpsCap = prof.FpsCap;
            }
        }

        private void BeginScan()
        {
            Log("Scanning host environment \u2026", 0);
            ThreadPool.QueueUserWorkItem(delegate
            {
                SysInfo s = Hardware.Scan();
                SafeInvoke(delegate
                {
                    sys = s;
                    if (sys.ProcWorkingSetMb > ramPeak) ramPeak = (int)sys.ProcWorkingSetMb;
                    InitProfile();
                    Log("CPU \u00b7 " + sys.CpuName + " (" + sys.Cores + "C/" + sys.Threads + "T, " + sys.Mhz + " MHz)", 0);
                    Log("RAM \u00b7 " + sys.RamTotalMb + " MB installed, " + sys.RamFreeMb + " MB available", 0);
                    Log("GPU \u00b7 " + sys.GpuName + " \u00b7 driver " + sys.GpuDriver, 0);
                    Log("Disk \u00b7 " + sys.DiskFreeGb + " GB free of " + sys.DiskTotalGb + " GB", 0);
                    Log("Profile \u00b7 " + prof.Name + " \u00b7 " + prof.RenderW + "\u00d7" + prof.RenderH + " @ " + prof.FpsCap + " fps", 1);
                    Log("ADB \u00b7 " + (Hardware.FindAdb() ?? "not found"), 4);
                    Log(LocalVm.ComponentsPresent()
                        ? "Runtime \u00b7 components ready \u00b7 " + LocalVm.AccelerationName()
                        : "Runtime \u00b7 components not installed \u2014 run VeyraSetup.exe", LocalVm.ComponentsPresent() ? 1 : 2);
                    LocalVm.ProbeAsync();
                    RefreshUi();
                });
            });
        }

        // ---------------------------------------------------------------- logging
        private void Log(string text, int kind)
        {
            if (string.IsNullOrEmpty(text)) return;
            LogLine l = new LogLine();
            l.Text = text;
            l.Kind = kind;
            logLines.Add(l);
            if (logLines.Count > 500) logLines.RemoveAt(0);
            consoleScroll = 0;
            frameDirty = true;
            if (IsHandleCreated) Invalidate();
        }

        private void SafeInvoke(Action a)
        {
            if (closing || !IsHandleCreated) return;
            try { BeginInvoke(a); }
            catch { }
        }

        private void Toast(string text)
        {
            toast = text;
            toastUntil = DateTime.Now.AddSeconds(3.2);
            frameDirty = true;
            Invalidate();
        }

        private void RefreshUi()
        {
            frameDirty = true;
            Invalidate();
        }

        // ---------------------------------------------------------------- APK
        private void Browse()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Select an Android package";
                d.Filter = "Android packages (*.apk)|*.apk|All files (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) LoadApk(d.FileName);
            }
        }

        private void LoadApk(string path)
        {
            if (busy) { Toast("Still inspecting the previous package"); return; }
            if (!File.Exists(path)) { Toast("File not found"); return; }
            if (!path.ToLowerInvariant().EndsWith(".apk"))
                Log("File does not end in .apk \u2014 trying anyway: " + Path.GetFileName(path), 2);

            busy = true;
            apk = null;
            page = Pg.Runner;
            scroll[(int)Pg.Runner] = 0;
            Log("Inspecting " + Path.GetFileName(path) + " \u00b7 stays local, nothing uploaded", 0);
            RefreshUi();

            ThreadPool.QueueUserWorkItem(delegate
            {
                ApkInfo info = ApkInspector.Inspect(path, delegate (string m)
                {
                    SafeInvoke(delegate { Log(m, 4); });
                });

                SafeInvoke(delegate
                {
                    apk = info;
                    busy = false;
                    if (info == null || !string.IsNullOrEmpty(info.Error)) Log("Inspection finished with errors", 3);
                    else Log("Ready \u00b7 " + info.DisplayName + " \u00b7 " + info.PackageName, 1);
                    RefreshUi();
                });
            });
        }

        private void ExportReport()
        {
            if (apk == null) { Toast("Load an APK first"); return; }
            string dir = "";
            try { dir = Path.GetDirectoryName(apk.Path); } catch { }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Export inspection report";
                d.Filter = "Text report (*.txt)|*.txt";
                d.FileName = SafeName(apk.DisplayName) + "-veyra-report.txt";
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) d.InitialDirectory = dir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, BuildReport(apk), Encoding.UTF8);
                    Log("Report exported \u00b7 " + d.FileName, 1);
                    Toast("Report saved");
                }
                catch (Exception ex) { Log("Export failed: " + ex.Message, 3); Toast("Export failed"); }
            }
        }

        private static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "package";
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++) s = s.Replace(bad[i], '_');
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        private string BuildReport(ApkInfo a)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("VEYRA \u2014 APK INSPECTION REPORT");
            sb.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("============================================================");
            sb.AppendLine();
            sb.AppendLine("PACKAGE");
            sb.AppendLine("  File          : " + a.Path);
            sb.AppendLine("  Label         : " + a.DisplayName);
            sb.AppendLine("  Package       : " + a.PackageName);
            sb.AppendLine("  Version       : " + a.VersionName + " (" + a.VersionCode + ")");
            sb.AppendLine("  Size          : " + T.Bytes(a.SizeBytes) + "  \u00b7  " + a.EntryCount + " entries");
            sb.AppendLine("  SHA-256 (8)   : " + a.Fingerprint);
            sb.AppendLine("  minSdk        : " + a.MinSdk + "   targetSdk: " + a.TargetSdk + "   compileSdk: " + a.CompileSdk);
            sb.AppendLine("  Launcher      : " + a.MainActivity);
            sb.AppendLine("  Debuggable    : " + (a.Debuggable ? "yes" : "no"));
            sb.AppendLine("  Signature     : " + a.Signature);
            sb.AppendLine();
            sb.AppendLine("CODE");
            sb.AppendLine("  DEX files     : " + a.DexCount + "  (" + string.Join(", ", a.DexFiles.ToArray()) + ")");
            sb.AppendLine("  Classes       : " + a.ClassCount);
            sb.AppendLine("  Methods       : " + a.MethodCount);
            sb.AppendLine("  Fields        : " + a.FieldCount);
            sb.AppendLine("  Strings       : " + a.StringCount);
            sb.AppendLine("  Native ABIs   : " + (a.Abis.Count == 0 ? "none (pure bytecode)" : string.Join(", ", a.Abis.ToArray())));
            sb.AppendLine("  Engines       : " + (a.Frameworks.Count == 0 ? "none detected" : string.Join(", ", a.Frameworks.ToArray())));
            sb.AppendLine();
            sb.AppendLine("PERMISSIONS (" + a.Permissions.Count + ")");
            for (int i = 0; i < a.Permissions.Count; i++) sb.AppendLine("  - " + a.Permissions[i]);
            sb.AppendLine();
            sb.AppendLine("ACTIVITIES (" + a.Activities.Count + ")");
            for (int i = 0; i < a.Activities.Count && i < 40; i++) sb.AppendLine("  - " + a.Activities[i]);
            sb.AppendLine();
            sb.AppendLine("VERDICT \u00b7 demand " + a.Verdict.Demand + "/100");
            sb.AppendLine("  " + a.Verdict.Headline);
            sb.AppendLine("  " + a.Verdict.Detail);
            for (int i = 0; i < a.Verdict.Reasons.Count; i++) sb.AppendLine("  * " + a.Verdict.Reasons[i]);
            sb.AppendLine();
            sb.AppendLine("HOST");
            sb.AppendLine("  " + sys.CpuName + " \u00b7 " + sys.Cores + "C/" + sys.Threads + "T");
            sb.AppendLine("  " + sys.RamTotalMb + " MB RAM \u00b7 " + sys.GpuName);
            sb.AppendLine("  " + sys.OsName + " build " + sys.OsBuild);
            sb.AppendLine("  Veyra " + Version + " \u00b7 local Android VM \u00b7 no network uploads");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- tools
        private void StartNetTest()
        {
            if (netBusy) return;
            netBusy = true;
            netStatus = "Testing\u2026";
            Log("Network probe \u00b7 latency + 1.5 MB download from a public edge (no key)", 4);
            RefreshUi();
            NetProbe.Run(
                delegate (string m) { SafeInvoke(delegate { netStatus = m; RefreshUi(); }); },
                delegate (NetResult r)
                {
                    SafeInvoke(delegate
                    {
                        netBusy = false;
                        net = r;
                        if (!string.IsNullOrEmpty(r.Error))
                        {
                            netStatus = "Failed";
                            Log("Network probe failed \u00b7 " + r.Error, 2);
                        }
                        else
                        {
                            netStatus = "Measured";
                            Log("Network \u00b7 " + r.DownMbps + " Mbps down \u00b7 " + r.LatencyMs + " ms \u00b7 " + r.Adapter, 1);
                        }
                        RefreshUi();
                    });
                });
        }

        private void BridgeAction()
        {
            string adb = ResolveAdb();
            if (string.IsNullOrEmpty(adb))
            {
                Log("Bridge \u00b7 adb.exe not found. Point Veyra at adb in Settings \u2192 Device bridge.", 2);
                Toast("adb not found \u2014 see Settings");
                return;
            }
            if (apk == null) { Toast("Load an APK first"); return; }
            Log("Bridge \u00b7 querying devices via " + adb, 4);
            Toast("Checking devices\u2026");
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> devices = Bridge.Devices(adb);
                SafeInvoke(delegate
                {
                    if (devices.Count == 0)
                    {
                        Log("Bridge \u00b7 no authorised device. Enable USB debugging, accept the RSA prompt.", 2);
                        Toast("No device connected");
                        return;
                    }
                    Log("Bridge \u00b7 " + devices.Count + " device(s): " + string.Join(", ", devices.ToArray()), 1);
                    string first = devices[0];
                    if (first.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log("Bridge \u00b7 device is unauthorized \u2014 accept the prompt on the phone.", 2);
                        return;
                    }
                    Toast("Installing on " + first + "\u2026");
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Bridge.InstallAndLaunch(adb, apk.Path, apk.PackageName, apk.MainActivity,
                            delegate (string m) { SafeInvoke(delegate { Log("  " + m, 4); }); },
                            delegate (bool ok)
                            {
                                SafeInvoke(delegate
                                {
                                    Log(ok ? "Bridge \u00b7 running on the device." : "Bridge \u00b7 install failed.", ok ? 1 : 3);
                                    Toast(ok ? "Launched on device" : "Install failed");
                                });
                            });
                    });
                });
            });
        }

        /// <summary>Run is offered only when the audit says the x86 VM can install the package.</summary>
        private bool CanRunInVm()
        {
            return apk != null && !vmBusy && (apk.Verdict == null || apk.Verdict.VmCompatible);
        }

        private void RunInVm()
        {
            if (apk == null) { Toast("Load an APK first"); return; }
            if (vmBusy) { Toast("The VM is already starting"); return; }
            if (apk.Verdict != null && !apk.Verdict.VmCompatible)
            {
                Log("Verdict \u00b7 " + apk.Verdict.Headline + " \u2014 not runnable in the local x86 VM.", 2);
                Toast("Not compatible with the local x86 VM");
                return;
            }

            string adb = LocalVm.AdbPath(cfg.AdbPath);
            if (!LocalVm.ComponentsPresent())
            {
                Log("Runtime \u00b7 components are not installed. Run VeyraSetup.exe to add them.", 3);
                Toast("VM runtime not installed");
                return;
            }
            if (string.IsNullOrEmpty(adb))
            {
                Log("Runtime \u00b7 adb.exe is missing from runtime\\adb.", 3);
                Toast("adb.exe missing");
                return;
            }

            vmBusy = true;
            Log("Runtime \u00b7 preparing " + apk.DisplayName + " for the x86 Android machine", 0);
            Toast("Starting Android VM\u2026");
            RefreshUi();

            Action<string> vmLog = delegate(string m)
            {
                SafeInvoke(delegate { Log(m, 4); });
            };

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    if (!LocalVm.Running)
                    {
                        if (!LocalVm.Start(cfg.VmRamMb, 1280, 720, cfg.VmForceTcg, vmLog, out error))
                        {
                            SafeInvoke(delegate { vmBusy = false; Log("Runtime \u00b7 start failed: " + error, 3); Toast("VM failed to start"); RefreshUi(); });
                            return;
                        }
                    }

                    Func<bool> stop = delegate { return closing; };
                    bool ready = LocalVm.WaitForAdb(adb, LocalVm.WhpxViable() ? 120000 : 900000, vmLog, stop);

                    // Some Windows builds accept WHPX and then stall Android's cold boot. Rather than
                    // leaving the user with a machine that never answers, drop to software once.
                    if (!ready && LocalVm.UsingWhpx && !closing)
                    {
                        vmLog("Runtime \u00b7 acceleration stalled during boot \u2014 restarting in software mode.");
                        LocalVm.Stop();
                        if (LocalVm.Start(cfg.VmRamMb, 0, 0, true, vmLog, out error))
                            ready = LocalVm.WaitForAdb(adb, 900000, vmLog, stop);
                        else
                            vmLog("Runtime \u00b7 software restart failed: " + error);
                    }

                    if (!ready)
                    {
                        SafeInvoke(delegate
                        {
                            vmBusy = false;
                            Log("Runtime \u00b7 Android did not answer adb on 127.0.0.1:5555.", 3);
                            Toast("Android did not finish booting");
                            RefreshUi();
                        });
                        return;
                    }

                    bool ok = LocalVm.InstallAndLaunch(adb, apk.Path, apk.PackageName, apk.MainActivity, vmLog, out error);
                    SafeInvoke(delegate
                    {
                        vmBusy = false;
                        if (ok)
                        {
                            Log("Runtime \u00b7 " + apk.DisplayName + " is running in the Android window.", 1);
                            Toast("Running in Veyra VM");
                        }
                        else
                        {
                            Log("Runtime \u00b7 install failed: " + error, 3);
                            Toast("APK could not be installed");
                        }
                        RefreshUi();
                    });
                }
                catch (Exception ex)
                {
                    SafeInvoke(delegate { vmBusy = false; Log("Runtime \u00b7 " + ex.Message, 3); Toast("VM error"); RefreshUi(); });
                }
            });
        }

        private void StopVm()
        {
            if (!LocalVm.Running) { Toast("The VM is not running"); return; }
            LocalVm.Stop();
            vmBusy = false;
            Log("Runtime \u00b7 machine stopped.", 0);
            Toast("Android VM stopped");
            RefreshUi();
        }

        private string ResolveAdb()
        {
            return LocalVm.AdbPath(cfg.AdbPath);
        }

        private void LocateAdb()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Locate adb.exe (optional \u2014 device bridge)";
                d.Filter = "adb.exe|adb.exe|Executables (*.exe)|*.exe";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                cfg.AdbPath = d.FileName;
                Hardware.AdbOverride = d.FileName;
                cfg.Save();
                Log("ADB configured \u00b7 " + d.FileName, 1);
                Toast("adb configured");
                RefreshUi();
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { Toast("Folder not found"); return; }
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { Toast("Could not open the folder"); }
        }

        // ---------------------------------------------------------------- timers
        /// <summary>Slow drift for the backdrop glow; idle when the window is not in use.</summary>
        private void OnAnimTick(object sender, EventArgs e)
        {
            if (SuppressBoot || !Visible || WindowState == FormWindowState.Minimized) return;
            if (!ContainsFocus) return;
            long now = animClock.ElapsedMilliseconds;
            if (lastAnimMs == 0) lastAnimMs = now;
            // Accumulate only while animating, so refocusing never makes the glow jump.
            if (now - lastAnimMs < 40) return;
            animT += (now - lastAnimMs) / 1000.0;
            lastAnimMs = now;
            Invalidate();
        }

        private void OnTick(object sender, EventArgs e)
        {
            Hardware.UpdateLive(sys);
            if (sys.ProcWorkingSetMb > ramPeak) ramPeak = (int)sys.ProcWorkingSetMb;

            if (sys.ProcWorkingSetMb > 500)
            {
                if (!ramBreach) Log("MEMORY GUARD \u00b7 working set " + sys.ProcWorkingSetMb + " MB exceeded 500 MB \u2014 trimming", 3);
                ramBreach = true;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            else ramBreach = false;

            if (!ramWarned && sys.ProcWorkingSetMb > 380)
            {
                ramWarned = true;
                Log("Memory guard \u00b7 approaching the 500 MB ceiling (" + sys.ProcWorkingSetMb + " MB)", 2);
            }

            if (toastUntil != DateTime.MinValue && DateTime.Now > toastUntil)
            {
                toastUntil = DateTime.MinValue;
                toast = "";
            }

            lastRamBreach = ramBreach;
            frameDirty = true;
            if (IsHandleCreated) Invalidate();
        }

        // ---------------------------------------------------------------- input
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Hit h = HitAt(e.X, e.Y);
            string id = h == null ? "" : h.Id;
            if (id != hover)
            {
                hover = id;
                Cursor = id.Length > 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != "") { hover = ""; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Hit h = HitAt(e.X, e.Y);
            if (h == null) return;
            HandleClick(h.Id);
        }

        private Hit HitAt(int x, int y)
        {
            for (int i = chromeHits.Count - 1; i >= 0; i--)
                if (chromeHits[i].Enabled && chromeHits[i].Contains(x, y)) return chromeHits[i];

            if (!contentRect.Contains(x, y)) return null;
            for (int i = pageHits.Count - 1; i >= 0; i--)
            {
                Hit h = pageHits[i];
                if (!h.Enabled) continue;
                if (h.Contains(x, y)) return h;
            }
            return null;
        }

        private void HandleClick(string id)
        {
            if (id.StartsWith("nav:"))
            {
                string which = id.Substring(4);
                if (which == "dashboard") page = Pg.Dashboard;
                else if (which == "runner") page = Pg.Runner;
                else if (which == "settings") page = Pg.Settings;
                else if (which == "about") page = Pg.About;
                scroll[(int)page] = 0;
                RefreshUi();
                return;
            }
            if (id.StartsWith("seg:"))
            {
                string[] parts = id.Split(':');
                if (parts.Length < 3) return;
                int idx;
                if (!int.TryParse(parts[2], out idx)) return;
                ApplySeg(parts[1], idx);
                return;
            }

            switch (id)
            {
                case "btn:browse": Browse(); break;
                case "btn:drop": Browse(); break;
                case "btn:export": ExportReport(); break;
                case "btn:bridge": BridgeAction(); break;
                case "btn:runvm": RunInVm(); break;
                case "btn:stopvm": StopVm(); break;
                case "btn:whpx":
                    cfg.VmForceTcg = !cfg.VmForceTcg;
                    cfg.Save();
                    Log("Runtime \u00b7 acceleration " + (cfg.VmForceTcg ? "pinned to software emulation (applies to the next start)" : "automatic \u2014 WHPX when this PC accepts it"), 0);
                    Toast(cfg.VmForceTcg ? "Software mode" : "Automatic mode");
                    RefreshUi();
                    break;
                case "btn:net": StartNetTest(); break;
                case "btn:rescan": BeginScan(); Toast("Rescanning hardware\u2026"); break;
                case "btn:applyProfile": ApplyProfile(); break;
                case "btn:openFolder":
                    if (apk != null) Process.Start("explorer.exe", "/select,\"" + apk.Path + "\"");
                    break;
                case "btn:clearLog": logLines.Clear(); Log("Console cleared", 4); RefreshUi(); break;
                case "btn:verbose":
                    cfg.Verbose = !cfg.Verbose;
                    cfg.Save();
                    RefreshUi();
                    break;
                case "btn:locateAdb": LocateAdb(); break;
                case "btn:openRuntime": OpenFolder(LocalVm.Root()); break;
                case "btn:openProject": OpenFolder(LocalVm.AppDir()); break;
                case "btn:autoprofile":
                    cfg.AutoProfile = !cfg.AutoProfile;
                    cfg.Save();
                    if (cfg.AutoProfile) ApplyProfile();
                    RefreshUi();
                    Toast(cfg.AutoProfile ? "Adaptive profile on" : "Adaptive profile off");
                    break;
                case "btn:details": page = Pg.Runner; scroll[(int)Pg.Runner] = 0; RefreshUi(); break;
            }
        }

        private void ApplySeg(string key, int idx)
        {
            if (key == "res")
            {
                cfg.Resolution = idx == 0 ? "480p" : (idx == 1 ? "720p" : "1080p");
                cfg.AutoProfile = false;
            }
            else if (key == "orient") cfg.Orientation = idx == 0 ? "Portrait" : "Landscape";
            else if (key == "fps") cfg.FpsCap = idx == 0 ? 30 : 60;
            else if (key == "tick")
            {
                cfg.TelemetryMs = idx == 0 ? 1000 : (idx == 1 ? 2000 : 5000);
                tick.Interval = cfg.TelemetryMs;
            }
            else if (key == "vmram")
            {
                cfg.VmRamMb = idx == 0 ? 1536 : (idx == 1 ? 2048 : (idx == 2 ? 2560 : 3072));
                Toast("VM memory " + (cfg.VmRamMb / 1024.0).ToString("0.0") + " GB \u00b7 applies next start");
            }
            cfg.Save();
            RefreshUi();
        }

        private void ApplyProfile()
        {
            if (prof == null) return;
            cfg.AutoProfile = true;
            cfg.Resolution = prof.RenderH <= 480 ? "480p" : (prof.RenderH <= 720 ? "720p" : "1080p");
            cfg.FpsCap = prof.FpsCap;
            cfg.Save();
            Log("Adaptive profile applied \u00b7 " + cfg.Resolution + " @ " + cfg.FpsCap + " fps", 1);
            Toast("Adaptive profile applied");
            RefreshUi();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int dir = e.Delta > 0 ? -1 : 1;

            if (consoleRect.Contains(e.X, e.Y))
            {
                consoleScroll += dir * -2;
                if (consoleScroll < 0) consoleScroll = 0;
                frameDirty = true;
                Invalidate();
                return;
            }

            if (page == Pg.Dashboard) return;
            int next = scroll[(int)page] + dir * 72;
            if (next < 0) next = 0;
            if (next > maxScroll) next = maxScroll;
            if (next != scroll[(int)page]) { scroll[(int)page] = next; frameDirty = true; Invalidate(); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.O) { Browse(); e.Handled = true; }
            if (e.KeyCode == Keys.F5) { BeginScan(); e.Handled = true; }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                dragOver = true;
                Invalidate();
            }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            dragOver = false;
            Invalidate();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            dragOver = false;
            Invalidate();
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) LoadApk(files[0]);
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            resizing = true;
            base.OnResizeBegin(e);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            resizing = false;
            frameDirty = true;
            base.OnResizeEnd(e);
            Invalidate();
        }

        internal void SetScroll(int value)
        {
            scroll[(int)page] = Math.Max(0, value);
            frameDirty = true;
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (anim != null) { anim.Stop(); anim.Dispose(); anim = null; }
            if (tick != null) { tick.Stop(); tick.Dispose(); tick = null; }
            if (auroraA != null) { auroraA.Dispose(); auroraA = null; }
            if (auroraB != null) { auroraB.Dispose(); auroraB = null; }
            if (bgClip != null) { bgClip.Dispose(); bgClip = null; }
            if (frame != null) { frame.Dispose(); frame = null; }
            base.OnFormClosed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            LocalVm.Stop();
            cfg.Save();
            base.OnFormClosing(e);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref on, 4);
            }
            catch { }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private Icon MakeIcon()
        {
            // build.sh embeds assets/veyra.ico with -win32icon, so the executable,
            // Explorer, the taskbar and every shortcut share one 9-size artwork.
            try
            {
                Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null) return embedded;
            }
            catch { }

            // Fallback for a build compiled without the icon resource.
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    T.FillRound(g, new RectangleF(0, 0, 32, 32), 7f, T.Accent);
                    T.TextC(g, "V", T.F(17f, true), Color.White, new RectangleF(0, -1, 32, 34));
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(h)) return (Icon)temp.Clone();
                }
                finally { DestroyIcon(h); }
            }
        }
    }
}
