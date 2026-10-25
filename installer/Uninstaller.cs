using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VeyraUninstall
{
    internal static class Program
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Veyra";

        [STAThread]
        private static int Main(string[] args)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            if (MessageBox.Show("Remove Veyra, its Android runtime and its settings from this PC?",
                    "Remove Veyra", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 0;

            try
            {
                string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Veyra.lnk");
                string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Veyra.lnk");
                try { if (File.Exists(start)) File.Delete(start); } catch { }
                try { if (File.Exists(desktop)) File.Delete(desktop); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false); } catch { }

                try
                {
                    string adbDir = Path.Combine(dir, @"runtime\adb");
                    using (RegistryKey env = Registry.CurrentUser.OpenSubKey("Environment", true))
                    {
                        if (env != null)
                        {
                            string current = (env.GetValue("Path") as string) ?? "";
                            System.Collections.Generic.List<string> keep = new System.Collections.Generic.List<string>();
                            foreach (string part in current.Split(';'))
                            {
                                if (part.Trim().Length == 0) continue;
                                if (string.Equals(part.Trim().TrimEnd('\\'), adbDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                                keep.Add(part.Trim());
                            }
                            env.SetValue("Path", string.Join(";", keep.ToArray()), RegistryValueKind.ExpandString);
                        }
                    }
                }
                catch { }

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c choice /t 2 /d y >nul & rmdir /s /q \"" + dir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not remove every file: " + ex.Message + "\n\nDelete " + dir + " manually.",
                    "Veyra", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
        }
    }
}
