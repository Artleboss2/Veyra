using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Veyra;

namespace VeyraSetup
{
    internal static class Program
    {
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Veyra";

        /// <summary>Release tag that holds the runtime asset and its manifest.</summary>
        internal const string ReleaseTag = "v0.1.0-alpha";
        internal const string ManifestName = "runtime.json";
        internal const string RuntimeAssetName = "veyra-runtime.zip";
        private const string RepoUrl = "https://github.com/Artleboss2/Veyra";

        /// <summary>Pinned first so an installer always gets the image it was built for.</summary>
        internal static string ManifestPinned
        {
            get { return RepoUrl + "/releases/download/" + ReleaseTag + "/" + ManifestName; }
        }

        internal static string ManifestLatest
        {
            get { return RepoUrl + "/releases/latest/download/" + ManifestName; }
        }

        /// <summary>Size of the runtime the last manifest reported, for the window.</summary>
        internal static long RuntimeBytes;
        internal static string RuntimeVersion = "";
        internal static string LastManifestError = "";
        private static RuntimeManifest cachedManifest;

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

            string test = Value(args, "--test-install");
            if (test != null && test.Length > 0)
                return TestInstall(test, Value(args, "--zip"), Value(args, "--manifest"));

            if (Has(args, "--check"))
            {
                string outPath = Value(args, "--check-out");
                if (outPath == null || outPath.Length == 0) outPath = Path.Combine("build", "installer-check.json");
                return CheckPayload(outPath, Has(args, "--online"));
            }

            string shot = Value(args, "--shot");
            if (shot != null && shot.Length > 0) return RenderShot(shot);

            bool uninstall = Has(args, "--uninstall");
            Application.Run(new SetupForm(uninstall));
            return 0;
        }

        // ---------------- embedded application payload ----------------

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
            if (footerLength <= 0) throw new InvalidOperationException("This installer has no application payload appended.");
            FileStream fs = new FileStream(Application.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RangeStream range = new RangeStream(fs, footerOffset, footerLength);
            return new ZipArchive(range, ZipArchiveMode.Read, false);
        }

        internal static void ExtractAll(string dir, Action<double, string> report)
        {
            ExtractAll(dir, report, null);
        }

        internal static void ExtractAll(string dir, Action<double, string> report, Func<bool> cancelled)
        {
            using (ZipArchive zip = OpenPayload())
                ExtractZipInto(zip, dir, report, cancelled);
        }

        internal static void ExtractZipInto(ZipArchive zip, string dir, Action<double, string> report, Func<bool> cancelled)
        {
            string root = Path.GetFullPath(dir);
            if (!root.EndsWith("\\")) root += "\\";
            Directory.CreateDirectory(root);

            long total = 0, done = 0;
            for (int i = 0; i < zip.Entries.Count; i++) total += zip.Entries[i].Length;
            if (total <= 0) total = 1;

            byte[] buf = new byte[1024 * 1024];
            for (int i = 0; i < zip.Entries.Count; i++)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
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
                using (FileStream output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        output.Write(buf, 0, n);
                        done += n;
                    }
                }
                if (report != null)
                    report(Math.Min(0.999, (double)done / total), "Unpacking " + e.FullName + "\u2026");
            }
        }

        // ---------------- runtime manifest and download ----------------

        /// <summary>Resolves the manifest, pinned tag first then the latest release.</summary>
        internal static RuntimeManifest ResolveManifest(string overrideUrl, Action<string> log)
        {
            if (cachedManifest != null && string.IsNullOrEmpty(overrideUrl)) return cachedManifest;

            string[] urls = string.IsNullOrEmpty(overrideUrl)
                ? new string[] { ManifestPinned, ManifestLatest }
                : new string[] { overrideUrl };

            foreach (string url in urls)
            {
                try
                {
                    string json = Net.GetString(url, 20000);
                    RuntimeManifest m = RuntimeManifest.Parse(json);
                    if (m == null) throw new InvalidOperationException("malformed runtime manifest");
                    if (string.IsNullOrEmpty(m.Url)) m.Url = CombineDirectory(url, m.File);
                    RuntimeBytes = m.Bytes;
                    RuntimeVersion = m.Version;
                    if (log != null) log("manifest " + m.Version + " " + m.Bytes + " bytes from " + url);
                    cachedManifest = m;
                    return m;
                }
                catch (Exception ex)
                {
                    LastManifestError = ShortError(ex);
                    if (log != null) log("manifest " + url + " failed: " + ex.Message);
                }
            }
            if (log != null) log("no runtime manifest could be read");
            return null;
        }

        private static string CombineDirectory(string url, string file)
        {
            if (string.IsNullOrEmpty(file)) return url;
            int cut = url.LastIndexOf('/');
            return cut < 0 ? file : url.Substring(0, cut + 1) + file;
        }

        /// <summary>A short, non-localised reason for the window instead of a framework message.</summary>
        private static string ShortError(Exception ex)
        {
            WebException we = ex as WebException;
            if (we != null)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null) return "HTTP " + (int)resp.StatusCode;
                return we.Status.ToString();
            }
            return ex.GetType().Name;
        }

        /// <summary>Downloads (resumably), verifies and unpacks the runtime into dir.</summary>
        internal static void EnsureRuntime(string dir, RuntimeManifest m, Action<double, string> report, Func<bool> cancelled)
        {
            string zipPath = Path.Combine(Path.GetTempPath(), "veyra-" + m.Version + "-" + RuntimeAssetName);
            // Keep a partial file so a cancelled download resumes; drop it only if it is longer than the asset.
            if (File.Exists(zipPath) && new FileInfo(zipPath).Length > m.Bytes)
                TryDeleteFile(zipPath);

            Net.DownloadTo(m.Url, zipPath, m.Bytes, delegate(double f, string detail)
            {
                report(f * 0.78, "Downloading the Android runtime \u00b7 " + detail);
            }, cancelled);

            if (cancelled != null && cancelled()) throw new OperationCanceledException();

            string sha = Net.Sha256File(zipPath, delegate(double f)
            {
                report(0.78 + f * 0.10, "Verifying the download \u00b7 SHA-256");
            }, cancelled);

            if (!string.Equals(sha, m.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(zipPath);
                throw new InvalidOperationException("The runtime download failed verification. Press Retry to download it again.");
            }

            using (FileStream fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                ExtractZipInto(zip, dir, delegate(double f, string msg)
                {
                    report(0.88 + f * 0.12, msg);
                }, cancelled);
            }

            string iso = Path.Combine(dir, "runtime", "android", "android-x86.iso");
            string qemu = Path.Combine(dir, "runtime", "qemu", "qemu-system-x86_64.exe");
            if (!File.Exists(iso) || !File.Exists(qemu))
                throw new InvalidOperationException("The runtime archive is incomplete (missing Android image or QEMU).");

            TryDeleteFile(zipPath);
        }

        // ---------------- command line helpers ----------------

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

        private static int CheckPayload(string outPath, bool online)
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

                string manifestBlock = "\"manifestUrl\": \"" + ManifestPinned + "\"";
                bool onlineOk = false;
                if (online)
                {
                    List<string> log = new List<string>();
                    RuntimeManifest m = ResolveManifest(null, delegate(string s) { log.Add(s); });
                    if (m != null)
                    {
                        onlineOk = true;
                        manifestBlock = "\"manifestUrl\": \"" + m.Url + "\",\n  \"runtimeVersion\": \"" + m.Version +
                            "\",\n  \"runtimeBytes\": " + m.Bytes + ",\n  \"runtimeSha256\": \"" + m.Sha256 + "\"";
                    }
                    else
                    {
                        manifestBlock += ",\n  \"onlineError\": \"" + Escape(string.Join(" | ", log.ToArray())) + "\"";
                    }
                }

                string json = "{\n  \"ok\": " + (bytes > 0 ? "true" : "false") + ",\n  \"installerBytes\": " + bytes +
                              ",\n  \"entries\": " + entries + ",\n  \"unpackedBytes\": " + unpacked +
                              (online ? ",\n  \"online\": " + (onlineOk ? "true" : "false") : "") +
                              ",\n  " + manifestBlock +
                              ",\n  \"installDir\": \"" + Escape(InstallDir) + "\"\n}\n";
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

        /// <summary>Headless install into dir: no PATH, shortcut or registry writes.</summary>
        private static int TestInstall(string dir, string localZip, string manifestUrl)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(dir);
                StringBuilder log = new StringBuilder();
                Action<string> note = delegate(string s) { log.AppendLine(s); };

                ExtractAll(dir, null);
                note("application payload extracted");

                RuntimeManifest m = null;
                string sha;
                long bytes;

                if (!string.IsNullOrEmpty(localZip))
                {
                    bytes = new FileInfo(localZip).Length;
                    sha = Net.Sha256File(localZip, null, null);
                    note("local archive " + localZip);
                }
                else
                {
                    m = ResolveManifest(manifestUrl, note);
                    if (m == null) throw new InvalidOperationException("no runtime manifest reachable");
                    string zipPath = Path.Combine(Path.GetTempPath(), "veyra-test-" + RuntimeAssetName);
                    if (File.Exists(zipPath) && new FileInfo(zipPath).Length > m.Bytes) TryDeleteFile(zipPath);
                    Net.DownloadTo(m.Url, zipPath, m.Bytes, null, null);
                    bytes = new FileInfo(zipPath).Length;
                    sha = Net.Sha256File(zipPath, null, null);
                    if (bytes != m.Bytes) throw new InvalidOperationException("size mismatch: " + bytes + " != " + m.Bytes);
                    if (!string.Equals(sha, m.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("sha256 mismatch: " + sha + " != " + m.Sha256);
                    localZip = zipPath;
                    note("downloaded " + bytes + " bytes, sha256 matches the manifest");
                }

                using (FileStream fs = new FileStream(localZip, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    ExtractZipInto(zip, dir, null, null);

                string iso = Path.Combine(dir, "runtime", "android", "android-x86.iso");
                string qemu = Path.Combine(dir, "runtime", "qemu", "qemu-system-x86_64.exe");
                string adb = Path.Combine(dir, "runtime", "adb", "adb.exe");
                string exe = Path.Combine(dir, "Veyra.exe");
                foreach (string required in new string[] { iso, qemu, adb, exe, Path.Combine(dir, "Uninstall.exe") })
                    if (!File.Exists(required)) throw new InvalidOperationException("missing after install: " + required);
                if (new FileInfo(iso).Length < 900L * 1024 * 1024)
                    throw new InvalidOperationException("Android image is unexpectedly small");

                watch.Stop();
                string json = "{\n  \"ok\": true,\n  \"installDir\": \"" + Escape(Path.GetFullPath(dir)) +
                              "\",\n  \"runtimeBytes\": " + bytes + ",\n  \"runtimeSha256\": \"" + sha +
                              "\",\n  \"runtimeVersion\": \"" + (m != null ? m.Version : "local") +
                              "\",\n  \"elapsedSeconds\": " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") +
                              ",\n  \"isoBytes\": " + new FileInfo(iso).Length +
                              ",\n  \"log\": \"" + Escape(log.ToString()) + "\"\n}\n";
                File.WriteAllText(Path.Combine(dir, "install-test.json"), json, Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                watch.Stop();
                try { File.WriteAllText(Path.Combine(dir, "install-test.err"), ex.ToString()); } catch { }
                return 2;
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        }

        internal static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
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

    /// <summary>The runtime.json published next to the release asset.</summary>
    internal sealed class RuntimeManifest
    {
        public string Version = "";
        public string File = "";
        public string Url = "";
        public string Sha256 = "";
        public long Bytes;
        public int Entries;

        public static RuntimeManifest Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            RuntimeManifest m = new RuntimeManifest();
            m.Version = Str(json, "version");
            m.File = Str(json, "file");
            m.Url = Str(json, "url");
            m.Sha256 = Str(json, "sha256");
            m.Bytes = Num(json, "bytes");
            m.Entries = (int)Num(json, "entries");

            if (m.Sha256 == null || !Regex.IsMatch(m.Sha256, "^[0-9a-fA-F]{64}$")) return null;
            if (m.Bytes < 100L * 1024 * 1024) return null;
            if (string.IsNullOrEmpty(m.Version)) m.Version = "unknown";
            if (string.IsNullOrEmpty(m.File)) m.File = "veyra-runtime.zip";
            if (!string.IsNullOrEmpty(m.Url) && !m.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) m.Url = "";
            return m;
        }

        private static string Str(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static long Num(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([0-9]+)");
            long v;
            return m.Success && long.TryParse(m.Groups[1].Value, out v) ? v : 0;
        }
    }

    /// <summary>Small HTTP helper: manifest fetch, resumable download, SHA-256.</summary>
    internal static class Net
    {
        private const int BufferSize = 1024 * 1024;

        internal static void Init()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
        }

        private static HttpWebRequest Request(string url, int timeoutMs)
        {
            Init();
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "VeyraSetup/" + T.Version;
            req.Accept = "*/*";
            req.AutomaticDecompression = DecompressionMethods.None;
            req.AllowAutoRedirect = true;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = 60000;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            return req;
        }

        internal static string GetString(string url, int timeoutMs)
        {
            using (HttpWebResponse resp = (HttpWebResponse)Request(url, timeoutMs).GetResponse())
            using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                char[] buf = new char[4096];
                StringBuilder sb = new StringBuilder();
                int n;
                while (sb.Length < 256 * 1024 && (n = reader.Read(buf, 0, buf.Length)) > 0)
                    sb.Append(buf, 0, n);
                return sb.ToString();
            }
        }

        /// <summary>Downloads to path, resuming a partial file. Retries four times.</summary>
        internal static void DownloadTo(string url, string path, long expected, Action<double, string> report, Func<bool> cancelled)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            Exception last = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                if (attempt > 1) Thread.Sleep(1200 * (attempt - 1));

                long have = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (expected > 0 && have == expected) return;
                if (expected > 0 && have > expected)
                {
                    Program.TryDeleteFile(path);
                    have = 0;
                }

                try
                {
                    HttpWebRequest req = Request(url, 30000);
                    if (have > 0) req.AddRange(have);

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        bool append = resp.StatusCode == HttpStatusCode.PartialContent && have > 0;
                        if (!append) have = 0;
                        long total = expected > 0 ? expected : have + Math.Max(0, resp.ContentLength);

                        using (FileStream fs = new FileStream(path, append ? FileMode.Append : FileMode.Create,
                                   FileAccess.Write, FileShare.None, BufferSize))
                        using (Stream input = resp.GetResponseStream())
                        {
                            byte[] buf = new byte[BufferSize];
                            DateTime clock = DateTime.UtcNow;
                            long mark = have;
                            int n;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                                fs.Write(buf, 0, n);
                                have += n;

                                if (report != null && total > 0)
                                {
                                    double secs = (DateTime.UtcNow - clock).TotalSeconds;
                                    if (secs >= 0.4)
                                    {
                                        double rate = (have - mark) / secs / (1024.0 * 1024.0);
                                        clock = DateTime.UtcNow;
                                        mark = have;
                                        report(Math.Min(1.0, (double)have / total),
                                            string.Format("{0:0} / {1:0} MB \u00b7 {2:0.0} MB/s",
                                                have / 1048576.0, total / 1048576.0, rate));
                                    }
                                }
                            }
                        }
                        if (expected > 0 && have < expected)
                            throw new IOException("download stopped at " + have + " of " + expected + " bytes");
                        if (report != null) report(1.0, string.Format("{0:0} MB", have / 1048576.0));
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new IOException("Download failed: " + (last != null ? last.Message : "unknown error"), last);
        }

        internal static string Sha256File(string path, Action<double> progress, Func<bool> cancelled)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            using (SHA256 sha = SHA256.Create())
            {
                long length = fs.Length, done = 0;
                byte[] buf = new byte[BufferSize];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    sha.TransformBlock(buf, 0, n, buf, 0);
                    done += n;
                    if (progress != null && length > 0) progress((double)done / length);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    /// <summary>Minimal install/remove window, custom painted with the same design tokens as Veyra.</summary>
    internal class SetupForm : Form
    {
        private readonly bool uninstallMode;
        private volatile bool busy;
        private volatile bool cancelled;
        private bool done;
        private bool pendingClose;
        private double progress;
        private string status = "Ready.";
        private string error = "";
        private bool desktopShortcut = true;
        private string hoverId = "";
        private RuntimeManifest manifest;

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
            if (!uninstallMode) ProbeManifest();
        }

        /// <summary>Reads the manifest in the background so the window states the real download size.</summary>
        private void ProbeManifest()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    RuntimeManifest m = Program.ResolveManifest(null, null);
                    if (m == null) return;
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)delegate { manifest = m; Invalidate(); });
                }
                catch { }
            });
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
                "Installs Veyra, then downloads the Android 9 x86 runtime from the GitHub release and verifies it.",
                T.F(9f, false), T.TextSub, 64, 136);

            // Destination
            T.Write(g, "INSTALL LOCATION", T.F(7.4f, true), T.TextMuted, 64, 178);
            T.FillRound(g, new RectangleF(64, 196, ClientSize.Width - 128, 38), 4f, T.Panel);
            T.Stroke(g, new RectangleF(64, 196, ClientSize.Width - 128, 38), 4f, T.BorderSoft, 1f);
            T.WriteClip(g, Program.InstallDir, T.F(8.2f, false), T.TextSub,
                new RectangleF(78, 206, ClientSize.Width - 156, 20));

            long runtimeMb = manifest != null ? manifest.Bytes / (1024 * 1024) : Program.RuntimeBytes / (1024 * 1024);
            string facts = runtimeMb > 0
                ? string.Format("{0:N0} MB download \u00b7 {1:N0} MB on disk", runtimeMb, runtimeMb + 2)
                : "about 1.1 GB download on install";
            T.Write(g, facts + "  \u00b7  per-user install  \u00b7  uninstall from Settings \u2192 Apps",
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
            {
                T.FillRound(g, new RectangleF(64, py, Math.Max(4f, (float)((ClientSize.Width - 128) * Math.Min(1.0, progress))), 6),
                    1.5f, string.IsNullOrEmpty(error) ? T.Accent : T.Bad);

                string pct = string.Format("{0:0}%", progress * 100);
                Size sz = TextRenderer.MeasureText(pct, T.F(7.6f, false));
                T.Write(g, pct, T.F(7.6f, false), T.TextMuted, ClientSize.Width - 64 - sz.Width, py - 17);
            }
            T.Write(g, string.IsNullOrEmpty(error) ? status : error, T.F(8.2f, false),
                string.IsNullOrEmpty(error) ? T.TextSub : T.Bad, 65, py + 18);

            // Buttons
            float bw = 150, bh = 38;
            btnPrimary = new RectangleF(ClientSize.Width - 64 - bw, ClientSize.Height - 106, bw, bh);
            btnSecondary = new RectangleF(ClientSize.Width - 64 - bw * 2 - 12, ClientSize.Height - 106, bw, bh);

            if (busy)
            {
                DrawButton(g, btnSecondary, "Cancel", false, true);
                DrawButton(g, btnPrimary, uninstallMode ? "Uninstall" : "Install", true, false);
            }
            else if (!done)
            {
                DrawButton(g, btnSecondary, "Cancel", false, true);
                DrawButton(g, btnPrimary, uninstallMode ? "Uninstall" : "Install", true, true);
            }
            else if (!string.IsNullOrEmpty(error) && !uninstallMode)
            {
                DrawButton(g, btnSecondary, "Close", false, true);
                DrawButton(g, btnPrimary, "Retry", true, true);
            }
            else
            {
                DrawButton(g, btnSecondary, "Close", false, true);
                DrawButton(g, btnPrimary, "Launch Veyra", true, !uninstallMode && File.Exists(TargetExe));
            }

            T.Write(g, "Veyra " + T.Version + "  \u00b7  no telemetry  \u00b7  runtime verified with SHA-256",
                T.F(7.2f, false), T.TextMuted, 64, ClientSize.Height - 62);
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
            if (btnPrimary.Contains(x, y))
            {
                if (busy) return "";
                if (done) return !string.IsNullOrEmpty(error) && !uninstallMode ? "Retry" : (uninstallMode ? "Close" : "Launch Veyra");
                return uninstallMode ? "Uninstall" : "Install";
            }
            if (btnSecondary.Contains(x, y)) return "Cancel";
            if (!uninstallMode && !done && !busy && toggleRect.Contains(x, y)) return "desktop";
            return "";
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (btnSecondary.Contains(e.X, e.Y))
            {
                if (busy) { RequestCancel(); return; }
                Close();
                return;
            }
            if (!btnPrimary.Contains(e.X, e.Y) || busy) return;
            if (done && !string.IsNullOrEmpty(error) && !uninstallMode) { StartInstall(); return; }
            if (done) { Launch(); return; }
            if (toggleRect.Contains(e.X, e.Y) && !uninstallMode)
            {
                desktopShortcut = !desktopShortcut;
                Invalidate();
                return;
            }
            if (uninstallMode) StartUninstall();
            else StartInstall();
        }

        private void RequestCancel()
        {
            cancelled = true;
            status = "Cancelling\u2026";
            Invalidate();
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
            done = false;
            cancelled = false;
            error = "";
            progress = 0.02;
            status = "Preparing\u2026";
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Program.ExtractAll(Program.InstallDir, delegate(double fraction, string message)
                    {
                        Report(0.02 + fraction * 0.06, message);
                    }, delegate { return cancelled; });

                    if (!File.Exists(TargetExe))
                        throw new InvalidOperationException("The Veyra.exe payload is missing from the installer archive.");

                    Report(0.09, "Contacting GitHub\u2026");
                    RuntimeManifest m = manifest ?? Program.ResolveManifest(null, null);
                    if (m == null)
                        throw new InvalidOperationException(
                            "Runtime manifest unavailable (" +
                            (Program.LastManifestError.Length > 0 ? Program.LastManifestError : "network error") +
                            "). Press Retry.");

                    Program.EnsureRuntime(Program.InstallDir, m, delegate(double fraction, string message)
                    {
                        Report(0.10 + fraction * 0.85, message);
                    }, delegate { return cancelled; });

                    Report(0.96, "Adding bundled adb.exe to your PATH\u2026");
                    EnsureUserPath(Path.Combine(Program.InstallDir, "runtime", "adb"));

                    Report(0.97, "Creating shortcuts\u2026");
                    ShellShortcut.Create(StartMenuLnk, TargetExe, "", "Veyra \u2014 APK intelligence", Program.InstallDir);
                    if (desktopShortcut)
                        ShellShortcut.Create(DesktopLnk, TargetExe, "", "Veyra \u2014 APK intelligence", Program.InstallDir);

                    Report(0.99, "Registering in Apps & features\u2026");
                    WriteRegistry(m.Bytes);

                    Report(1.0, "Installed successfully \u00b7 runtime " + m.Version);
                    SafeDone(null);
                }
                catch (OperationCanceledException)
                {
                    SafeDone(new InvalidOperationException(
                        "Cancelled. Press Install to resume \u2014 the partial download is kept."));
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
            if (IsDisposed || !IsHandleCreated)
            {
                if (pendingClose) try { Close(); } catch { }
                return;
            }
            SafeInvoke(delegate
            {
                busy = false;
                done = true;
                if (ex != null)
                {
                    error = ex.Message;
                    progress = 0;
                }
                else
                {
                    progress = 1.0;
                    error = "";
                }
                Invalidate();
                if (pendingClose) Close();
            });
        }

        private void SafeInvoke(Action a)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        private void WriteRegistry(long runtimeBytes)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
            {
                if (k == null) throw new InvalidOperationException("Could not open the uninstall registry key.");
                long kb = (runtimeBytes + Program.PayloadLength) / 1024;
                k.SetValue("DisplayName", "Veyra");
                k.SetValue("DisplayVersion", T.Version);
                k.SetValue("Publisher", "Veyra");
                k.SetValue("DisplayIcon", TargetExe + ",0");
                k.SetValue("InstallLocation", Program.InstallDir);
                k.SetValue("UninstallString", "\"" + Path.Combine(Program.InstallDir, "Uninstall.exe") + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + Path.Combine(Program.InstallDir, "Uninstall.exe") + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                cancelled = true;
                pendingClose = true;
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>Read-only window over the appended application ZIP payload.</summary>
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
