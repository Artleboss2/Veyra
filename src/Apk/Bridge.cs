using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Veyra
{
    /// <summary>
    /// Optional zero-emulation path: hand the APK to a real Android device over ADB.
    /// Nothing here runs unless adb.exe exists and a device is authorised.
    /// </summary>
    internal static class Bridge
    {
        public static List<string> Devices(string adb)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(adb)) return list;
            try
            {
                string output = Run(adb, "devices", 6000);
                string[] lines = output.Replace("\r", "").Split('\n');
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("List of devices")) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2 && parts[1].Trim() == "device") list.Add(parts[0].Trim());
                    else if (parts.Length >= 2 && parts[1].Trim() == "unauthorized") list.Add(parts[0].Trim() + " (unauthorized)");
                }
            }
            catch { }
            return list;
        }

        public static bool InstallAndLaunch(string adb, string apk, string package, string activity, Action<string> log, Action<bool> done)
        {
            bool ok = false;
            try
            {
                log("adb install -r " + System.IO.Path.GetFileName(apk));
                string install = Run(adb, "install -r \"" + apk + "\"", 120000);
                log(install.Trim());
                ok = install.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0;

                if (ok)
                {
                    if (!string.IsNullOrEmpty(activity))
                    {
                        string launch = "shell am start -n " + package + "/" + activity;
                        log("adb " + launch);
                        string outp = Run(adb, launch, 20000);
                        log(outp.Trim());
                    }
                    else
                    {
                        log("adb shell monkey -p " + package + " 1");
                        string outp = Run(adb, "shell monkey -p " + package + " -c android.intent.category.LAUNCHER 1", 20000);
                        log(outp.Trim());
                    }
                }
                else log("Install did not report success \u2014 check the device screen for a confirmation prompt.");
            }
            catch (Exception ex)
            {
                log("Bridge error: " + ex.Message);
                ok = false;
            }
            if (done != null) done(ok);
            return ok;
        }

        private static string Run(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                StringBuilder sb = new StringBuilder();
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) sb.AppendLine(e.Data);
                };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) sb.AppendLine(e.Data);
                };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return sb.ToString() + "\n(timed out after " + (timeoutMs / 1000) + "s)";
                }
                p.WaitForExit();
                return sb.ToString();
            }
        }
    }
}
