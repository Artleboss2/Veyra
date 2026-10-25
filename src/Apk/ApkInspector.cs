using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Veyra
{
    internal class Verdict
    {
        public string Tier = "analysis";     // local-vm | bridge | incompatible | blocked
        public string Headline = "";
        public string Detail = "";
        public List<string> Reasons = new List<string>();
        public int Demand = 10;
        public Color Tone = T.Warn;
        public bool CanLaunch = false;
        public bool VmCompatible = false;
    }

    internal class ApkInfo
    {
        public string Path = "";
        public string FileName = "";
        public string PackageName = "";
        public string VersionName = "";
        public string Label = "";
        public string MainActivity = "";
        public string Fingerprint = "";
        public int VersionCode;
        public int MinSdk = -1;
        public int TargetSdk = -1;
        public int CompileSdk = -1;
        public long SizeBytes;
        public Bitmap Icon;
        public bool IconFromArchive;
        public string Signature = "";
        public string ManifestMode = "";
        public bool HasManifest;
        public bool HasResources;
        public bool Debuggable;
        public bool NativeArm;
        public bool NativeX86;

        public List<string> Permissions = new List<string>();
        public List<string> Activities = new List<string>();
        public List<string> Abis = new List<string>();
        public List<string> Frameworks = new List<string>();
        public List<string> DexFiles = new List<string>();
        public List<string> NotableClasses = new List<string>();
        public List<string> SignatureFiles = new List<string>();

        public int DexCount, ClassCount, MethodCount, FieldCount, StringCount, EntryCount;
        public string Error = "";
        public Verdict Verdict;

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Label)) return Label;
                if (!string.IsNullOrEmpty(PackageName))
                {
                    string[] parts = PackageName.Split('.');
                    string last = parts[parts.Length - 1];
                    if (last.Length > 0) return char.ToUpperInvariant(last[0]) + last.Substring(1);
                }
                return "Unknown application";
            }
        }

        public string Initials
        {
            get
            {
                string n = DisplayName;
                if (string.IsNullOrEmpty(n)) return "AP";
                string[] parts = n.Split(new char[] { ' ', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) return ("" + parts[0][0] + parts[1][0]).ToUpperInvariant();
                return n.Substring(0, Math.Min(2, n.Length)).ToUpperInvariant();
            }
        }
    }

    internal static class ApkInspector
    {
        public const int ATTR_LABEL = Axml.ATTR_LABEL;
        public const int ATTR_ICON = Axml.ATTR_ICON;

        private static readonly string[] DensityRank = new string[]
        {
            "xxxhdpi", "xxhdpi", "xhdpi", "hdpi", "mdpi", "ldpi", "nodpi", "anydpi"
        };

        public static ApkInfo Inspect(string path, Action<string> log)
        {
            ApkInfo a = new ApkInfo();
            a.Path = path;
            a.FileName = System.IO.Path.GetFileName(path);
            try
            {
                FileInfo fi = new FileInfo(path);
                a.SizeBytes = fi.Length;
            }
            catch { }

            log("Opening archive \u00b7 " + a.FileName);
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read, false))
                {
                    a.EntryCount = zip.Entries.Count;
                    log("Archive parsed \u00b7 " + a.EntryCount + " entries, " + T.Bytes(a.SizeBytes));

                    // ---- manifest ----
                    ZipArchiveEntry man = Find(zip, "AndroidManifest.xml");
                    Axml xml = null;
                    if (man != null)
                    {
                        byte[] mb = ReadAll(man);
                        a.HasManifest = true;
                        if (mb.Length > 4 && mb[0] == 0x03 && mb[1] == 0x00)
                        {
                            a.ManifestMode = "binary AXML";
                            try { xml = new Axml(mb); }
                            catch (Exception ex) { log("Manifest parse warning: " + ex.Message); }
                        }
                        else
                        {
                            a.ManifestMode = "plain text";
                            log("Manifest is not compiled AXML \u2014 skipping structured parse.");
                        }
                    }
                    else log("No AndroidManifest.xml found \u2014 not a standard APK.");

                    // ---- resources ----
                    Arsc arsc = null;
                    ZipArchiveEntry resEntry = Find(zip, "resources.arsc");
                    if (resEntry != null)
                    {
                        try
                        {
                            arsc = new Arsc(ReadAll(resEntry));
                            a.HasResources = true;
                            log("Resource table parsed \u00b7 " + arsc.StringCount + " strings, " + arsc.TypeCount + " types");
                        }
                        catch (Exception ex) { log("Resource table skipped: " + ex.Message); }
                    }

                    int iconResId = 0;
                    if (xml != null) ReadManifest(a, xml, arsc, log, out iconResId);

                    // ---- dex ----
                    for (int i = 0; i < zip.Entries.Count; i++)
                    {
                        string name = zip.Entries[i].FullName;
                        if (!IsDex(name)) continue;
                        try
                        {
                            DexInfo di = DexReader.Read(zip.Entries[i].Open(), name);
                            a.DexFiles.Add(name + "  \u00b7  " + T.Bytes(di.Size));
                            a.DexCount++;
                            a.ClassCount += di.ClassCount;
                            a.MethodCount += di.MethodCount;
                            a.FieldCount += di.FieldCount;
                            a.StringCount += di.StringCount;
                            if (a.NotableClasses.Count < 60)
                            {
                                for (int k = 0; k < di.ClassNames.Count && a.NotableClasses.Count < 60; k++)
                                {
                                    string c = di.ClassNames[k];
                                    if (IsNotable(c)) a.NotableClasses.Add(c);
                                }
                            }
                            log("DEX " + name + " \u00b7 v" + di.Version + " \u00b7 " + di.ClassCount + " classes, " + di.MethodCount + " methods");
                        }
                        catch (Exception ex) { log("DEX " + name + " unreadable: " + ex.Message); }
                    }
                    if (a.DexCount == 0) log("No classes.dex found \u2014 unusual package.");

                    // ---- native + signature + icon ----
                    List<string> iconCandidates = new List<string>();
                    for (int i = 0; i < zip.Entries.Count; i++)
                    {
                        string name = zip.Entries[i].FullName;
                        string lower = name.ToLowerInvariant();
                        if (lower.StartsWith("lib/"))
                        {
                            string[] seg = name.Split('/');
                            if (seg.Length >= 3)
                            {
                                string abi = seg[1];
                                if (!a.Abis.Contains(abi)) a.Abis.Add(abi);
                                string so = seg[seg.Length - 1].ToLowerInvariant();
                                DetectFramework(a, so);
                                if (abi.StartsWith("arm")) a.NativeArm = true;
                                if (abi.StartsWith("x86")) a.NativeX86 = true;
                            }
                        }
                        if (lower.StartsWith("assets/bin/data") || lower.StartsWith("assets/www/"))
                            DetectFramework(a, lower);
                        if (lower.StartsWith("meta-inf/") && (lower.EndsWith(".rsa") || lower.EndsWith(".dsa") || lower.EndsWith(".ec")))
                            a.SignatureFiles.Add(name);
                        if (IsIconCandidate(lower)) iconCandidates.Add(name);
                    }

                    a.Abis.Sort();
                    a.Signature = a.SignatureFiles.Count > 0
                        ? "v1 certificate present (" + System.IO.Path.GetFileName(a.SignatureFiles[0]) + ")"
                        : "No v1 certificate file (v2/v3 scheme or unsigned)";

                    // ---- icon ----
                    if (arsc != null && iconResId != 0)
                    {
                        string iconPath = arsc.ResolveFile(iconResId);
                        if (!string.IsNullOrEmpty(iconPath))
                        {
                            ZipArchiveEntry ie = Find(zip, iconPath);
                            if (ie != null)
                            {
                                a.Icon = Decode(ReadAll(ie));
                                a.IconFromArchive = a.Icon != null;
                                if (a.Icon != null) log("Icon resolved from " + iconPath);
                            }
                        }
                    }
                    if (a.Icon == null) a.Icon = HeuristicIcon(zip, iconCandidates, log);

                    // ---- fingerprint ----
                    try
                    {
                        fs.Position = 0;
                        using (SHA256 sha = SHA256.Create())
                        {
                            byte[] h = sha.ComputeHash(fs);
                            StringBuilder sb = new StringBuilder();
                            for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                            a.Fingerprint = sb.ToString();
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                a.Error = ex.GetType().Name + ": " + ex.Message;
                log("Inspection failed \u2014 " + a.Error);
            }

            a.Verdict = BuildVerdict(a, log);
            return a;
        }

        private static void ReadManifest(ApkInfo a, Axml xml, Arsc arsc, Action<string> log, out int iconResId)
        {
            iconResId = 0;
            AxmlElement root = xml.Root;
            if (root == null) return;

            AxmlAttr pkg = root.Attr("package");
            if (pkg != null) a.PackageName = pkg.Raw;
            AxmlAttr vc = root.Attr(Axml.ATTR_VERSION_CODE);
            if (vc != null) a.VersionCode = vc.IntValue;
            AxmlAttr vn = root.Attr(Axml.ATTR_VERSION_NAME);
            if (vn != null) a.VersionName = vn.Raw;
            AxmlAttr cs = root.Attr(Axml.ATTR_COMPILE_SDK);
            if (cs != null) a.CompileSdk = cs.IntValue;

            List<string> launcherActions = new List<string>();
            List<string> launcherCategories = new List<string>();

            foreach (AxmlElement el in xml.Walk())
            {
                if (el.Name == "uses-sdk")
                {
                    AxmlAttr min = el.Attr(Axml.ATTR_MIN_SDK);
                    if (min != null) a.MinSdk = min.IntValue;
                    AxmlAttr tgt = el.Attr(Axml.ATTR_TARGET_SDK);
                    if (tgt != null) a.TargetSdk = tgt.IntValue;
                    else if (min != null) a.TargetSdk = min.IntValue;
                }
                else if (el.Name == "uses-permission")
                {
                    AxmlAttr n = el.Attr(Axml.ATTR_NAME);
                    if (n != null && !a.Permissions.Contains(n.Raw)) a.Permissions.Add(n.Raw);
                }
                else if (el.Name == "application")
                {
                    AxmlAttr label = el.Attr(Axml.ATTR_LABEL);
                    a.Label = ResolveLabel(label, arsc);
                    AxmlAttr icon = el.Attr(Axml.ATTR_ICON);
                    if (icon != null && icon.Type == 0x01) iconResId = icon.Data;
                    AxmlAttr dbg = el.Attr(Axml.ATTR_DEBUGGABLE);
                    if (dbg != null) a.Debuggable = dbg.IntValue != 0;
                }
                else if (el.Name == "activity" || el.Name == "activity-alias")
                {
                    AxmlAttr n = el.Attr(Axml.ATTR_NAME);
                    if (n != null && n.Raw.Length > 0 && !a.Activities.Contains(n.Raw)) a.Activities.Add(n.Raw);

                    bool isLauncher = false;
                    foreach (AxmlElement child in el.Children)
                    {
                        if (child.Name != "intent-filter") continue;
                        bool main = false, launcher = false;
                        foreach (AxmlElement sub in child.Children)
                        {
                            AxmlAttr an = sub.Attr(Axml.ATTR_NAME);
                            if (an == null) continue;
                            if (sub.Name == "action" && an.Raw == "android.intent.action.MAIN") main = true;
                            if (sub.Name == "category" && an.Raw == "android.intent.category.LAUNCHER") launcher = true;
                        }
                        if (main && launcher) isLauncher = true;
                    }
                    if (isLauncher && n != null)
                        a.MainActivity = NormalizeComponent(n.Raw, a.PackageName);
                }
            }

            if (string.IsNullOrEmpty(a.Label))
            {
                AxmlAttr appLabel = null;
                foreach (AxmlElement el in xml.Walk())
                    if (el.Name == "application") { appLabel = el.Attr(Axml.ATTR_LABEL); break; }
                a.Label = ResolveLabel(appLabel, arsc);
            }
            if (string.IsNullOrEmpty(a.Label)) a.Label = "";
            if (string.IsNullOrEmpty(a.MainActivity) && a.Activities.Count > 0) a.MainActivity = a.Activities[0];

            log("Manifest \u00b7 " + (a.PackageName.Length > 0 ? a.PackageName : "package unknown")
                + (a.VersionName.Length > 0 ? " v" + a.VersionName : "")
                + (a.MinSdk > 0 ? " \u00b7 minSdk " + a.MinSdk : "")
                + (a.TargetSdk > 0 ? " \u00b7 targetSdk " + a.TargetSdk : ""));
        }

        private static string NormalizeComponent(string name, string package)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.StartsWith(".")) return package + name;
            if (name.IndexOf('.') < 0) return package + "." + name;
            return name;
        }

        private static string ResolveLabel(AxmlAttr label, Arsc arsc)
        {
            if (label == null) return "";
            if (label.Type == 0x01 && arsc != null)
            {
                string s = arsc.ResolveString(label.Data);
                if (!string.IsNullOrEmpty(s)) return s;
                return "";
            }
            string raw = label.Raw;
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.StartsWith("@0x") || raw.StartsWith("@"))
            {
                string hex = raw.StartsWith("@0x") ? raw.Substring(3) : raw.Substring(1);
                int id;
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out id) && arsc != null)
                    return arsc.ResolveString(id);
                return "";
            }
            if (raw.StartsWith("?") || raw.StartsWith("@string/")) return "";
            return raw;
        }

        private static bool IsDex(string name)
        {
            string lower = name.ToLowerInvariant();
            return lower.EndsWith(".dex") && lower.IndexOf('/') < 0;
        }

        private static bool IsNotable(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            if (cls.StartsWith("android.") || cls.StartsWith("androidx.") || cls.StartsWith("kotlin.")
                || cls.StartsWith("java.") || cls.StartsWith("javax.") || cls.StartsWith("dalvik.")) return false;
            if (cls.StartsWith("com.android.") || cls.StartsWith("com.google.android.")) return false;
            return true;
        }

        private static void DetectFramework(ApkInfo a, string name)
        {
            string add = null;
            if (name.IndexOf("libflutter") >= 0) add = "Flutter engine";
            else if (name.IndexOf("libunity") >= 0 || name.IndexOf("libil2cpp") >= 0 || name.StartsWith("assets/bin/data")) add = "Unity (IL2CPP)";
            else if (name.IndexOf("libreactnative") >= 0 || name.IndexOf("libhermes") >= 0) add = "React Native";
            else if (name.IndexOf("libmonodroid") >= 0 || name.IndexOf("libmonosgen") >= 0) add = ".NET for Android";
            else if (name.IndexOf("libgodot") >= 0) add = "Godot engine";
            else if (name.IndexOf("libcocos") >= 0) add = "Cocos2d-x";
            else if (name.StartsWith("assets/www/")) add = "Cordova / Capacitor web app";
            if (add != null && !a.Frameworks.Contains(add)) a.Frameworks.Add(add);
        }

        private static bool IsIconCandidate(string lower)
        {
            if (!(lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg"))) return false;
            if (lower.IndexOf("/mipmap") < 0 && lower.IndexOf("/drawable") < 0) return false;
            string file = lower.Substring(lower.LastIndexOf('/') + 1);
            return file.IndexOf("ic_launcher") >= 0 || file.IndexOf("icon") >= 0 || file.IndexOf("logo") >= 0;
        }

        private static Bitmap HeuristicIcon(ZipArchive zip, List<string> candidates, Action<string> log)
        {
            candidates.Sort(delegate (string x, string y)
            {
                return Rank(y).CompareTo(Rank(x));
            });
            for (int i = 0; i < candidates.Count && i < 6; i++)
            {
                try
                {
                    ZipArchiveEntry e = Find(zip, candidates[i]);
                    if (e == null) continue;
                    Bitmap bmp = Decode(ReadAll(e));
                    if (bmp != null)
                    {
                        log("Icon recovered by name scan \u00b7 " + candidates[i]);
                        return bmp;
                    }
                }
                catch { }
            }
            return null;
        }

        private static int Rank(string path)
        {
            string lower = path.ToLowerInvariant();
            for (int i = 0; i < DensityRank.Length; i++)
                if (lower.IndexOf(DensityRank[i]) >= 0) return DensityRank.Length - i;
            if (lower.IndexOf("ic_launcher") >= 0) return 1;
            return 0;
        }

        private static Bitmap Decode(byte[] bytes)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image img = Image.FromStream(ms))
                {
                    Bitmap bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
                    return bmp;
                }
            }
            catch { return null; }
        }

        private static ZipArchiveEntry Find(ZipArchive zip, string name)
        {
            for (int i = 0; i < zip.Entries.Count; i++)
                if (string.Equals(zip.Entries[i].FullName, name, StringComparison.OrdinalIgnoreCase))
                    return zip.Entries[i];
            return null;
        }

        private static byte[] ReadAll(ZipArchiveEntry e)
        {
            using (Stream s = e.Open())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------
        // Verdict: computed from evidence in the package, never guessed.
        // ------------------------------------------------------------------
        private static Verdict BuildVerdict(ApkInfo a, Action<string> log)
        {
            Verdict v = new Verdict();
            string adb = Hardware.FindAdb();
            bool vm = LocalVm.ComponentsPresent();
            bool hasX86 = false;
            for (int i = 0; i < a.Abis.Count; i++)
            {
                string abi = a.Abis[i].ToLowerInvariant();
                if (abi == "x86" || abi == "x86_64") hasX86 = true;
            }
            bool armOnly = a.NativeArm && !hasX86;
            bool tooNew = a.MinSdk > 28;

            int demand = 12;
            if (a.ClassCount > 2000) demand += 12;
            if (a.ClassCount > 10000) demand += 13;
            if (a.ClassCount > 50000) demand += 15;
            if (a.MethodCount > 12000) demand += 8;
            if (a.MethodCount > 60000) demand += 12;
            demand += Math.Min(30, a.Frameworks.Count * 15);
            if (a.NativeArm) demand += 12;
            if (a.SizeBytes > 60L * 1024 * 1024) demand += 10;
            if (a.TargetSdk >= 33) demand += 4;
            v.Demand = Math.Max(4, Math.Min(100, demand));

            if (!string.IsNullOrEmpty(a.Error))
            {
                v.Tier = "blocked";
                v.Tone = T.Bad;
                v.Headline = "Package could not be read";
                v.Detail = a.Error;
                v.Reasons.Add("The archive is corrupt, encrypted, or not a valid APK.");
                return v;
            }

            if (!a.HasManifest)
            {
                v.Tier = "blocked";
                v.Tone = T.Bad;
                v.Headline = "No Android manifest";
                v.Detail = "This ZIP has no AndroidManifest.xml, so it is not an installable Android package.";
                v.Reasons.Add("AndroidManifest.xml missing.");
                return v;
            }

            if (a.NativeArm)
            {
                v.Reasons.Add("Ships ARM native libraries (" + Join(a.Abis) + "). The local VM is x86_64 and cannot load ARM machine code without a translation layer.");
            }
            else if (a.Abis.Count > 0)
            {
                v.Reasons.Add("Ships x86/x86_64 native libraries (" + Join(a.Abis) + "); the local VM can load these.");
            }
            else
            {
                v.Reasons.Add("No native libraries \u2014 pure Dalvik bytecode that the local Android 9 VM can interpret.");
            }

            if (a.Frameworks.Count > 0)
                v.Reasons.Add("Engine stack detected: " + Join(a.Frameworks) + ". Its own x86 runtime libraries must be present or the app will abort at startup.");

            if (a.MinSdk > 0)
                v.Reasons.Add("Requires API " + a.MinSdk + (a.TargetSdk > 0 ? " (targets API " + a.TargetSdk + ")" : "") + (tooNew ? "; the bundled Android 9 VM is API 28 and will refuse it." : "; Android 9 / API 28 satisfies this."));

            if (a.DexCount > 1)
                v.Reasons.Add(a.DexCount + " multidex files, " + a.ClassCount + " classes and " + a.MethodCount + " methods \u2014 a large code surface to interpret.");

            if (a.Debuggable)
                v.Reasons.Add("Debuggable build: adb and inspection tools can attach without signature restrictions.");

            if (a.Permissions.Count > 0)
                v.Reasons.Add(a.Permissions.Count + " declared permission(s), including " + Join(Take(a.Permissions, 3)) + ".");

            if (tooNew)
            {
                v.Tier = "incompatible";
                v.Tone = T.Warn;
                v.Headline = "Needs Android " + a.MinSdk + " \u2014 newer than the VM";
                v.Detail = "The bundled Android-x86 machine is Android 9 (API 28). This package refuses to install on it" +
                    (adb != null ? "; a modern phone over ADB can still run it." : ".");
                v.CanLaunch = false;
                v.VmCompatible = false;
                v.Reasons.Add("Run it on a device with API " + a.MinSdk + " or newer, or rebuild the VM image on a newer Android base.");
            }
            else if (armOnly)
            {
                v.Tier = "incompatible";
                v.Tone = T.Warn;
                v.Headline = "ARM-only - not runnable in the x86 VM";
                v.Detail = "Only ARM native code is present. Android on x86 will reject this with INSTALL_FAILED_NO_MATCHING_ABIS" +
                    (adb != null ? "; a real ARM phone over ADB can run it." : ".");
                v.CanLaunch = false;
                v.VmCompatible = false;
                v.Reasons.Add("Use the Phone action with an ARM device; machine code cannot be translated by Veyra.");
            }
            else if (vm)
            {
                v.Tier = "local-vm";
                v.Tone = T.Aqua;
                v.Headline = "Ready for the local Android 9 VM";
                v.Detail = "Veyra can install this into its bundled x86_64 Android machine and launch the main activity. Google Play Services are not included.";
                v.CanLaunch = true;
                v.VmCompatible = true;
                v.Reasons.Add("Code and SDK level match the VM; install and launch happen locally over ADB.");
                if (a.Frameworks.Count > 0)
                    v.Reasons.Add("If the app needs Google Play Services, sign-in or SafetyNet, expect it to fail inside this image.");
            }
            else if (adb != null)
            {
                v.Tier = "bridge";
                v.Tone = T.Aqua;
                v.Headline = "Bridge available \u2014 run it on a real device";
                v.Detail = "adb was found at " + adb + ". Connect an Android phone with USB debugging on, then Veyra can install and launch this APK there with 50\u2013100 MB of PC memory.";
                v.CanLaunch = true;
                v.Reasons.Add("Recommended path: ADB device bridge \u2014 zero emulation, the phone's own hardware runs the app.");
            }
            else
            {
                v.Tier = "analysis";
                v.Tone = T.Warn;
                v.Headline = "Runtime components are missing";
                v.Detail = "Install the bundled runtime to run this package locally, or connect an Android device over ADB.";
                v.CanLaunch = false;
                v.Reasons.Add("No VM runtime and no adb found. Run VeyraSetup.exe or point Settings at adb.exe.");
            }

            v.Reasons.Add("Package is fully parseable here: manifest, resource table, DEX and ABI audit completed locally.");
            log("Verdict \u00b7 demand " + v.Demand + "/100 \u00b7 tier " + v.Tier);
            return v;
        }

        private static string Join(List<string> items)
        {
            return string.Join(", ", items.ToArray());
        }

        private static List<string> Take(List<string> items, int n)
        {
            List<string> r = new List<string>();
            for (int i = 0; i < items.Count && i < n; i++)
                r.Add(items[i].Replace("android.permission.", ""));
            return r;
        }
    }
}
