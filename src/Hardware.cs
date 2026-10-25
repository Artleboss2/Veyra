using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace Veyra
{
    internal class SysInfo
    {
        public string CpuName = "Unknown CPU";
        public int Cores = 1;
        public int Threads = 1;
        public int Mhz;
        public string Arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";

        public long RamTotalMb;
        public long RamFreeMb;

        public string GpuName = "Unknown GPU";
        public string GpuDriver = "";
        public long GpuVramMb;

        public string DiskLetter = "C:";
        public long DiskTotalGb;
        public long DiskFreeGb;

        public string OsName = "Windows";
        public string OsBuild = "";
        public string HostName = "";
        public string Manufacturer = "";
        public string Model = "";

        public string DotNet = "";
        public string Java = "not detected";
        public string Adb = "not detected";

        public bool OnAc = true;
        public int BatteryPct = -1;
        public bool HasBattery;

        public int ScreenW, ScreenH;
        public float DpiScale = 1f;

        // Live values
        public double CpuLoad;
        public long ProcWorkingSetMb;
        public long ProcPrivateMb;
        public long ScanMs;

        public string CpuShort
        {
            get { return CpuName.Replace("Intel(R) ", "").Replace("CPU ", "").Replace("@", " @ ").Trim(); }
        }
    }

    internal static class Hardware
    {
        // ---------- P/Invoke ----------
        [StructLayout(LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
            public ulong ToU64() { return ((ulong)High << 32) | Low; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr h, [In, Out] PROCESS_MEMORY_COUNTERS m, uint cb);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        private static ulong prevIdle, prevKernel, prevUser;
        private static bool prevInit;
        private static Process currentProc;

        // ---------- Scanning ----------
        public static SysInfo Scan()
        {
            Stopwatch sw = Stopwatch.StartNew();
            SysInfo s = new SysInfo();

            s.CpuName = ReadReg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "Unknown CPU").Trim();
            s.Threads = Environment.ProcessorCount;
            s.Cores = s.Threads;
            s.HostName = Environment.MachineName;
            s.DotNet = "CLR " + Environment.Version + " / .NET Framework 4.8";
            s.Manufacturer = ReadReg(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", "");
            s.Model = ReadReg(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", "");

            // RAM (instant)
            UpdateLive(s);

            try
            {
                DriveInfo d = new DriveInfo("C");
                s.DiskLetter = "C:";
                s.DiskTotalGb = d.TotalSize / (1024L * 1024L * 1024L);
                s.DiskFreeGb = d.AvailableFreeSpace / (1024L * 1024L * 1024L);
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher se = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                {
                    foreach (ManagementObject mo in se.Get())
                    {
                        object nc = mo["NumberOfCores"];
                        if (nc != null) s.Cores = Convert.ToInt32(nc);
                        object nl = mo["NumberOfLogicalProcessors"];
                        if (nl != null) s.Threads = Convert.ToInt32(nl);
                        object mc = mo["MaxClockSpeed"];
                        if (mc != null) s.Mhz = Convert.ToInt32(mc);
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher se = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController"))
                {
                    long best = -1;
                    foreach (ManagementObject mo in se.Get())
                    {
                        string n = Convert.ToString(mo["Name"]);
                        if (string.IsNullOrEmpty(n)) continue;
                        if (n.IndexOf("basic display", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        long ram = 0;
                        try { if (mo["AdapterRAM"] != null) ram = Convert.ToInt64(mo["AdapterRAM"]); } catch { }
                        if (ram > best)
                        {
                            best = ram;
                            s.GpuName = n.Trim();
                            s.GpuDriver = Convert.ToString(mo["DriverVersion"]).Trim();
                            s.GpuVramMb = ram / (1024L * 1024L);
                        }
                    }
                }
            }
            catch { }

            try
            {
                s.OsName = ReadReg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "Windows");
                string display = ReadReg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "");
                string buildStr = ReadReg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "");
                int buildNum = 0;
                int.TryParse(buildStr, out buildNum);
                // Windows 11 still reports "Windows 10" in ProductName; the build number is authoritative.
                if (buildNum >= 22000 && s.OsName.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
                    s.OsName = s.OsName.Replace("Windows 10", "Windows 11");
                if (string.IsNullOrEmpty(buildStr)) buildStr = Environment.OSVersion.Version.Build.ToString();
                s.OsBuild = buildStr + (display.Length > 0 ? "  \u00b7  " + display : "");
            }
            catch { }

            s.Java = FindJava();
            s.Adb = FindAdb();

            try
            {
                System.Windows.Forms.Screen sc = System.Windows.Forms.Screen.PrimaryScreen;
                if (sc != null)
                {
                    s.ScreenW = sc.Bounds.Width;
                    s.ScreenH = sc.Bounds.Height;
                }
            }
            catch { }

            s.ScanMs = sw.ElapsedMilliseconds;
            return s;
        }

        public static void UpdateLive(SysInfo s)
        {
            try
            {
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(m))
                {
                    s.RamTotalMb = (long)(m.ullTotalPhys / (1024UL * 1024UL));
                    s.RamFreeMb = (long)(m.ullAvailPhys / (1024UL * 1024UL));
                }
            }
            catch { }

            try
            {
                FILETIME i, k, u;
                if (GetSystemTimes(out i, out k, out u))
                {
                    ulong idle = i.ToU64(), kern = k.ToU64(), user = u.ToU64();
                    if (prevInit)
                    {
                        ulong di = idle - prevIdle;
                        ulong dk = kern - prevKernel;
                        ulong du = user - prevUser;
                        ulong total = dk + du;
                        if (total > 0)
                        {
                            double load = 1.0 - ((double)di / (double)total);
                            if (load < 0) load = 0;
                            if (load > 1) load = 1;
                            s.CpuLoad = s.CpuLoad * 0.35 + load * 100.0 * 0.65;
                        }
                    }
                    prevIdle = idle; prevKernel = kern; prevUser = user; prevInit = true;
                }
            }
            catch { }

            try
            {
                if (currentProc == null) currentProc = Process.GetCurrentProcess();
                s.ProcWorkingSetMb = currentProc.WorkingSet64 / (1024L * 1024L);
                s.ProcPrivateMb = currentProc.PrivateMemorySize64 / (1024L * 1024L);
            }
            catch { }

            try
            {
                SYSTEM_POWER_STATUS ps;
                if (GetSystemPowerStatus(out ps))
                {
                    s.OnAc = ps.ACLineStatus == 1;
                    s.HasBattery = ps.BatteryFlag != 128 && ps.BatteryLifePercent != 255;
                    s.BatteryPct = ps.BatteryLifePercent == 255 ? -1 : ps.BatteryLifePercent;
                }
            }
            catch { }
        }

        // ---------- Tools ----------
        public static string ReadReg(string path, string name, string fallback)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return fallback;
                    object v = k.GetValue(name);
                    return v == null ? fallback : v.ToString();
                }
            }
            catch { return fallback; }
        }

        private static string FindJava()
        {
            List<string> candidates = new List<string>();
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string[] roots = new string[] { Path.Combine(pf, "Eclipse Adoptium"), Path.Combine(pf, "Java"), Path.Combine(pf, "Microsoft") };
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    string[] dirs = Directory.GetDirectories(root);
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        string exe = Path.Combine(dirs[i], "bin", "java.exe");
                        if (File.Exists(exe)) candidates.Add(exe);
                    }
                }
            }
            catch { }

            string onPath = Which("java.exe");
            if (onPath != null) candidates.Insert(0, onPath);

            if (candidates.Count == 0) return "not detected";
            string best = candidates[0];
            string ver = "";
            try
            {
                FileVersionInfo fi = FileVersionInfo.GetVersionInfo(best);
                if (!string.IsNullOrEmpty(fi.ProductVersion)) ver = fi.ProductVersion;
            }
            catch { }
            if (string.IsNullOrEmpty(ver)) ver = VersionOfJava(best);
            if (string.IsNullOrEmpty(ver)) ver = "detected";
            return ver + "  \u00b7  " + Shorten(best);
        }

        private static string VersionOfJava(string exe)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "-version");
                psi.UseShellExecute = false;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(2500);
                    string[] lines = err.Replace("\r", "").Split('\n');
                    if (lines.Length > 0) return lines[0].Replace("openjdk version ", "").Replace("\"", "").Trim();
                }
            }
            catch { }
            return "";
        }

        public static string AdbOverride;

        public static string FindAdb()
        {
            try
            {
                if (!string.IsNullOrEmpty(AdbOverride) && File.Exists(AdbOverride)) return AdbOverride;
            }
            catch { }
            try
            {
                string bundled = Path.Combine(LocalVm.Root(), @"adb\adb.exe");
                if (File.Exists(bundled)) return bundled;
            }
            catch { }
            string onPath = Which("adb.exe");
            if (onPath != null) return onPath;
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] probe = new string[]
                {
                    Path.Combine(local, @"Android\Sdk\platform-tools\adb.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"scrcpy\adb.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"scrcpy\adb.exe")
                };
                for (int i = 0; i < probe.Length; i++)
                    if (File.Exists(probe[i])) return probe[i];
            }
            catch { }
            return null;
        }

        public static string Which(string exe)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(path)) return null;
                string[] parts = path.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Trim().Length == 0) continue;
                    try
                    {
                        string full = Path.Combine(parts[i].Trim(), exe);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string Shorten(string p)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && p.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                    return "~" + p.Substring(home.Length);
            }
            catch { }
            return p;
        }
    }

    /// <summary>Adaptive runtime profile derived purely from local hardware, no network or AI.</summary>
    internal class Profile
    {
        public string Name = "Balanced";
        public int RenderW = 1280;
        public int RenderH = 720;
        public int FpsCap = 60;
        public int ScanThreads = 2;
        public int BudgetMb = 500;
        public string Note = "";

        public static Profile Compute(SysInfo s)
        {
            Profile p = new Profile();
            bool lowRam = s.RamTotalMb > 0 && s.RamTotalMb <= 4600;
            bool midRam = s.RamTotalMb > 4600 && s.RamTotalMb <= 9000;
            bool weakCpu = s.Cores <= 2;
            bool igpu = s.GpuName.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0;

            p.ScanThreads = Math.Max(1, Math.Min(3, s.Cores - 1));

            if (lowRam || weakCpu)
            {
                p.Name = "Eco";
                p.RenderW = 854; p.RenderH = 480;
                p.FpsCap = 30;
                p.BudgetMb = 320;
                p.Note = "Low-power profile: 480p, 30 fps cap, 1\u20132 decode threads. Protects a dual-core CPU from thermal throttling.";
            }
            else if (midRam)
            {
                p.Name = "Balanced";
                p.RenderW = 1280; p.RenderH = 720;
                p.FpsCap = 30;
                p.BudgetMb = 420;
                p.Note = "Balanced profile: 720p, 30 fps cap. Keeps the integrated GPU cool while staying visually sharp.";
            }
            else
            {
                p.Name = "Performance";
                p.RenderW = 1920; p.RenderH = 1080;
                p.FpsCap = 60;
                p.BudgetMb = 500;
                p.Note = "High-headroom profile: 1080p, 60 fps. Plenty of RAM and a strong CPU were detected.";
            }

            if (igpu && p.FpsCap > 30)
            {
                p.FpsCap = 30;
                p.Note += " Integrated GPU detected \u2014 frame cap held at 30 fps.";
            }

            if (s.RamFreeMb > 0 && s.RamFreeMb < 1500)
            {
                p.Note += " Only " + s.RamFreeMb + " MB free right now; close browsers before a session.";
            }
            return p;
        }
    }
}
