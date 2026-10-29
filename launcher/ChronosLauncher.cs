using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ChronosLauncher
{
    static class Program
    {
        private static Process backendProcess;
        private static Process frontendProcess;
        private static NotifyIcon trayIcon;
        private static string rootDir;
        private static QuickAddForm quickAddForm;

        // Low-Level Keyboard Hook für 100% zuverlässige globale Hotkeys (Alt + C & Strg + Shift + C)
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc keyboardProc = HookCallback;
        private static IntPtr hookId = IntPtr.Zero;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static Mutex singleInstanceMutex;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            rootDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

            // Prüfen, ob der Chronos-Launcher (Tray & Alt+C Hook) bereits läuft
            bool createdNew;
            singleInstanceMutex = new Mutex(true, "Local\\ChronosLauncherSingleInstanceMutex", out createdNew);
            if (!createdNew)
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

            // Quick-Add Spotlight Form erstellen und Handle vorab erzwingen
            quickAddForm = new QuickAddForm(appIcon);
            IntPtr forceHandle = quickAddForm.Handle;

            // Globalen Tastatur-Hook registrieren (funktioniert überall in Windows)
            try
            {
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    hookId = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch { }

            // System-Tray-Icon einrichten
            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("⚡ Schnellzugriff öffnen (Alt + C)", (s, e) => quickAddForm.ShowQuickAdd());
            trayMenu.MenuItems.Add("Schnellzugriff-Position zentrieren", (s, e) => quickAddForm.ResetPositionAndShow());
            trayMenu.MenuItems.Add("Chronos Web-App öffnen", (s, e) => OpenAppWindow("http://localhost:3000"));
            trayMenu.MenuItems.Add("Backend API Docs (/docs)", (s, e) => Process.Start("http://localhost:8000/docs"));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Chronos beenden (Server stoppen)", (s, e) => ExitApplication());

            trayIcon = new NotifyIcon
            {
                Text = "Chronos (Alt + C aktiv)",
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
                                4000,
                                "Chronos aktiv (Alt + C)",
                                "Drücke überall Alt + C für den Schnellzugriff oder klicke ins Tray-Icon.",
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

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool altPressed = (GetAsyncKeyState(0x12) & 0x8000) != 0;      // VK_MENU (Alt)
                bool ctrlPressed = (GetAsyncKeyState(0x11) & 0x8000) != 0;     // VK_CONTROL
                bool shiftPressed = (GetAsyncKeyState(0x10) & 0x8000) != 0;    // VK_SHIFT
                bool winPressed = ((GetAsyncKeyState(0x5B) & 0x8000) != 0) || ((GetAsyncKeyState(0x5C) & 0x8000) != 0);

                // Shortcut 1: Alt + C
                // Shortcut 2: Strg + Shift + C
                // Shortcut 3: Win + Alt + C
                bool isAltC = (vkCode == (int)Keys.C && altPressed && !ctrlPressed && !shiftPressed);
                bool isCtrlShiftC = (vkCode == (int)Keys.C && ctrlPressed && shiftPressed && !altPressed);
                bool isWinAltC = (vkCode == (int)Keys.C && winPressed && altPressed);

                if (isAltC || isCtrlShiftC || isWinAltC)
                {
                    if (quickAddForm != null)
                    {
                        quickAddForm.BeginInvoke((MethodInvoker)delegate
                        {
                            quickAddForm.ToggleQuickAdd();
                        });
                        return (IntPtr)1; // Tastendruck abfangen
                    }
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        private static void StartBackend()
        {
            string venvPython = Path.Combine(rootDir, ".venv", "Scripts", "python.exe");
            string pythonExe = File.Exists(venvPython) ? venvPython : "python";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-m uvicorn backend.main:app --port 8000 --reload",
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

        public static void OpenAppWindow(string url)
        {
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

        public static void ShowTrayNotification(string title, string text)
        {
            if (trayIcon != null)
            {
                trayIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
            }
        }

        private static void ExitApplication()
        {
            try
            {
                if (hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hookId);
                    hookId = IntPtr.Zero;
                }
            }
            catch { }

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

    /// <summary>
    /// Minimalistisches, schwebendes Schnellzugriff-Fenster (Raycast / Spotlight-Style).
    /// </summary>
    public class QuickAddForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int leftWidth;
            public int rightWidth;
            public int topHeight;
            public int bottomHeight;
        }

        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse
        );

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, string lp);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private const int EM_SETCUEBANNER = 0x1501;
        private const int WM_NCHITTEST = 0x84;
        private const int HTCLIENT = 0x1;
        private const int HTCAPTION = 0x2;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_NCLBUTTONDOWN = 0x00A1;

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;

        private const int COLLAPSED_HEIGHT = 114;
        private const int HEADER_BAR_HEIGHT = 38;
        private const int FOOTER_HEIGHT = 42;
        private const int MAX_CARDS_VIEW_HEIGHT = 330;

        private class QuickEventItem
        {
            public string Title = "";
            public string StartTime = "";
            public string EndTime = "";
            public string Category = "other";
            public string Location = "";
            public string Description = "";
            public int? ReminderMinutes = null;
            public bool HasConflict = false;
            public string ConflictWith = "";
            public string ConflictWarning = "";
            public string SuggestedStartTime = "";
            public string SuggestedEndTime = "";
            public string SuggestedTimeLabel = "";

            // Hit-Test-Rechtecke innerhalb des eventsPanel für interaktive Klicks
            public Rectangle CardRect;
            public Rectangle CategoryBadgeRect;
            public Rectangle ReminderBadgeRect;
            public Rectangle DeleteBtnRect;
            public Rectangle AcceptSuggestionBtnRect;
            public Rectangle DismissConflictBtnRect;
        }

        private class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                this.DoubleBuffered = true;
                this.SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.UserPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true
                );
            }
        }

        private TextBox textBox;
        private Label statusLabel;
        private Label hintLabel;
        private DoubleBufferedPanel eventsPanel;
        private Icon appIcon;

        private bool isBusy = false;
        private string lastParsedQuery = "";
        private List<QuickEventItem> parsedEvents = new List<QuickEventItem>();
        private int cardsScrollOffset = 0;
        private int totalCardsContentHeight = 0;
        private Rectangle syncAllBtnRect = Rectangle.Empty;
        private Point eventsMousePos = new Point(-1, -1);

        private System.Windows.Forms.Timer autoCloseTimer;
        private DateTime lastShownTime = DateTime.MinValue;

        // Sanfte Animation (Slide & Fade beim Öffnen/Schließen)
        private System.Windows.Forms.Timer animTimer;
        private Stopwatch animStopwatch;
        private bool isAnimating = false;
        private bool isHiding = false;
        private int animStartX, animStartY;
        private int animTargetX, animTargetY;
        private const int ANIM_IN_DURATION_MS = 135;
        private const int ANIM_OUT_DURATION_MS = 80;

        // Sanfte Aufklapp-Animation (Height Expansion)
        private System.Windows.Forms.Timer expandTimer;
        private Stopwatch expandStopwatch;
        private int expandStartHeight = COLLAPSED_HEIGHT;
        private int expandTargetHeight = COLLAPSED_HEIGHT;
        private const int EXPAND_DURATION_MS = 145;

        // DWM & Position
        private bool hasNativeDwmCorners = false;
        private bool hasUserMoved = false;
        private Point savedUserLocation = Point.Empty;

        public QuickAddForm(Icon icon)
        {
            this.appIcon = icon;
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.Size = new Size(680, COLLAPSED_HEIGHT);
            this.BackColor = Color.FromArgb(15, 15, 20);
            this.DoubleBuffered = true;

            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true
            );

            // Text Input Feld
            textBox = new TextBox
            {
                Location = new Point(54, 20),
                Width = 604,
                Font = new Font("Segoe UI", 13.5f, FontStyle.Regular),
                BackColor = Color.FromArgb(15, 15, 20),
                ForeColor = Color.FromArgb(244, 244, 245),
                BorderStyle = BorderStyle.None,
            };
            textBox.KeyDown += TextBox_KeyDown;
            textBox.TextChanged += TextBox_TextChanged;

            // Aufklappbarer Bereich für die kompakte Termin-Vorschau
            eventsPanel = new DoubleBufferedPanel
            {
                Location = new Point(14, 70),
                Width = 652,
                Height = 0,
                BackColor = Color.FromArgb(15, 15, 20),
                Visible = false
            };
            eventsPanel.Paint += EventsPanel_Paint;
            eventsPanel.MouseMove += EventsPanel_MouseMove;
            eventsPanel.MouseLeave += (s, e) =>
            {
                eventsMousePos = new Point(-1, -1);
                eventsPanel.Cursor = Cursors.Default;
                eventsPanel.Invalidate();
            };
            eventsPanel.MouseDown += EventsPanel_MouseDown;
            eventsPanel.MouseWheel += EventsPanel_MouseWheel;

            // Statusanzeige (links unten)
            statusLabel = new Label
            {
                Location = new Point(20, 78),
                Width = 370,
                Height = 28,
                AutoSize = false,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(140, 140, 155),
                Text = "⚡ Chronos Schnellzugriff (Alt + C)"
            };
            statusLabel.MouseDown += Draggable_MouseDown;

            // Tastatur-Tipps (rechts unten)
            hintLabel = new Label
            {
                Location = new Point(394, 78),
                Width = 266,
                Height = 28,
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Segoe UI", 8f, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 100, 115),
                Text = "[Enter] Vorschau · [Tab] Web-App · [Esc]"
            };
            hintLabel.MouseDown += Draggable_MouseDown;

            this.Controls.Add(textBox);
            this.Controls.Add(eventsPanel);
            this.Controls.Add(statusLabel);
            this.Controls.Add(hintLabel);

            // Gesamtes Formular kann per Mausklick bewegt werden
            this.MouseDown += Draggable_MouseDown;

            // Tooltip zur Orientierung für die Fensterbewegung
            ToolTip dragTip = new ToolTip();
            dragTip.SetToolTip(statusLabel, "Gedrückt halten zum Bewegen des Fensters");
            dragTip.SetToolTip(hintLabel, "Gedrückt halten zum Bewegen des Fensters");

            // Klick außerhalb schließt das Fenster (mit Puffer gegen vorzeitiges Schließen beim Einblenden)
            this.Deactivate += (s, e) =>
            {
                if (!isBusy && this.Visible && !isHiding && (DateTime.Now - lastShownTime).TotalMilliseconds > 400)
                {
                    HideQuickAdd();
                }
            };

            autoCloseTimer = new System.Windows.Forms.Timer { Interval = 1300 };
            autoCloseTimer.Tick += (s, e) =>
            {
                autoCloseTimer.Stop();
                HideQuickAdd();
            };

            // Animations-Timer (Ein-/Ausblenden)
            animTimer = new System.Windows.Forms.Timer { Interval = 12 };
            animTimer.Tick += AnimTimer_Tick;
            animStopwatch = new Stopwatch();

            // Aufklapp-Timer (Höhen-Animation)
            expandTimer = new System.Windows.Forms.Timer { Interval = 12 };
            expandTimer.Tick += ExpandTimer_Tick;
            expandStopwatch = new Stopwatch();
        }

        private void TextBox_TextChanged(object sender, EventArgs e)
        {
            if (isBusy) return;
            string current = textBox.Text.Trim();
            if (parsedEvents.Count > 0 && current != lastParsedQuery)
            {
                hintLabel.Text = "[Enter] Neu analysieren · [Tab] App · [Esc]";
                hintLabel.ForeColor = Color.FromArgb(129, 140, 248);
            }
            else if (parsedEvents.Count > 0)
            {
                UpdateExpandedFooterText();
            }
        }

        private void Draggable_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Verhindert ungewolltes Maximieren beim Doppelklick auf die Titelleisten-Region
            if (m.Msg == WM_NCLBUTTONDBLCLK)
            {
                return;
            }

            base.WndProc(ref m);

            // Macht den gesamten Formular-Hintergrund als Drag-Bereich greifbar
            if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
            {
                m.Result = (IntPtr)HTCAPTION;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetupWindowStyleAndDwm();
            SendMessage(textBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Termin eingeben (z. B. Morgen 14 Uhr Team-Call)...");
        }

        private void SetupWindowStyleAndDwm()
        {
            hasNativeDwmCorners = false;

            // Windows 11 DWM (Build >= 22000): Native, hardware-beschleunigte Kantenglättung ohne schwarze Ecken!
            try
            {
                if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000)
                {
                    int cornerVal = DWMWCP_ROUND;
                    int hr1 = DwmSetWindowAttribute(this.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerVal, sizeof(int));

                    int borderColor = 0x00F16663; // Indigo #6366F1 (0x00BBGGRR)
                    int hr2 = DwmSetWindowAttribute(this.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

                    if (hr1 == 0)
                    {
                        hasNativeDwmCorners = true;
                    }
                }
            }
            catch { }

            // Moderner DWM-Schatten (ersetzt das alte CS_DROPSHADOW, wodurch die schwarzen Ecken-Artefakte verschwinden)
            try
            {
                MARGINS m = new MARGINS { leftWidth = 1, rightWidth = 1, topHeight = 1, bottomHeight = 1 };
                DwmExtendFrameIntoClientArea(this.Handle, ref m);
            }
            catch { }

            UpdateFallbackRegion();
        }

        private void UpdateFallbackRegion()
        {
            if (!hasNativeDwmCorners && this.IsHandleCreated)
            {
                try
                {
                    IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 28, 28);
                    this.Region = Region.FromHrgn(rgn);
                }
                catch { }
            }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            // Sobald der Nutzer das Fenster selbst bewegt hat, merken wir uns die Wunschposition
            if (this.Visible && !isAnimating)
            {
                hasUserMoved = true;
                savedUserLocation = this.Location;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Feine Akzent-Umrandung (Indigo) – auf Win11 rendert DWM die Umrandung nativ und perfekt sauber
            if (!hasNativeDwmCorners)
            {
                using (GraphicsPath path = GetRoundedRectPath(new Rectangle(1, 1, Width - 3, Height - 3), 14))
                using (Pen borderPen = new Pen(Color.FromArgb(99, 102, 241), 1.5f))
                {
                    g.DrawPath(borderPen, path);
                }
            }

            // Obere horizontale Trennlinie unter dem Textfeld
            using (Pen sepPen = new Pen(Color.FromArgb(39, 39, 45), 1f))
            {
                g.DrawLine(sepPen, 16, 68, Width - 16, 68);
                // Untere Trennlinie über dem Footer, wenn aufgeklappt
                if (this.Height > COLLAPSED_HEIGHT + 10)
                {
                    int footerLineY = this.Height - FOOTER_HEIGHT;
                    g.DrawLine(sepPen, 16, footerLineY, Width - 16, footerLineY);
                }
            }

            // Chronos Icon links
            if (appIcon != null)
            {
                try
                {
                    g.DrawIcon(appIcon, new Rectangle(18, 22, 24, 24));
                }
                catch { }
            }
        }

        private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void AnimateToHeight(int newTargetHeight, bool immediate = false)
        {
            expandTargetHeight = newTargetHeight;
            if (immediate || !this.Visible)
            {
                expandTimer.Stop();
                expandStopwatch.Stop();
                ApplyFormHeight(newTargetHeight);
                return;
            }

            expandStartHeight = this.Height;
            if (expandStartHeight == expandTargetHeight)
            {
                ApplyFormHeight(expandTargetHeight);
                return;
            }

            expandStopwatch.Restart();
            expandTimer.Start();
        }

        private void ExpandTimer_Tick(object sender, EventArgs e)
        {
            long elapsed = expandStopwatch.ElapsedMilliseconds;
            double progress = (double)elapsed / EXPAND_DURATION_MS;
            if (progress >= 1.0)
            {
                expandTimer.Stop();
                expandStopwatch.Stop();
                ApplyFormHeight(expandTargetHeight);
                return;
            }

            double ease = 1.0 - Math.Pow(1.0 - progress, 3);
            int curH = expandStartHeight + (int)((expandTargetHeight - expandStartHeight) * ease);
            ApplyFormHeight(curH);
        }

        private void ApplyFormHeight(int h)
        {
            this.Height = h;
            int footerTop = h - FOOTER_HEIGHT + 8;
            statusLabel.Top = footerTop;
            hintLabel.Top = footerTop;

            int panelH = Math.Max(0, h - 70 - FOOTER_HEIGHT - 2);
            if (panelH > 10 && parsedEvents.Count > 0)
            {
                eventsPanel.Height = panelH;
                if (!eventsPanel.Visible) eventsPanel.Visible = true;
                eventsPanel.Invalidate();
            }
            else
            {
                eventsPanel.Visible = false;
                eventsPanel.Height = 0;
            }

            UpdateFallbackRegion();
            this.Invalidate();
        }

        private int CalculateDesiredExpandedHeight()
        {
            if (parsedEvents == null || parsedEvents.Count == 0)
            {
                return COLLAPSED_HEIGHT;
            }

            int cardsH = 0;
            for (int i = 0; i < parsedEvents.Count; i++)
            {
                QuickEventItem ev = parsedEvents[i];
                int cardH = ev.HasConflict ? 116 : 64;
                cardsH += cardH + 8;
            }
            totalCardsContentHeight = cardsH;

            int visibleCardsH = Math.Min(MAX_CARDS_VIEW_HEIGHT, cardsH);
            int totalPanelH = HEADER_BAR_HEIGHT + visibleCardsH + 4;
            return 70 + totalPanelH + FOOTER_HEIGHT + 2;
        }

        private void CollapsePreview(bool immediate = false)
        {
            parsedEvents.Clear();
            lastParsedQuery = "";
            cardsScrollOffset = 0;
            totalCardsContentHeight = 0;
            AnimateToHeight(COLLAPSED_HEIGHT, immediate);
        }

        private void UpdateExpandedFooterText()
        {
            if (parsedEvents.Count == 0)
            {
                statusLabel.Text = "⚡ Chronos Schnellzugriff (Alt + C)";
                statusLabel.ForeColor = Color.FromArgb(140, 140, 155);
                hintLabel.Text = "[Enter] Vorschau · [Tab] Web-App · [Esc]";
                hintLabel.ForeColor = Color.FromArgb(100, 100, 115);
                return;
            }

            int conflictCount = 0;
            foreach (QuickEventItem ev in parsedEvents)
            {
                if (ev.HasConflict) conflictCount++;
            }

            if (conflictCount > 0)
            {
                statusLabel.Text = conflictCount == 1
                    ? "⚠️ 1 Doppelbuchung erkannt – Vorschlag wählen oder eintragen"
                    : "⚠️ " + conflictCount + " Doppelbuchungen erkannt";
                statusLabel.ForeColor = Color.FromArgb(251, 191, 36);
                hintLabel.Text = "[Enter] Eintragen · [Shift+Enter] Vorschlag · [Esc]";
                hintLabel.ForeColor = Color.FromArgb(251, 191, 36);
            }
            else
            {
                statusLabel.Text = parsedEvents.Count == 1
                    ? "✨ 1 Termin bereit – [Enter] zum Synchronisieren"
                    : "✨ " + parsedEvents.Count + " Termine bereit – [Enter] zum Synchronisieren";
                statusLabel.ForeColor = Color.FromArgb(161, 161, 170);
                hintLabel.Text = "[Enter] In Kalender eintragen · [Tab] App · [Esc]";
                hintLabel.ForeColor = Color.FromArgb(113, 113, 122);
            }
        }

        // --- KOMPAKTE TERMIN-ANSICHT (Painting & Interaktion) ---

        private void GetCategoryStyle(string cat, out string label, out Color bg, out Color border, out Color fg)
        {
            switch ((cat ?? "other").ToLowerInvariant())
            {
                case "work":
                    label = "Work";
                    bg = Color.FromArgb(38, 59, 130, 246);
                    border = Color.FromArgb(90, 59, 130, 246);
                    fg = Color.FromArgb(147, 197, 253);
                    break;
                case "meeting":
                    label = "Meeting";
                    bg = Color.FromArgb(38, 245, 158, 11);
                    border = Color.FromArgb(90, 245, 158, 11);
                    fg = Color.FromArgb(252, 211, 77);
                    break;
                case "focus":
                    label = "Deep Work";
                    bg = Color.FromArgb(38, 168, 85, 247);
                    border = Color.FromArgb(90, 168, 85, 247);
                    fg = Color.FromArgb(216, 180, 254);
                    break;
                case "health":
                    label = "Health";
                    bg = Color.FromArgb(38, 16, 185, 129);
                    border = Color.FromArgb(90, 16, 185, 129);
                    fg = Color.FromArgb(110, 231, 183);
                    break;
                case "personal":
                    label = "Personal";
                    bg = Color.FromArgb(38, 20, 184, 166);
                    border = Color.FromArgb(90, 20, 184, 166);
                    fg = Color.FromArgb(94, 234, 212);
                    break;
                default:
                    label = "Sonstiges";
                    bg = Color.FromArgb(45, 113, 113, 122);
                    border = Color.FromArgb(90, 113, 113, 122);
                    fg = Color.FromArgb(212, 212, 216);
                    break;
            }
        }

        private string FormatCompactDateTime(string startIso, string endIso)
        {
            try
            {
                DateTimeOffset s = DateTimeOffset.Parse(startIso, CultureInfo.InvariantCulture);
                DateTimeOffset e = DateTimeOffset.Parse(endIso, CultureInfo.InvariantCulture);
                CultureInfo de = new CultureInfo("de-DE");
                string datePart = s.ToString("ddd, dd.MM.yyyy", de);
                string timePart = s.ToString("HH:mm", de) + " – " + e.ToString("HH:mm", de) + " Uhr";
                return datePart + "  ·  " + timePart;
            }
            catch
            {
                return startIso;
            }
        }

        private void EventsPanel_Paint(object sender, PaintEventArgs e)
        {
            if (parsedEvents.Count == 0) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int w = eventsPanel.Width;

            // 1. Header-Zeile ("Erkannte Termine (N)" + Button "In Google Calendar eintragen")
            using (Font headerFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (Font subFont = new Font("Segoe UI", 7.8f, FontStyle.Regular))
            using (SolidBrush whiteBrush = new SolidBrush(Color.FromArgb(244, 244, 245)))
            using (SolidBrush mutedBrush = new SolidBrush(Color.FromArgb(113, 113, 122)))
            {
                string headerTitle = "📅 Erkannte Termine (" + parsedEvents.Count + ")";
                g.DrawString(headerTitle, headerFont, whiteBrush, new PointF(4, 7));

                SizeF titleSz = g.MeasureString(headerTitle, headerFont);
                g.DrawString("·  Klicke auf Badges zum Anpassen", subFont, mutedBrush, new PointF(8 + titleSz.Width, 9));
            }

            syncAllBtnRect = new Rectangle(w - 210, 4, 206, 28);
            bool syncHover = syncAllBtnRect.Contains(eventsMousePos);
            Color syncBg = syncHover ? Color.FromArgb(16, 185, 129) : Color.FromArgb(5, 150, 105);
            using (GraphicsPath btnPath = GetRoundedRectPath(syncAllBtnRect, 8))
            using (SolidBrush btnBrush = new SolidBrush(syncBg))
            using (Font btnFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.FillPath(btnBrush, btnPath);
                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("＋ In Google Calendar eintragen ↵", btnFont, textBrush, syncAllBtnRect, sf);
            }

            // 2. Scrollbarer Karten-Bereich (Clip setzen)
            Rectangle cardsViewport = new Rectangle(0, HEADER_BAR_HEIGHT, w, eventsPanel.Height - HEADER_BAR_HEIGHT);
            GraphicsState state = g.Save();
            g.SetClip(cardsViewport);

            int curY = HEADER_BAR_HEIGHT + 2 - cardsScrollOffset;

            using (Font badgeFont = new Font("Segoe UI", 7.8f, FontStyle.Bold))
            using (Font timeFont = new Font("Segoe UI", 8.2f, FontStyle.Regular))
            using (Font titleFont = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (Font metaFont = new Font("Segoe UI", 8.2f, FontStyle.Regular))
            using (Font warnTitleFont = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (Font warnTextFont = new Font("Segoe UI", 7.8f, FontStyle.Regular))
            {
                for (int i = 0; i < parsedEvents.Count; i++)
                {
                    QuickEventItem ev = parsedEvents[i];
                    int cardH = ev.HasConflict ? 116 : 64;
                    Rectangle cardRect = new Rectangle(2, curY, w - 4, cardH);
                    ev.CardRect = cardRect;

                    bool cardHover = cardRect.Contains(eventsMousePos);

                    // Karten-Hintergrund & Rahmen
                    Color cardBg = Color.FromArgb(24, 24, 30);
                    Color cardBorder = ev.HasConflict
                        ? Color.FromArgb(170, 245, 158, 11)
                        : (cardHover ? Color.FromArgb(63, 63, 74) : Color.FromArgb(39, 39, 46));

                    using (GraphicsPath cardPath = GetRoundedRectPath(cardRect, 11))
                    using (SolidBrush bgBrush = new SolidBrush(cardBg))
                    using (Pen borderPen = new Pen(cardBorder, 1f))
                    {
                        g.FillPath(bgBrush, cardPath);
                        g.DrawPath(borderPen, cardPath);
                    }

                    // --- Zeile 1: Kategorie-Badge · Datum/Uhrzeit · Reminder-Badge · Löschen-Button ---
                    string catLabel;
                    Color catBg, catBorder, catFg;
                    GetCategoryStyle(ev.Category, out catLabel, out catBg, out catBorder, out catFg);

                    SizeF catSz = g.MeasureString(catLabel, badgeFont);
                    int catW = (int)catSz.Width + 16;
                    Rectangle catRect = new Rectangle(cardRect.X + 12, cardRect.Y + 9, catW, 20);
                    ev.CategoryBadgeRect = catRect;

                    using (GraphicsPath catPath = GetRoundedRectPath(catRect, 9))
                    using (SolidBrush cBg = new SolidBrush(catBg))
                    using (Pen cPen = new Pen(catBorder, 1f))
                    using (SolidBrush cFg = new SolidBrush(catFg))
                    {
                        g.FillPath(cBg, catPath);
                        g.DrawPath(cPen, catPath);
                        StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(catLabel, badgeFont, cFg, catRect, sf);
                    }

                    // Datum & Uhrzeit
                    string dtText = FormatCompactDateTime(ev.StartTime, ev.EndTime);
                    float dtX = catRect.Right + 10;
                    using (SolidBrush dtBrush = new SolidBrush(Color.FromArgb(161, 161, 170)))
                    {
                        g.DrawString(dtText, timeFont, dtBrush, new PointF(dtX, cardRect.Y + 11));
                    }
                    SizeF dtSz = g.MeasureString(dtText, timeFont);

                    // Reminder-Badge
                    string remLabel = (ev.ReminderMinutes.HasValue && ev.ReminderMinutes.Value > 0)
                        ? ("🔔 " + ev.ReminderMinutes.Value + "m")
                        : "🔕 Keine";
                    SizeF remSz = g.MeasureString(remLabel, badgeFont);
                    Rectangle remRect = new Rectangle((int)(dtX + dtSz.Width + 8), cardRect.Y + 9, (int)remSz.Width + 14, 20);
                    ev.ReminderBadgeRect = remRect;

                    Color remBg = (ev.ReminderMinutes.HasValue && ev.ReminderMinutes.Value > 0)
                        ? Color.FromArgb(38, 99, 102, 241)
                        : Color.FromArgb(30, 63, 63, 70);
                    Color remBorder = (ev.ReminderMinutes.HasValue && ev.ReminderMinutes.Value > 0)
                        ? Color.FromArgb(90, 99, 102, 241)
                        : Color.FromArgb(60, 82, 82, 91);
                    Color remFg = (ev.ReminderMinutes.HasValue && ev.ReminderMinutes.Value > 0)
                        ? Color.FromArgb(165, 180, 252)
                        : Color.FromArgb(113, 113, 122);

                    using (GraphicsPath remPath = GetRoundedRectPath(remRect, 9))
                    using (SolidBrush rBg = new SolidBrush(remBg))
                    using (Pen rPen = new Pen(remBorder, 1f))
                    using (SolidBrush rFg = new SolidBrush(remFg))
                    {
                        g.FillPath(rBg, remPath);
                        g.DrawPath(rPen, remPath);
                        StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(remLabel, badgeFont, rFg, remRect, sf);
                    }

                    // Löschen-Button (✕) oben rechts
                    Rectangle delRect = new Rectangle(cardRect.Right - 30, cardRect.Y + 8, 22, 22);
                    ev.DeleteBtnRect = delRect;
                    bool delHover = delRect.Contains(eventsMousePos);
                    if (delHover)
                    {
                        using (GraphicsPath delPath = GetRoundedRectPath(delRect, 6))
                        using (SolidBrush delBg = new SolidBrush(Color.FromArgb(40, 239, 68, 68)))
                        {
                            g.FillPath(delBg, delPath);
                        }
                    }
                    using (SolidBrush delFg = new SolidBrush(delHover ? Color.FromArgb(248, 113, 113) : Color.FromArgb(113, 113, 122)))
                    {
                        StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("✕", badgeFont, delFg, delRect, sf);
                    }

                    // --- Zeile 2: Titel + Ort + Notiz kompakt in einer Zeile ---
                    float row2Y = cardRect.Y + 35;
                    using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(244, 244, 245)))
                    {
                        g.DrawString(ev.Title, titleFont, titleBrush, new PointF(cardRect.X + 12, row2Y));
                    }
                    SizeF titleMeasured = g.MeasureString(ev.Title, titleFont);
                    float metaX = cardRect.X + 16 + titleMeasured.Width;

                    if (!string.IsNullOrEmpty(ev.Location))
                    {
                        string locStr = "📍 " + ev.Location;
                        using (SolidBrush locBrush = new SolidBrush(Color.FromArgb(129, 140, 248)))
                        {
                            g.DrawString(locStr, metaFont, locBrush, new PointF(metaX, row2Y + 2));
                        }
                        metaX += g.MeasureString(locStr, metaFont).Width + 8;
                    }

                    if (!string.IsNullOrEmpty(ev.Description) && metaX < cardRect.Right - 80)
                    {
                        string descStr = "·  " + ev.Description;
                        RectangleF descClip = new RectangleF(metaX, row2Y + 2, cardRect.Right - metaX - 14, 18);
                        StringFormat descSf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                        using (SolidBrush descBrush = new SolidBrush(Color.FromArgb(113, 113, 122)))
                        {
                            g.DrawString(descStr, metaFont, descBrush, descClip, descSf);
                        }
                    }

                    // --- Zeile 3 (Optional): Konflikt- & Doppelbuchungs-Banner ---
                    if (ev.HasConflict)
                    {
                        Rectangle warnBox = new Rectangle(cardRect.X + 10, cardRect.Y + 61, cardRect.Width - 20, 46);
                        using (GraphicsPath warnPath = GetRoundedRectPath(warnBox, 8))
                        using (SolidBrush warnBg = new SolidBrush(Color.FromArgb(28, 245, 158, 11)))
                        using (Pen warnPen = new Pen(Color.FromArgb(90, 245, 158, 11), 1f))
                        {
                            g.FillPath(warnBg, warnPath);
                            g.DrawPath(warnPen, warnPath);
                        }

                        // Warntext links
                        string warnMsg = !string.IsNullOrEmpty(ev.ConflictWarning)
                            ? ev.ConflictWarning
                            : ("Überschneidung mit " + (string.IsNullOrEmpty(ev.ConflictWith) ? "bestehendem Termin" : ev.ConflictWith));
                        RectangleF warnTextRect = new RectangleF(warnBox.X + 10, warnBox.Y + 6, warnBox.Width - 255, 36);
                        StringFormat warnSf = new StringFormat { Trimming = StringTrimming.EllipsisWord };
                        using (SolidBrush warnTextBrush = new SolidBrush(Color.FromArgb(253, 230, 138)))
                        {
                            g.DrawString("⚠️ " + warnMsg, warnTextFont, warnTextBrush, warnTextRect, warnSf);
                        }

                        // Button 1: "Auf HH:MM legen"
                        if (!string.IsNullOrEmpty(ev.SuggestedStartTime) && !string.IsNullOrEmpty(ev.SuggestedEndTime))
                        {
                            string sugLabel = "🕒 Auf " + (!string.IsNullOrEmpty(ev.SuggestedTimeLabel) ? ev.SuggestedTimeLabel : "freien Slot");
                            Rectangle sugRect = new Rectangle(warnBox.Right - 238, warnBox.Y + 9, 148, 28);
                            ev.AcceptSuggestionBtnRect = sugRect;
                            bool sugHover = sugRect.Contains(eventsMousePos);

                            using (GraphicsPath sugPath = GetRoundedRectPath(sugRect, 6))
                            using (SolidBrush sugBg = new SolidBrush(sugHover ? Color.FromArgb(251, 191, 36) : Color.FromArgb(245, 158, 11)))
                            using (SolidBrush sugFg = new SolidBrush(Color.FromArgb(9, 9, 11)))
                            {
                                g.FillPath(sugBg, sugPath);
                                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                                g.DrawString(sugLabel, warnTitleFont, sugFg, sugRect, sf);
                            }
                        }
                        else
                        {
                            ev.AcceptSuggestionBtnRect = Rectangle.Empty;
                        }

                        // Button 2: "Trotzdem"
                        Rectangle keepRect = new Rectangle(warnBox.Right - 84, warnBox.Y + 9, 76, 28);
                        ev.DismissConflictBtnRect = keepRect;
                        bool keepHover = keepRect.Contains(eventsMousePos);

                        using (GraphicsPath keepPath = GetRoundedRectPath(keepRect, 6))
                        using (SolidBrush keepBg = new SolidBrush(keepHover ? Color.FromArgb(63, 63, 70) : Color.FromArgb(39, 39, 42)))
                        using (Pen keepPen = new Pen(Color.FromArgb(82, 82, 91), 1f))
                        using (SolidBrush keepFg = new SolidBrush(Color.FromArgb(212, 212, 216)))
                        {
                            g.FillPath(keepBg, keepPath);
                            g.DrawPath(keepPen, keepPath);
                            StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                            g.DrawString("✓ Trotzdem", warnTextFont, keepFg, keepRect, sf);
                        }
                    }
                    else
                    {
                        ev.AcceptSuggestionBtnRect = Rectangle.Empty;
                        ev.DismissConflictBtnRect = Rectangle.Empty;
                    }

                    curY += cardH + 8;
                }
            }

            g.Restore(state);
        }

        private void EventsPanel_MouseMove(object sender, MouseEventArgs e)
        {
            eventsMousePos = e.Location;
            bool isInteractive = syncAllBtnRect.Contains(e.Location);

            if (!isInteractive)
            {
                foreach (QuickEventItem ev in parsedEvents)
                {
                    if (ev.CategoryBadgeRect.Contains(e.Location) ||
                        ev.ReminderBadgeRect.Contains(e.Location) ||
                        ev.DeleteBtnRect.Contains(e.Location) ||
                        (!ev.AcceptSuggestionBtnRect.IsEmpty && ev.AcceptSuggestionBtnRect.Contains(e.Location)) ||
                        (!ev.DismissConflictBtnRect.IsEmpty && ev.DismissConflictBtnRect.Contains(e.Location)))
                    {
                        isInteractive = true;
                        break;
                    }
                }
            }

            eventsPanel.Cursor = isInteractive ? Cursors.Hand : Cursors.Default;
            eventsPanel.Invalidate();
        }

        private void EventsPanel_MouseWheel(object sender, MouseEventArgs e)
        {
            int viewportH = eventsPanel.Height - HEADER_BAR_HEIGHT;
            int maxScroll = Math.Max(0, totalCardsContentHeight - viewportH);
            if (maxScroll <= 0) return;

            cardsScrollOffset = Math.Max(0, Math.Min(maxScroll, cardsScrollOffset - (e.Delta / 3)));
            eventsPanel.Invalidate();
        }

        private void EventsPanel_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || isBusy) return;

            if (syncAllBtnRect.Contains(e.Location))
            {
                SyncParsedEventsToCalendar();
                textBox.Focus();
                return;
            }

            string[] categoryOrder = new string[] { "work", "meeting", "focus", "health", "personal", "other" };
            int?[] reminderOrder = new int?[] { null, 10, 15, 30, 60 };

            for (int i = 0; i < parsedEvents.Count; i++)
            {
                QuickEventItem ev = parsedEvents[i];

                // Kategorie durchschalten
                if (ev.CategoryBadgeRect.Contains(e.Location))
                {
                    int idx = Array.IndexOf(categoryOrder, (ev.Category ?? "other").ToLowerInvariant());
                    ev.Category = categoryOrder[(idx + 1) % categoryOrder.Length];
                    eventsPanel.Invalidate();
                    textBox.Focus();
                    return;
                }

                // Erinnerung durchschalten
                if (ev.ReminderBadgeRect.Contains(e.Location))
                {
                    int idx = Array.IndexOf(reminderOrder, ev.ReminderMinutes);
                    ev.ReminderMinutes = reminderOrder[(idx + 1) % reminderOrder.Length];
                    eventsPanel.Invalidate();
                    textBox.Focus();
                    return;
                }

                // Termin aus Vorschau entfernen
                if (ev.DeleteBtnRect.Contains(e.Location))
                {
                    parsedEvents.RemoveAt(i);
                    if (parsedEvents.Count == 0)
                    {
                        CollapsePreview();
                        UpdateExpandedFooterText();
                    }
                    else
                    {
                        UpdateExpandedFooterText();
                        AnimateToHeight(CalculateDesiredExpandedHeight());
                    }
                    textBox.Focus();
                    return;
                }

                // Konflikt-Vorschlag annehmen
                if (ev.HasConflict && !ev.AcceptSuggestionBtnRect.IsEmpty && ev.AcceptSuggestionBtnRect.Contains(e.Location))
                {
                    ev.StartTime = ev.SuggestedStartTime;
                    ev.EndTime = ev.SuggestedEndTime;
                    ev.HasConflict = false;
                    ev.ConflictWarning = "";
                    ev.ConflictWith = "";
                    UpdateExpandedFooterText();
                    AnimateToHeight(CalculateDesiredExpandedHeight());
                    textBox.Focus();
                    return;
                }

                // Konflikt ignorieren ("Trotzdem behalten")
                if (ev.HasConflict && !ev.DismissConflictBtnRect.IsEmpty && ev.DismissConflictBtnRect.Contains(e.Location))
                {
                    ev.HasConflict = false;
                    ev.ConflictWarning = "";
                    UpdateExpandedFooterText();
                    AnimateToHeight(CalculateDesiredExpandedHeight());
                    textBox.Focus();
                    return;
                }
            }

            // Klick auf freie Fläche im Panel erlaubt ebenfalls Fenster-Drag
            Draggable_MouseDown(sender, e);
        }

        public void ToggleQuickAdd()
        {
            if (this.Visible && !isHiding)
            {
                HideQuickAdd();
            }
            else
            {
                ShowQuickAdd();
            }
        }

        public void ResetPositionAndShow()
        {
            hasUserMoved = false;
            savedUserLocation = Point.Empty;
            ShowQuickAdd();
        }

        public void ShowQuickAdd()
        {
            lastShownTime = DateTime.Now;
            autoCloseTimer.Stop();
            isBusy = false;
            isHiding = false;

            int targetX, targetY;
            if (hasUserMoved && savedUserLocation != Point.Empty && IsPointOnAnyScreen(savedUserLocation))
            {
                targetX = savedUserLocation.X;
                targetY = savedUserLocation.Y;
            }
            else
            {
                Screen screen = Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen;
                Rectangle area = screen.WorkingArea;
                targetX = area.Left + (area.Width - this.Width) / 2;
                targetY = area.Top + (int)(area.Height * 0.20);
            }

            if (parsedEvents.Count == 0)
            {
                ApplyFormHeight(COLLAPSED_HEIGHT);
            }
            UpdateExpandedFooterText();
            textBox.Enabled = true;

            StartAnimateIn(targetX, targetY);
        }

        private bool IsPointOnAnyScreen(Point pt)
        {
            foreach (Screen scr in Screen.AllScreens)
            {
                Rectangle testRect = new Rectangle(pt.X, pt.Y, Math.Min(100, this.Width), Math.Min(50, this.Height));
                if (scr.WorkingArea.IntersectsWith(testRect))
                    return true;
            }
            return false;
        }

        private void StartAnimateIn(int x, int y)
        {
            animTimer.Stop();
            isHiding = false;
            isAnimating = true;

            animTargetX = x;
            animTargetY = y;
            animStartX = x;
            animStartY = y - 14;

            this.Opacity = 0.0;
            this.Location = new Point(animStartX, animStartY);

            this.Show();
            this.BringToFront();
            SetForegroundWindow(this.Handle);
            this.Activate();

            textBox.Focus();
            textBox.SelectAll();

            animStopwatch.Restart();
            animTimer.Start();
        }

        public void HideQuickAdd(bool immediate = false)
        {
            if (!this.Visible) return;

            if (immediate)
            {
                animTimer.Stop();
                animStopwatch.Stop();
                isAnimating = false;
                isHiding = false;
                this.Hide();
                return;
            }

            animTimer.Stop();
            isHiding = true;
            isAnimating = true;
            animStopwatch.Restart();
            animTimer.Start();
        }

        private void AnimTimer_Tick(object sender, EventArgs e)
        {
            long elapsed = animStopwatch.ElapsedMilliseconds;

            if (isHiding)
            {
                double progress = (double)elapsed / ANIM_OUT_DURATION_MS;
                if (progress >= 1.0)
                {
                    animTimer.Stop();
                    animStopwatch.Stop();
                    isAnimating = false;
                    isHiding = false;
                    this.Hide();
                    this.Opacity = 1.0;
                    return;
                }

                this.Opacity = Math.Max(0.0, 1.0 - progress);
            }
            else
            {
                double progress = (double)elapsed / ANIM_IN_DURATION_MS;
                if (progress >= 1.0)
                {
                    animTimer.Stop();
                    animStopwatch.Stop();
                    isAnimating = false;
                    this.Opacity = 1.0;
                    this.Location = new Point(animTargetX, animTargetY);
                    return;
                }

                // Cubic ease-out
                double ease = 1.0 - Math.Pow(1.0 - progress, 3);
                this.Opacity = Math.Min(1.0, 0.05 + 0.95 * ease);
                int curY = animStartY + (int)((animTargetY - animStartY) * ease);
                this.Location = new Point(animTargetX, curY);
            }
        }

        private void DeletePreviousWord(bool toStartOfLine)
        {
            string txt = textBox.Text;
            int selStart = textBox.SelectionStart;
            int selLen = textBox.SelectionLength;

            if (selLen > 0)
            {
                textBox.Text = txt.Remove(selStart, selLen);
                textBox.SelectionStart = selStart;
                return;
            }

            if (selStart <= 0) return;

            if (toStartOfLine)
            {
                textBox.Text = txt.Substring(selStart);
                textBox.SelectionStart = 0;
                return;
            }

            int i = selStart;
            // 1. Leerzeichen links vom Cursor überspringen
            while (i > 0 && char.IsWhiteSpace(txt[i - 1]))
            {
                i--;
            }

            // 2. Wortzeichen oder Satzzeichen-Block überspringen
            if (i > 0)
            {
                bool isWordChar = char.IsLetterOrDigit(txt[i - 1]);
                while (i > 0 && !char.IsWhiteSpace(txt[i - 1]) && (char.IsLetterOrDigit(txt[i - 1]) == isWordChar))
                {
                    i--;
                }
            }

            textBox.Text = txt.Remove(i, selStart - i);
            textBox.SelectionStart = i;
        }

        private void DeleteNextWord(bool toEndOfLine)
        {
            string txt = textBox.Text;
            int selStart = textBox.SelectionStart;
            int selLen = textBox.SelectionLength;

            if (selLen > 0)
            {
                textBox.Text = txt.Remove(selStart, selLen);
                textBox.SelectionStart = selStart;
                return;
            }

            if (selStart >= txt.Length) return;

            if (toEndOfLine)
            {
                textBox.Text = txt.Substring(0, selStart);
                textBox.SelectionStart = selStart;
                return;
            }

            int i = selStart;
            // 1. Falls direkt Leerzeichen folgen, erst diese überspringen
            while (i < txt.Length && char.IsWhiteSpace(txt[i]))
            {
                i++;
            }

            // 2. Wortzeichen oder Satzzeichen-Block rechts vom Cursor überspringen
            if (i < txt.Length)
            {
                bool isWordChar = char.IsLetterOrDigit(txt[i]);
                while (i < txt.Length && !char.IsWhiteSpace(txt[i]) && (char.IsLetterOrDigit(txt[i]) == isWordChar))
                {
                    i++;
                }
            }

            textBox.Text = txt.Remove(selStart, i - selStart);
            textBox.SelectionStart = selStart;
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Standard-Text-Shortcuts: Strg + Backspace (Wort links löschen), Strg + Entf (Wort rechts löschen), Strg + A, Strg + Z
            if (e.Control && e.KeyCode == Keys.Back)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                DeletePreviousWord(e.Shift);
                return;
            }

            if (e.Control && e.KeyCode == Keys.Delete)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                DeleteNextWord(e.Shift);
                return;
            }

            if (e.Control && e.KeyCode == Keys.A)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                textBox.SelectAll();
                return;
            }

            if (e.Control && e.KeyCode == Keys.Z)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (textBox.CanUndo) textBox.Undo();
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (parsedEvents.Count > 0)
                {
                    // Erstes Esc klappt die Vorschau wieder ein, zweites Esc schließt das Fenster
                    CollapsePreview();
                    UpdateExpandedFooterText();
                }
                else
                {
                    this.HideQuickAdd();
                }
                return;
            }

            if (e.KeyCode == Keys.Tab)
            {
                e.SuppressKeyPress = true;
                string q = textBox.Text.Trim();
                this.HideQuickAdd(true);
                Program.OpenAppWindow("http://localhost:3000" + (string.IsNullOrEmpty(q) ? "" : "?q=" + Uri.EscapeDataString(q)));
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                string query = textBox.Text.Trim();
                if (string.IsNullOrEmpty(query) || isBusy) return;

                // Wenn bereits aufgeklappt und Text unverändert -> Direkt synchronisieren!
                if (parsedEvents.Count > 0 && query == lastParsedQuery)
                {
                    if (e.Shift)
                    {
                        // Shift + Enter übernimmt bei Konflikten automatisch die vorgeschlagenen Slots in der Vorschau
                        bool anyApplied = false;
                        foreach (QuickEventItem ev in parsedEvents)
                        {
                            if (ev.HasConflict && !string.IsNullOrEmpty(ev.SuggestedStartTime) && !string.IsNullOrEmpty(ev.SuggestedEndTime))
                            {
                                ev.StartTime = ev.SuggestedStartTime;
                                ev.EndTime = ev.SuggestedEndTime;
                                ev.HasConflict = false;
                                ev.ConflictWarning = "";
                                anyApplied = true;
                            }
                        }
                        if (anyApplied)
                        {
                            UpdateExpandedFooterText();
                            AnimateToHeight(CalculateDesiredExpandedHeight());
                            return;
                        }
                    }

                    SyncParsedEventsToCalendar();
                    return;
                }

                // Ansonsten: Text analysieren und Fenster mit kompakter Termin-Ansicht aufklappen!
                ParseAndExpandSchedule(query);
            }
        }

        private void ParseAndExpandSchedule(string text)
        {
            isBusy = true;
            statusLabel.Text = "⚡ KI analysiert Termine & prüft Kalender...";
            statusLabel.ForeColor = Color.FromArgb(129, 140, 248);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string nowIso = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
                    string jsonPayload = "{\"text\":" + EscapeJsonString(text) +
                        ",\"timezone\":\"Europe/Berlin\"" +
                        ",\"current_time\":" + EscapeJsonString(nowIso) + "}";
                    byte[] data = Encoding.UTF8.GetBytes(jsonPayload);

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://localhost:8000/api/parse");
                    req.Method = "POST";
                    req.ContentType = "application/json; charset=utf-8";
                    req.ContentLength = data.Length;
                    req.Timeout = 25000;

                    using (Stream stream = req.GetRequestStream())
                    {
                        stream.Write(data, 0, data.Length);
                    }

                    string responseStr = "";
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        responseStr = reader.ReadToEnd();
                    }

                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    Dictionary<string, object> result = serializer.Deserialize<Dictionary<string, object>>(responseStr);

                    List<QuickEventItem> extracted = new List<QuickEventItem>();
                    if (result != null && result.ContainsKey("events") && result["events"] is System.Collections.IEnumerable)
                    {
                        foreach (object itemObj in (System.Collections.IEnumerable)result["events"])
                        {
                            Dictionary<string, object> d = itemObj as Dictionary<string, object>;
                            if (d == null) continue;

                            QuickEventItem ev = new QuickEventItem
                            {
                                Title = GetDictString(d, "title", "Termin"),
                                StartTime = GetDictString(d, "start_time", ""),
                                EndTime = GetDictString(d, "end_time", ""),
                                Category = GetDictString(d, "category", "other"),
                                Location = GetDictString(d, "location", ""),
                                Description = GetDictString(d, "description", ""),
                                HasConflict = d.ContainsKey("has_conflict") && d["has_conflict"] != null && Convert.ToBoolean(d["has_conflict"]),
                                ConflictWith = GetDictString(d, "conflict_with", ""),
                                ConflictWarning = GetDictString(d, "conflict_warning", ""),
                                SuggestedStartTime = GetDictString(d, "suggested_start_time", ""),
                                SuggestedEndTime = GetDictString(d, "suggested_end_time", ""),
                                SuggestedTimeLabel = GetDictString(d, "suggested_time_label", "")
                            };

                            if (d.ContainsKey("reminder_minutes") && d["reminder_minutes"] != null)
                            {
                                try { ev.ReminderMinutes = Convert.ToInt32(d["reminder_minutes"]); } catch { }
                            }

                            extracted.Add(ev);
                        }
                    }

                    this.Invoke((MethodInvoker)delegate
                    {
                        isBusy = false;
                        if (extracted.Count == 0)
                        {
                            CollapsePreview();
                            statusLabel.Text = "❌ Kein Termin im Text erkannt.";
                            statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                            textBox.Focus();
                            return;
                        }

                        parsedEvents = extracted;
                        lastParsedQuery = text;
                        cardsScrollOffset = 0;

                        UpdateExpandedFooterText();
                        int targetH = CalculateDesiredExpandedHeight();
                        AnimateToHeight(targetH);
                        textBox.Focus();
                    });
                }
                catch (Exception ex)
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        isBusy = false;
                        statusLabel.Text = "❌ Fehler bei der KI-Analyse: " + ex.Message;
                        statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                        textBox.Focus();
                    });
                }
            });
        }

        private void SyncParsedEventsToCalendar()
        {
            if (parsedEvents.Count == 0 || isBusy) return;

            isBusy = true;
            textBox.Enabled = false;
            statusLabel.Text = "⚡ Synchronisiere mit Google Calendar...";
            statusLabel.ForeColor = Color.FromArgb(16, 185, 129);

            StringBuilder eventsJson = new StringBuilder();
            eventsJson.Append("[");
            for (int i = 0; i < parsedEvents.Count; i++)
            {
                QuickEventItem ev = parsedEvents[i];
                if (i > 0) eventsJson.Append(",");
                eventsJson.Append("{");
                eventsJson.Append("\"title\":").Append(EscapeJsonString(ev.Title)).Append(",");
                eventsJson.Append("\"start_time\":").Append(EscapeJsonString(ev.StartTime)).Append(",");
                eventsJson.Append("\"end_time\":").Append(EscapeJsonString(ev.EndTime)).Append(",");
                eventsJson.Append("\"category\":").Append(EscapeJsonString(ev.Category)).Append(",");
                eventsJson.Append("\"location\":").Append(string.IsNullOrEmpty(ev.Location) ? "null" : EscapeJsonString(ev.Location)).Append(",");
                eventsJson.Append("\"description\":").Append(string.IsNullOrEmpty(ev.Description) ? "null" : EscapeJsonString(ev.Description)).Append(",");
                eventsJson.Append("\"reminder_minutes\":").Append(ev.ReminderMinutes.HasValue ? ev.ReminderMinutes.Value.ToString() : "null");
                eventsJson.Append("}");
            }
            eventsJson.Append("]");

            string payload = "{\"events\":" + eventsJson.ToString() + ",\"calendar_id\":\"primary\"}";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[] data = Encoding.UTF8.GetBytes(payload);
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://localhost:8000/api/calendar/sync");
                    req.Method = "POST";
                    req.ContentType = "application/json; charset=utf-8";
                    req.ContentLength = data.Length;
                    req.Timeout = 25000;

                    using (Stream stream = req.GetRequestStream())
                    {
                        stream.Write(data, 0, data.Length);
                    }

                    string responseStr = "";
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        responseStr = reader.ReadToEnd();
                    }

                    int count = parsedEvents.Count;
                    string firstTitle = parsedEvents[0].Title;
                    string msg = count == 1
                        ? ("1 Termin eingetragen: '" + firstTitle + "'")
                        : (count + " Termine erfolgreich eingetragen");

                    this.Invoke((MethodInvoker)delegate
                    {
                        isBusy = false;
                        CollapsePreview();
                        statusLabel.Text = "✅ " + msg;
                        statusLabel.ForeColor = Color.FromArgb(74, 222, 128);
                        hintLabel.Text = "[Enter] Vorschau · [Tab] Web-App · [Esc]";
                        hintLabel.ForeColor = Color.FromArgb(100, 100, 115);
                        Program.ShowTrayNotification("Chronos – Termin eingetragen", msg);
                        textBox.Text = "";
                        textBox.Enabled = true;
                        autoCloseTimer.Start();
                    });
                }
                catch (WebException wex)
                {
                    string errDetail = wex.Message;
                    try
                    {
                        if (wex.Response != null)
                        {
                            using (StreamReader r = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                            {
                                string body = r.ReadToEnd();
                                JavaScriptSerializer ser = new JavaScriptSerializer();
                                Dictionary<string, object> errObj = ser.Deserialize<Dictionary<string, object>>(body);
                                if (errObj != null && errObj.ContainsKey("detail"))
                                {
                                    errDetail = errObj["detail"].ToString();
                                }
                            }
                        }
                    }
                    catch { }

                    this.Invoke((MethodInvoker)delegate
                    {
                        isBusy = false;
                        statusLabel.Text = "❌ " + errDetail;
                        statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                        textBox.Enabled = true;
                        textBox.Focus();
                    });
                }
                catch (Exception ex)
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        isBusy = false;
                        statusLabel.Text = "❌ Fehler: " + ex.Message;
                        statusLabel.ForeColor = Color.FromArgb(248, 113, 113);
                        textBox.Enabled = true;
                        textBox.Focus();
                    });
                }
            });
        }

        private static string GetDictString(Dictionary<string, object> d, string key, string fallback)
        {
            if (d != null && d.ContainsKey(key) && d[key] != null)
            {
                return d[key].ToString();
            }
            return fallback;
        }

        private static string EscapeJsonString(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
