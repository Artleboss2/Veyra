using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Threading;

namespace Veyra
{
    internal class NetResult
    {
        public double LatencyMs = -1;
        public double DownMbps = -1;
        public long Bytes;
        public double Seconds;
        public string Error = "";
        public string Adapter = "";
        public string LinkSpeed = "";
        public string Endpoint = "speed.cloudflare.com";
    }

    /// <summary>Keyless throughput probe against Cloudflare's public speed endpoint. No API key, no account.</summary>
    internal static class NetProbe
    {
        private const string BaseUrl = "https://speed.cloudflare.com/__down?bytes=";

        public static void Run(Action<string> progress, Action<NetResult> done)
        {
            Thread t = new Thread(delegate ()
            {
                NetResult r = new NetResult();
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    r.Adapter = AdapterInfo(out r.LinkSpeed);

                    progress("Pinging speed.cloudflare.com\u2026");
                    double total = 0;
                    int ok = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        try
                        {
                            Fetch(1000, 6000);
                            sw.Stop();
                            total += sw.Elapsed.TotalMilliseconds;
                            ok++;
                        }
                        catch { }
                    }
                    if (ok > 0) r.LatencyMs = Math.Round(total / ok, 1);

                    progress("Measuring download throughput (1.5 MB)\u2026");
                    Stopwatch dl = Stopwatch.StartNew();
                    long got = Fetch(1536 * 1024, 15000);
                    dl.Stop();
                    r.Bytes = got;
                    r.Seconds = dl.Elapsed.TotalSeconds;
                    if (got > 0 && r.Seconds > 0.05)
                        r.DownMbps = Math.Round(got * 8.0 / r.Seconds / 1000000.0, 2);
                    else
                        throw new Exception("empty response");
                }
                catch (Exception ex)
                {
                    r.Error = Friendly(ex);
                }
                done(r);
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        private static long Fetch(int bytes, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(BaseUrl + bytes);
            req.Proxy = null;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Veyra/1.0";
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            {
                byte[] buffer = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) total += n;
                return total;
            }
        }

        private static string AdapterInfo(out string speed)
        {
            speed = "";
            try
            {
                using (ManagementObjectSearcher se = new ManagementObjectSearcher(
                    "SELECT Name, Speed, NetConnectionID FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE"))
                {
                    foreach (ManagementObject mo in se.Get())
                    {
                        string name = Convert.ToString(mo["Name"]);
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        long bps = 0;
                        try { if (mo["Speed"] != null) bps = Convert.ToInt64(mo["Speed"]); } catch { }
                        if (bps > 0)
                            speed = (bps / 1000000) + " Mbps link";
                        return name.Trim();
                    }
                }
            }
            catch { }
            return "network adapter unavailable";
        }

        private static string Friendly(Exception ex)
        {
            WebException we = ex as WebException;
            if (we != null)
            {
                if (we.Status == WebExceptionStatus.Timeout) return "Timed out \u2014 connection too slow or blocked.";
                if (we.Status == WebExceptionStatus.NameResolutionFailure) return "DNS lookup failed \u2014 offline?";
                if (we.Status == WebExceptionStatus.ConnectFailure) return "Could not connect \u2014 firewall or offline.";
            }
            return ex.Message;
        }
    }

    /// <summary>Small INI-style settings file stored next to the executable.</summary>
    internal class Settings
    {
        public string Resolution = "720p";
        public string Orientation = "Portrait";
        public int FpsCap = 30;
        public int TelemetryMs = 1000;
        public bool Ambient = false;
        public string Accent = "Violet";
        public bool AutoProfile = true;
        public string AdbPath = "";
        public int VmRamMb = 2048;
        public bool VmForceTcg = false;
        public bool Verbose = true;

        private static string FilePath()
        {
            try
            {
                return Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), "veyra.cfg");
            }
            catch { return "veyra.cfg"; }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath())) return s;
                string[] lines = File.ReadAllLines(FilePath());
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "resolution") s.Resolution = v;
                    else if (k == "orientation") s.Orientation = v;
                    else if (k == "fps") { int n; if (int.TryParse(v, out n)) s.FpsCap = n; }
                    else if (k == "telemetry") { int n; if (int.TryParse(v, out n)) s.TelemetryMs = Math.Max(250, n); }
                    else if (k == "ambient") s.Ambient = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (k == "accent") s.Accent = v;
                    else if (k == "autoprofile") s.AutoProfile = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (k == "adb") s.AdbPath = v;
                    else if (k == "vmram") { int n; if (int.TryParse(v, out n)) s.VmRamMb = n; }
                    else if (k == "vmtcg") s.VmForceTcg = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (k == "verbose") s.Verbose = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# Veyra settings \u2014 safe to edit or delete (regenerated on first launch).");
                lines.Add("resolution=" + Resolution);
                lines.Add("orientation=" + Orientation);
                lines.Add("fps=" + FpsCap);
                lines.Add("telemetry=" + TelemetryMs);
                lines.Add("ambient=" + (Ambient ? "1" : "0"));
                lines.Add("accent=" + Accent);
                lines.Add("autoprofile=" + (AutoProfile ? "1" : "0"));
                lines.Add("adb=" + AdbPath);
                lines.Add("vmram=" + VmRamMb);
                lines.Add("vmtcg=" + (VmForceTcg ? "1" : "0"));
                lines.Add("verbose=" + (Verbose ? "1" : "0"));
                File.WriteAllLines(FilePath(), lines.ToArray());
            }
            catch { }
        }
    }
}
