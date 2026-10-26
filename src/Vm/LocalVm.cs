using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Veyra
{
    /// <summary>
    /// Controller for the bundled Android-x86 machine. QEMU does the emulation; this class
    /// locates the components, picks the fastest accelerator that actually survives a probe,
    /// starts one process and talks to it over the bundled adb.
    /// </summary>
    internal static class LocalVm
    {
        // WHV_CAPABILITY is an 8-byte union whose first byte is the present flag.
        [DllImport("WinHvPlatform.dll")]
        private static extern int WHvGetCapability(int code, byte[] buffer, int size, out int written);

        public static string Root()
        {
            string env = Environment.GetEnvironmentVariable("VEYRA_RUNTIME");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            return Path.Combine(AppDir(), "runtime");
        }

        public static string AppDir()
        {
            try { return Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath); }
            catch { return "."; }
        }

        public static string QemuExe { get { return Path.Combine(Root(), @"qemu\qemu-system-x86_64.exe"); } }
        public static string QemuImg { get { return Path.Combine(Root(), @"qemu\qemu-img.exe"); } }
        public static string Kernel { get { return Path.Combine(Root(), @"android\kernel"); } }
        public static string Initrd { get { return Path.Combine(Root(), @"android\initrd.img"); } }
        public static string Iso { get { return Path.Combine(Root(), @"android\android-x86.iso"); } }
        public static string Adb { get { return Path.Combine(Root(), @"adb\adb.exe"); } }

        /// <summary>Guest state lives beside the executable, not in RAM and not in a per-user cache.</summary>
        public static string DataDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("VEYRA_VMDATA");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(AppDir(), "data");
            }
        }

        public static string DataDisk { get { return Path.Combine(DataDir, "disk.qcow2"); } }

        public static long DataDiskBytes
        {
            get
            {
                try { return File.Exists(DataDisk) ? new FileInfo(DataDisk).Length : 0; }
                catch { return 0; }
            }
        }

        /// <summary>
        /// A qcow2 disk that grows with the packages actually installed. It is created once;
        /// the guest initrd formats it as ext4 on the first boot and reuses it afterwards.
        /// </summary>
        public static bool EnsureDataDisk(out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(DataDir);
                if (File.Exists(DataDisk) && new FileInfo(DataDisk).Length > 0) return true;
                if (!File.Exists(QemuImg))
                {
                    error = "qemu-img.exe is missing from the runtime, so the Android data disk cannot be created.";
                    return false;
                }
                ProcessStartInfo psi = new ProcessStartInfo(QemuImg,
                    "create -f qcow2 \"" + DataDisk + "\" 6G");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(QemuImg);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                        error = "qemu-img did not finish creating the data disk.";
                        return false;
                    }
                    if (p.ExitCode != 0 || !File.Exists(DataDisk))
                    {
                        error = "qemu-img could not create " + DataDisk + " (code " + p.ExitCode + ").";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Data disk: " + ex.Message;
                return false;
            }
        }

        public static bool ComponentsPresent()
        {
            return File.Exists(QemuExe) && File.Exists(Kernel) && File.Exists(Initrd) && File.Exists(Iso);
        }

        /// <summary>True only if Windows exposes the hypervisor API.</summary>
        public static bool WhpxAvailable()
        {
            try
            {
                byte[] buffer = new byte[8];
                int written;
                int hr = WHvGetCapability(0, buffer, 8, out written);
                return hr == 0 && buffer[0] != 0;
            }
            catch { return false; }
        }

        private static int? whpxProbe;

        /// <summary>
        /// WHPX is present on most modern Windows builds, but the hypervisor can still refuse
        /// QEMU's partition configuration. A four second probe is the only honest answer, so the
        /// UI never promises acceleration that would die half a second after launch.
        /// </summary>
        public static bool WhpxViable()
        {
            if (whpxProbe.HasValue) return whpxProbe.Value == 1;
            bool ok = false;
            if (WhpxAvailable() && ComponentsPresent())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(QemuExe,
                        "-accel whpx,kernel-irqchip=off -machine pc -cpu host,-xsave -smp 1 -m 256 " +
                        "-display none -monitor none -serial none -S");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(QemuExe);
                    psi.RedirectStandardError = true;
                    psi.RedirectStandardOutput = true;
                    using (Process p = Process.Start(psi))
                    {
                        Thread.Sleep(2500);
                        ok = !p.HasExited;
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        try { p.WaitForExit(2000); } catch { }
                    }
                }
                catch { ok = false; }
            }
            whpxProbe = ok ? 1 : 0;
            return ok;
        }

        /// <summary>-1 while the probe runs, 1 when WHPX works, 0 when it does not.</summary>
        public static int ProbeState { get { return whpxProbe.HasValue ? whpxProbe.Value : -1; } }

        private static bool probing;

        /// <summary>Starts the probe off the UI thread so painting never blocks on a process spawn.</summary>
        public static void ProbeAsync()
        {
            if (probing || whpxProbe.HasValue) return;
            probing = true;
            ThreadPool.QueueUserWorkItem(delegate { WhpxViable(); probing = false; });
        }

        /// <summary>Short, paint-safe label for the current acceleration state.</summary>
        public static string AccelShort()
        {
            if (!WhpxAvailable()) return "software";
            if (!whpxProbe.HasValue) return "checking\u2026";
            return whpxProbe.Value == 1 ? "WHPX available" : "software";
        }

        public static bool AccelKnown { get { return whpxProbe.HasValue; } }

        public static string AccelerationName()
        {
            if (!WhpxAvailable()) return "software emulation";
            return WhpxViable() ? "WHPX hardware acceleration" : "software emulation (WHPX unavailable)";
        }

        public static string AdbPath(string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;
            if (File.Exists(Adb)) return Adb;
            return Hardware.FindAdb();
        }

        // ------------------------------------------------------------------ process
        private static Process proc;
        private static bool usingWhpx;
        private static bool guestReady;

        public static bool Running
        {
            get { try { return proc != null && !proc.HasExited; } catch { return false; } }
        }

        public static int ProcessId
        {
            get { try { return proc == null || proc.HasExited ? 0 : proc.Id; } catch { return 0; } }
        }

        public static bool Start(int ramMb, int width, int height, bool forceTcg, Action<string> log, out string error)
        {
            error = null;
            if (Running) return true;
            if (!ComponentsPresent())
            {
                error = "VM components are missing from " + Root() + ".";
                return false;
            }

            string diskError;
            if (!EnsureDataDisk(out diskError))
            {
                error = diskError;
                return false;
            }
            bool whpx = !forceTcg && WhpxViable();
            if (!TryStart(ramMb, whpx, log, out error))
            {
                if (whpx)
                {
                    // WHPX refused the machine: fall back once, automatically, so the user still gets Android.
                    if (log != null) log("vm: hardware acceleration failed, falling back to software emulation.");
                    if (!TryStart(ramMb, false, log, out error)) return false;
                }
                else return false;
            }
            return true;
        }

        private static bool TryStart(int ramMb, bool whpx, Action<string> log, out string error)
        {
            error = null;
            guestReady = false;
            string args = BuildArgs(ramMb, whpx);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(QemuExe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(QemuExe);
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                proc = new Process();
                proc.StartInfo = psi;
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (!string.IsNullOrEmpty(e.Data) && log != null) log("vm: " + e.Data); };
                proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (!string.IsNullOrEmpty(e.Data) && log != null) log("vm: " + e.Data); };
                proc.Start();
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
                usingWhpx = whpx;
                if (log != null)
                    log("Local VM starting \u00b7 " + ramMb + " MB \u00b7 " +
                        (whpx ? "WHPX hardware acceleration" : "software emulation") + ".");

                // A machine that dies in the first seconds never booted: report it instead of
                // leaving the UI waiting on an adb port that will never answer.
                Thread.Sleep(3500);
                if (proc.HasExited && proc.ExitCode != 0)
                {
                    error = "QEMU exited immediately (code " + proc.ExitCode + ").";
                    proc = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                proc = null;
                return false;
            }
        }

        private static string BuildArgs(int ramMb, bool whpx)
        {
            if (ramMb < 1024) ramMb = 1024;
            if (ramMb > 3072) ramMb = 3072;

            StringBuilder sb = new StringBuilder();
            sb.Append("-accel ").Append(whpx ? "whpx,kernel-irqchip=off" : "tcg").Append(' ');
            sb.Append("-machine pc -cpu ").Append(whpx ? "host,-xsave" : "max").Append(' ');
            sb.Append("-smp 2 ");
            sb.Append("-m ").Append(ramMb).Append(' ');
            sb.Append("-kernel \"").Append(Kernel).Append("\" ");
            sb.Append("-initrd \"").Append(Initrd).Append("\" ");
            sb.Append("-append \"root=/dev/ram0 SRC= DATA=/dev/sda SETUPWIZARD=0 androidboot.selinux=permissive androidboot.hardware=android_x86_64\" ");
            sb.Append("-cdrom \"").Append(Iso).Append("\" ");
            sb.Append("-drive \"file=").Append(DataDisk).Append(",if=ide,format=qcow2,cache=writeback\" ");
            sb.Append("-netdev user,id=net0,hostfwd=tcp:127.0.0.1:5555-10.0.2.15:5555 ");
            sb.Append("-device e1000,netdev=net0 ");
            sb.Append("-vga std -display sdl ");
            sb.Append("-name \"Veyra Android\" ");
            sb.Append("-rtc base=localtime ");
            sb.Append("-usb -device usb-tablet ");
            sb.Append("-monitor none ");
            return sb.ToString();
        }

        public static bool UsingWhpx { get { return usingWhpx && Running; } }

        /// <summary>
        /// Shut the guest down cleanly when adb is already answering, so ext4 has flushed
        /// /data before QEMU goes away. A machine that never booted is killed directly.
        /// </summary>
        public static void Stop()
        {
            Process p = proc;
            try
            {
                if (p == null) return;
                if (!p.HasExited)
                {
                    if (guestReady)
                    {
                        string adb = AdbPath(null);
                        if (!string.IsNullOrEmpty(adb))
                        {
                            RunAdb(adb, "-s 127.0.0.1:5555 shell sync", 3000);
                            RunAdb(adb, "-s 127.0.0.1:5555 shell reboot -p", 3000);
                            try { p.WaitForExit(8000); } catch { }
                        }
                    }
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(4000);
                    }
                }
            }
            catch { }
            finally { proc = null; guestReady = false; }
        }

        // ------------------------------------------------------------------ adb
        public static string RunAdb(string adb, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(adb, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(p.StandardOutput.ReadToEnd());
                    sb.Append(p.StandardError.ReadToEnd());
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return sb.ToString();
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Waits for the guest's adbd on the forwarded port. Returns true when authorised.</summary>
        public static bool WaitForAdb(string adb, int timeoutMs, Action<string> log, Func<bool> cancelled)
        {
            if (string.IsNullOrEmpty(adb)) return false;
            RunAdb(adb, "connect 127.0.0.1:5555", 5000);
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (cancelled != null && cancelled()) return false;
                if (!Running) return false;
                string dev = RunAdb(adb, "devices", 5000);
                if (dev != null && dev.IndexOf("127.0.0.1:5555\tdevice", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    guestReady = true;
                    return true;
                }
                if (waited % 15000 == 0 && log != null && waited > 0)
                {
                    string note = usingWhpx ? "" : " \u2014 software emulation, this takes several minutes";
                    log("Waiting for Android to finish booting\u2026 (" + (waited / 1000) + " s" + note + ")");
                }
                Thread.Sleep(1000);
                waited += 1000;
            }
            return false;
        }

        public static bool InstallAndLaunch(string adb, string apk, string package, string activity,
            Action<string> log, out string error)
        {
            error = null;
            string install = RunAdb(adb, "-s 127.0.0.1:5555 install -r \"" + apk + "\"", 240000);
            if (log != null) log("adb install \u00b7 " + Trim(install));
            if (install == null || install.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (install != null && install.IndexOf("INSTALL_FAILED_NO_MATCHING_ABIS", StringComparison.OrdinalIgnoreCase) >= 0)
                    error = "This package contains ARM-only native code; the x86 VM cannot load it.";
                else if (install != null && install.IndexOf("INSTALL_FAILED_", StringComparison.OrdinalIgnoreCase) >= 0)
                    error = Trim(install);
                else
                    error = "The VM did not accept the package.";
                return false;
            }

            string launch = "";
            if (!string.IsNullOrEmpty(activity))
                launch = RunAdb(adb, "-s 127.0.0.1:5555 shell am start -n \"" + package + "/" + activity + "\"", 45000);
            if (launch == null || launch.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || launch.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                launch = RunAdb(adb, "-s 127.0.0.1:5555 shell monkey -p " + package + " -c android.intent.category.LAUNCHER 1", 45000);
            if (log != null) log("adb start \u00b7 " + Trim(launch));
            return true;
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "\u2026" : s;
        }
    }
}
