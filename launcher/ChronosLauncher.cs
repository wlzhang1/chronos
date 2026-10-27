using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace ChronosLauncher
{
    static class Program
    {
        private static Process backendProcess;
        private static Process frontendProcess;
        private static NotifyIcon trayIcon;
        private static string rootDir;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            rootDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

            // Falls Chronos schon läuft, einfach direkt das App-Fenster öffnen und beenden
            bool alreadyRunning = IsPortOpen("127.0.0.1", 3000) && IsPortOpen("127.0.0.1", 8000);
            if (alreadyRunning)
            {
                OpenAppWindow("http://localhost:3000");
                return;
            }

            Icon appIcon = LoadAppIcon();

            // Kleines Ladefenster anzeigen, während Backend & Next.js starten
            Form splash = new Form
            {
                Text = "Chronos – AI Day Planner",
                Size = new Size(380, 140),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false,
                BackColor = Color.FromArgb(9, 9, 11),
                ForeColor = Color.FromArgb(244, 244, 245),
                Icon = appIcon,
                TopMost = true
            };

            Label titleLabel = new Label
            {
                Text = "⚡ Chronos wird gestartet...",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = Color.FromArgb(129, 140, 248),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 55
            };

            Label statusLabel = new Label
            {
                Text = "Starte FastAPI Backend & Next.js Frontend...",
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
                ForeColor = Color.FromArgb(161, 161, 170),
                AutoSize = false,
                TextAlign = ContentAlignment.TopCenter,
                Dock = DockStyle.Fill
            };

            splash.Controls.Add(statusLabel);
            splash.Controls.Add(titleLabel);

            // System-Tray-Icon einrichten (damit man die Server jederzeit sauber beenden oder das Fenster neu öffnen kann)
            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Chronos öffnen", (s, e) => OpenAppWindow("http://localhost:3000"));
            trayMenu.MenuItems.Add("Backend API Docs (/docs)", (s, e) => Process.Start("http://localhost:8000/docs"));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Chronos beenden (Server stoppen)", (s, e) => ExitApplication());

            trayIcon = new NotifyIcon
            {
                Text = "Chronos – AI Day Planner (Aktiv)",
                Icon = appIcon,
                ContextMenu = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => OpenAppWindow("http://localhost:3000");

            splash.Shown += (s, e) =>
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (!IsPortOpen("127.0.0.1", 8000))
                        {
                            StartBackend();
                        }

                        if (!IsPortOpen("127.0.0.1", 3000))
                        {
                            StartFrontend();
                        }

                        // Warte bis Port 3000 erreichbar ist (max. 25 Sekunden)
                        for (int i = 0; i < 50; i++)
                        {
                            if (IsPortOpen("127.0.0.1", 3000))
                                break;
                            Thread.Sleep(500);
                        }

                        splash.Invoke((MethodInvoker)delegate
                        {
                            splash.Close();
                            OpenAppWindow("http://localhost:3000");
                            trayIcon.ShowBalloonTip(
                                3000,
                                "Chronos läuft im Hintergrund",
                                "Doppelklicke auf das Tray-Icon zum Öffnen oder Rechtsklick zum Beenden.",
                                ToolTipIcon.Info
                            );
                        });
                    }
                    catch (Exception ex)
                    {
                        splash.Invoke((MethodInvoker)delegate
                        {
                            splash.Close();
                            MessageBox.Show(
                                "Fehler beim Starten von Chronos:\n" + ex.Message,
                                "Chronos Fehler",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error
                            );
                            ExitApplication();
                        });
                    }
                });
            };

            splash.Show();
            Application.Run();
        }

        private static void StartBackend()
        {
            string venvPython = Path.Combine(rootDir, ".venv", "Scripts", "python.exe");
            string pythonExe = File.Exists(venvPython) ? venvPython : "python";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-m uvicorn backend.main:app --port 8000",
                WorkingDirectory = rootDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            backendProcess = Process.Start(psi);
        }

        private static void StartFrontend()
        {
            string frontendDir = Path.Combine(rootDir, "frontend");
            string npmCmd = FindNpmExecutable();

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + npmCmd + "\" run dev",
                WorkingDirectory = frontendDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            // Sicherstellen, dass Node.js im PATH des Prozesses ist
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.EnvironmentVariables["PATH"] = currentPath + @";C:\Program Files\nodejs;C:\nvm4w\nodejs;" +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

            frontendProcess = Process.Start(psi);
        }

        private static string FindNpmExecutable()
        {
            string[] candidates = new string[]
            {
                @"C:\Program Files\nodejs\npm.cmd",
                @"C:\nvm4w\nodejs\npm.cmd",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd")
            };

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
            return "npm.cmd";
        }

        private static void OpenAppWindow(string url)
        {
            // Versuche Chrome oder Edge im schlanken App-Modus (ohne Adressleiste/Tabs wie eine echte Desktop-App) zu öffnen
            string[] browserCandidates = new string[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            };

            foreach (string browser in browserCandidates)
            {
                if (File.Exists(browser))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = browser,
                        Arguments = "--app=" + url + " --window-size=1100,820",
                        UseShellExecute = true
                    });
                    return;
                }
            }

            // Fallback: Standardbrowser
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }

        private static bool IsPortOpen(string host, int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult result = client.BeginConnect(host, port, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
                    if (!success) return false;
                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void KillProcessTree(Process proc)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    Process p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /T /PID " + proc.Id,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    if (p != null)
                    {
                        p.WaitForExit(3000);
                    }
                }
            }
            catch { }
        }

        private static void ExitApplication()
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }

            KillProcessTree(frontendProcess);
            KillProcessTree(backendProcess);

            Application.Exit();
            Environment.Exit(0);
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                string icoPath = Path.Combine(rootDir, "chronos.ico");
                if (File.Exists(icoPath))
                {
                    return new Icon(icoPath);
                }
                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null)
                {
                    return extracted;
                }
            }
            catch { }

            return SystemIcons.Application;
        }
    }
}
