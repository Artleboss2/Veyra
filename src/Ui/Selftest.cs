using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace Veyra
{
    /// <summary>Headless verification pass used by the build pipeline. Exit 0 = all checks passed.</summary>
    internal static class Selftest
    {
        public static int Run(string apkPath, string outPath)
        {
            List<string> errors = new List<string>();
            List<string> checks = new List<string>();
            SysInfo sys = null;
            ApkInfo apk = null;
            Profile prof = null;
            int procMb = 0;
            bool rendering = false;

            try
            {
                sys = Hardware.Scan();
                if (sys.RamTotalMb <= 0) errors.Add("RAM probe returned 0 MB");
                else checks.Add("ram:" + sys.RamTotalMb + "MB");
                if (string.IsNullOrEmpty(sys.CpuName) || sys.CpuName == "Unknown CPU") errors.Add("CPU name not detected");
                else checks.Add("cpu:" + sys.CpuName);
                if (string.IsNullOrEmpty(sys.GpuName) || sys.GpuName == "Unknown GPU") errors.Add("GPU not detected");
                else checks.Add("gpu:" + sys.GpuName);
                if (sys.DiskTotalGb <= 0) errors.Add("Disk probe failed");
                else checks.Add("disk:" + sys.DiskFreeGb + "GB free");

                prof = Profile.Compute(sys);
                checks.Add("profile:" + prof.Name + " " + prof.RenderW + "x" + prof.RenderH + "@" + prof.FpsCap);
                if (prof.FpsCap <= 0 || prof.FpsCap > 60) errors.Add("Profile frame cap out of range");

                // Rendering pipeline: gradient bed, rounded path and text metrics, without a window.
                using (Bitmap bmp = new Bitmap(960, 600, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        Bitmap bg = Backdrop.Get(960, 600);
                        if (bg == null) errors.Add("Backdrop render returned null");
                        else
                        {
                            g.DrawImageUnscaled(bg, 0, 0);
                            T.FillRound(g, new RectangleF(10, 10, 200, 80), 16f, Color.FromArgb(120, 255, 255, 255));
                            T.Write(g, "Veyra self-test", T.F(12f, true), Color.White, 24, 24);
                            rendering = true;
                            checks.Add("render:gradient+rounded+text");
                        }
                    }
                    // Confirm pixels were actually painted.
                    Color px = bmp.GetPixel(480, 300);
                    if (px.A == 0) errors.Add("Backdrop painted fully transparent");
                    Color topLeft = bmp.GetPixel(40, 40), bottomRight = bmp.GetPixel(900, 540);
                    if (topLeft.ToArgb() == bottomRight.ToArgb()) errors.Add("Backdrop is a flat colour, gradient missing");
                }

                if (!string.IsNullOrEmpty(sys.Java) && sys.Java != "not detected") checks.Add("java:" + Short(sys.Java));

                if (!string.IsNullOrEmpty(apkPath))
                {
                    List<string> ilog = new List<string>();
                    if (!File.Exists(apkPath)) errors.Add("APK not found: " + apkPath);
                    else
                    {
                        apk = ApkInspector.Inspect(apkPath, delegate (string m) { if (ilog.Count < 200) ilog.Add(m); });
                        if (!string.IsNullOrEmpty(apk.Error)) errors.Add("APK inspect error: " + apk.Error);
                        if (!apk.HasManifest) errors.Add("Manifest not detected");
                        if (string.IsNullOrEmpty(apk.PackageName)) errors.Add("Package name not extracted");
                        if (apk.Verdict == null) errors.Add("Verdict not produced");
                        if (apk.ClassCount <= 0) errors.Add("No DEX classes parsed");
                        if (apk.Icon == null) checks.Add("apk:icon-fallback (no decodable raster icon)");
                        else checks.Add("apk:icon " + apk.Icon.Width + "x" + apk.Icon.Height);
                        checks.Add("apk:package=" + apk.PackageName);
                        checks.Add("apk:label=" + apk.DisplayName);
                        checks.Add("apk:minSdk=" + apk.MinSdk + " targetSdk=" + apk.TargetSdk);
                        checks.Add("apk:dex=" + apk.DexCount + " classes=" + apk.ClassCount);
                        checks.Add("apk:abis=" + (apk.Abis.Count == 0 ? "none" : string.Join(",", apk.Abis.ToArray())));
                        checks.Add("apk:verdict=" + apk.Verdict.Tier + " demand=" + apk.Verdict.Demand);
                    }
                }

                Hardware.UpdateLive(sys);
                procMb = (int)sys.ProcWorkingSetMb;
                if (procMb > 500) errors.Add("Working set " + procMb + " MB exceeds 500 MB ceiling");
                else checks.Add("memory:" + procMb + "MB");
            }
            catch (Exception ex)
            {
                errors.Add(ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                StringBuilder j = new StringBuilder();
                j.Append("{\n");
                j.Append("  \"ok\": ").Append(errors.Count == 0 ? "true" : "false").Append(",\n");
                j.Append("  \"version\": \"" + MainForm.Version + "\",\n");
                j.Append("  \"checks\": ").Append(JsonArray(checks)).Append(",\n");
                j.Append("  \"errors\": ").Append(JsonArray(errors)).Append(",\n");
                j.Append("  \"rendering\": ").Append(rendering ? "true" : "false").Append(",\n");
                j.Append("  \"processMb\": ").Append(procMb).Append(",\n");
                if (sys != null)
                {
                    j.Append("  \"host\": {\n");
                    j.Append("    \"cpu\": ").Append(JsonStr(sys.CpuName)).Append(",\n");
                    j.Append("    \"cores\": ").Append(sys.Cores).Append(",\n");
                    j.Append("    \"threads\": ").Append(sys.Threads).Append(",\n");
                    j.Append("    \"mhz\": ").Append(sys.Mhz).Append(",\n");
                    j.Append("    \"ramTotalMb\": ").Append(sys.RamTotalMb).Append(",\n");
                    j.Append("    \"ramFreeMb\": ").Append(sys.RamFreeMb).Append(",\n");
                    j.Append("    \"gpu\": ").Append(JsonStr(sys.GpuName)).Append(",\n");
                    j.Append("    \"gpuDriver\": ").Append(JsonStr(sys.GpuDriver)).Append(",\n");
                    j.Append("    \"os\": ").Append(JsonStr(sys.OsName + " " + sys.OsBuild)).Append(",\n");
                    j.Append("    \"diskFreeGb\": ").Append(sys.DiskFreeGb).Append(",\n");
                    j.Append("    \"java\": ").Append(JsonStr(sys.Java)).Append(",\n");
                    j.Append("    \"adb\": ").Append(JsonStr(sys.Adb == null ? "" : sys.Adb)).Append(",\n");
                    j.Append("    \"scanMs\": ").Append(sys.ScanMs).Append("\n");
                    j.Append("  },\n");
                }
                if (prof != null)
                {
                    j.Append("  \"profile\": { \"name\": ").Append(JsonStr(prof.Name))
                     .Append(", \"width\": ").Append(prof.RenderW)
                     .Append(", \"height\": ").Append(prof.RenderH)
                     .Append(", \"fps\": ").Append(prof.FpsCap)
                     .Append(", \"threads\": ").Append(prof.ScanThreads)
                     .Append(", \"budgetMb\": ").Append(prof.BudgetMb).Append(" },\n");
                }
                j.Append("  \"apk\": ");
                if (apk == null) j.Append("null");
                else j.Append(ApkJson(apk));
                j.Append("\n}\n");

                string path = string.IsNullOrEmpty(outPath) ? "veyra-selftest.json" : outPath;
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, j.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                errors.Add("report write failed: " + ex.Message);
            }

            return errors.Count == 0 ? 0 : 2;
        }

        private static string ApkJson(ApkInfo a)
        {
            StringBuilder s = new StringBuilder();
            s.Append("{");
            s.Append("\"file\":").Append(JsonStr(a.FileName));
            s.Append(",\"package\":").Append(JsonStr(a.PackageName));
            s.Append(",\"label\":").Append(JsonStr(a.DisplayName));
            s.Append(",\"versionName\":").Append(JsonStr(a.VersionName));
            s.Append(",\"versionCode\":").Append(a.VersionCode);
            s.Append(",\"minSdk\":").Append(a.MinSdk);
            s.Append(",\"targetSdk\":").Append(a.TargetSdk);
            s.Append(",\"dexCount\":").Append(a.DexCount);
            s.Append(",\"classCount\":").Append(a.ClassCount);
            s.Append(",\"methodCount\":").Append(a.MethodCount);
            s.Append(",\"sizeBytes\":").Append(a.SizeBytes);
            s.Append(",\"abis\":").Append(JsonArray(a.Abis));
            s.Append(",\"frameworks\":").Append(JsonArray(a.Frameworks));
            s.Append(",\"permissions\":").Append(JsonArray(a.Permissions));
            s.Append(",\"mainActivity\":").Append(JsonStr(a.MainActivity));
            s.Append(",\"iconDecoded\":").Append(a.Icon != null ? "true" : "false");
            s.Append(",\"verdictTier\":").Append(JsonStr(a.Verdict == null ? "" : a.Verdict.Tier));
            s.Append(",\"verdictDemand\":").Append(a.Verdict == null ? 0 : a.Verdict.Demand);
            s.Append(",\"error\":").Append(JsonStr(a.Error));
            s.Append("}");
            return s.ToString();
        }

        private static string JsonArray(List<string> items)
        {
            StringBuilder s = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) s.Append(", ");
                s.Append(JsonStr(items[i]));
            }
            s.Append("]");
            return s.ToString();
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "null";
            StringBuilder b = new StringBuilder("\"");
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            b.Append("\"");
            return b.ToString();
        }

        private static string Short(string s)
        {
            return s.Length <= 60 ? s : s.Substring(0, 60);
        }
    }
}
