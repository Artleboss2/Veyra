using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Veyra
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static int Bench(string apk)
        {
            try
            {
                using (MainForm f = new MainForm(""))
                {
                    f.SuppressBoot = true;
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new System.Drawing.Point(40, 40);
                    f.Size = new System.Drawing.Size(1240, 820);
                    f.PrepareForShot(apk, "dashboard");
                    f.Show();
                    Application.DoEvents();
                    for (int i = 0; i < 3; i++) { f.Invalidate(); f.Update(); Application.DoEvents(); }

                    double rebuild = 0, build = 0;
                    for (int i = 0; i < 6; i++)
                    {
                        f.ForceFullPaint();
                        f.Invalidate();
                        f.Update();
                        Application.DoEvents();
                        rebuild += MainForm.LastPaintMs;
                        build += MainForm.LastBuildMs;
                    }

                    double cached = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        f.Invalidate();
                        f.Update();
                        Application.DoEvents();
                        cached += MainForm.LastPaintMs;
                    }
                    cached /= 10.0;

                    System.IO.File.WriteAllText("build/bench.txt",
                        "full rebuild: " + (rebuild / 6.0).ToString("0.0") + " ms (draw " + (build / 6.0).ToString("0.0") + " ms)\n" +
                        "cached paint: " + cached.ToString("0.0") + " ms\n" +
                        "idle cpu at 1 Hz: " + ((rebuild / 6.0) / 10.0).ToString("0.00") + "% of one core (one rebuild per second)\n");
                    f.Close();
                }
                return 0;
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("build/bench.txt", ex.ToString());
                return 6;
            }
        }

        private static string ArgValue(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return "";
        }

        private static string PageArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--page") return args[i + 1];
            return "dashboard";
        }

        private static int RenderShot(string path, string apk, string pageName, int scrollAmt)
        {
            try
            {
                using (MainForm f = new MainForm(""))
                {
                    f.SuppressBoot = true;
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new System.Drawing.Point(-4000, -2000);
                    f.Size = new System.Drawing.Size(1240, 820);
                    string sizeArg = ArgValue(Environment.GetCommandLineArgs(), "--size");
                    if (!string.IsNullOrEmpty(sizeArg))
                    {
                        int sx = sizeArg.IndexOf('x');
                        int sw, sh;
                        if (sx > 0 && int.TryParse(sizeArg.Substring(0, sx), out sw) && int.TryParse(sizeArg.Substring(sx + 1), out sh))
                            f.Size = new System.Drawing.Size(sw, sh);
                    }
                    f.PrepareForShot(apk, pageName);
                    f.Show();
                    Application.DoEvents();
                    if (scrollAmt > 0) f.SetScroll(scrollAmt);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(120);
                    Application.DoEvents();
                    System.Drawing.Size client = f.ClientSize;
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(client.Width, client.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, client.Width, client.Height));
                        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    f.Close();
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err", ex.ToString()); } catch { }
                return 5;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); } catch { }
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
            try { SetProcessDPIAware(); } catch { }

            string apk = "";
            string outPath = "";
            string shot = "";
            bool runner = false;
            bool selftest = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--selftest") selftest = true;
                else if (a == "--apk" && i + 1 < args.Length) apk = args[++i];
                else if (a == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (a == "--shot" && i + 1 < args.Length) shot = args[++i];
                else if (a == "--runner") runner = true;
                else if (!a.StartsWith("-") && apk.Length == 0) apk = a;
            }

            if (selftest) return Selftest.Run(apk, outPath);
            if (shot.Length > 0)
            {
                int scrollAmt = 0;
                int.TryParse(ArgValue(args, "--scroll"), out scrollAmt);
                return RenderShot(shot, apk, PageArg(args), scrollAmt);
            }
            if (Array.IndexOf(args, "--bench") >= 0) return Bench(apk);

            if (apk.Length > 0 && !File.Exists(apk))
            {
                MessageBox.Show("APK not found:\n" + apk, "Veyra", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                apk = "";
            }

            try
            {
                Application.Run(new MainForm(apk));
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "veyra-crash.log"),
                        DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine);
                }
                catch { }
                MessageBox.Show("Veyra hit an unexpected error:\n\n" + ex.Message, "Veyra",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
