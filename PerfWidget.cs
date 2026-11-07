using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PerfWidget
{
    internal static class Native
    {
        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int VK_CONTROL = 0x11;

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int Width; public int Height; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }
    }

    public class PerfWidget : Form
    {
        private const int HorizontalPanelW = 396;
        private const int VerticalPanelW = 200;
        private const int HorizontalMetricsH = 28;
        private const int VerticalMetricsH = 104;
        private const int AppRowCount = 2;
        private const int AppRowH = 22;
        private const int AppSampleIntervalMs = 2000;
        private const int ShadowGap = 4;
        private const float CornerRadius = 14f;
        private const int HoverPollInterval = 60;
        private const int HoverReleaseMargin = 12;

        private static readonly Color Green = Color.FromArgb(61, 220, 132);
        private static readonly Color Amber = Color.FromArgb(255, 193, 7);
        private static readonly Color Red = Color.FromArgb(255, 82, 82);

        private PerformanceCounter cpu;
        private PerformanceCounter ram;

        private float cpuVal = -1f;
        private float ramVal = -1f;
        private float diskVal = -1f;
        private float batteryPct = -1f;
        private int batterySeconds = -1;
        private bool batteryOnAC;

        private Bitmap canvas;
        private Graphics gfx;
        private float scale = 1f;

        private Font labelFont;
        private Font valueFont;

        private readonly Dictionary<string, AppSample> previousApps =
            new Dictionary<string, AppSample>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> appEnergy =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly string[] topAppNames = new string[AppRowCount];
        private readonly float[] topAppCpu = new float[AppRowCount];
        private readonly long[] topAppMemory = new long[AppRowCount];
        private long lastAppSample;
        private bool promptOpen;

        private bool dragging;
        private Point dragOffset;

        private bool verticalLayout;
        private readonly System.Collections.Generic.List<ToolStripMenuItem> layoutItems =
            new System.Collections.Generic.List<ToolStripMenuItem>();
        private readonly System.Collections.Generic.List<ToolStripMenuItem> startupItems =
            new System.Collections.Generic.List<ToolStripMenuItem>();

        private static string LayoutMenuText(bool vertical)
        {
            return vertical
                ? "Passer en disposition en ligne"
                : "Passer en disposition en colonne";
        }

        private int PanelWidth { get { return verticalLayout ? VerticalPanelW : HorizontalPanelW; } }
        private int MetricsHeight { get { return verticalLayout ? VerticalMetricsH : HorizontalMetricsH; } }
        private int PanelHeight { get { return MetricsHeight + AppRowH * AppRowCount; } }
        private int LogicalW { get { return PanelWidth + ShadowGap; } }
        private int LogicalH { get { return PanelHeight + ShadowGap; } }

        private Timer timer;
        private Timer hoverTimer;
        private bool hiddenByHover;
        private NotifyIcon trayIcon;
        private Icon appIcon;

        public PerfWidget()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.SizeAll;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                scale = g.DpiX / 96f;

            LoadSettings();

            int w = (int)(LogicalW * scale);
            int h = (int)(LogicalH * scale);
            Size = new Size(w, h);

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - w - 16, work.Top + 16);

            labelFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);
            valueFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);

            InitCounters();
            BuildTrayIcon();
            StartHoverWatcher();

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            timer = new Timer();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                if (!Visible) return;
                RefreshValues();
                Render();
            };
            timer.Start();

            RefreshValues();
            Render();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            // The widget must never steal the focus from the window underneath.
            get { return true; }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !CtrlDown) return;

            int row = AppRowAt(e.Location);
            if (row >= 0)
            {
                AskCloseApp(row);
                return;
            }

            dragging = true;
            dragOffset = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point cur = Cursor.Position;
                Location = ClampToWorkingArea(new Point(cur.X - dragOffset.X, cur.Y - dragOffset.Y));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
                dragging = false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (timer != null) { timer.Stop(); timer.Dispose(); }
            if (hoverTimer != null) { hoverTimer.Stop(); hoverTimer.Dispose(); }
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            if (appIcon != null) appIcon.Dispose();
            if (cpu != null) cpu.Dispose();
            if (ram != null) ram.Dispose();
            if (gfx != null) gfx.Dispose();
            if (canvas != null) canvas.Dispose();
            if (labelFont != null) labelFont.Dispose();
            if (valueFont != null) valueFont.Dispose();
        }

        private void InitCounters()
        {
            try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
            catch { cpu = null; }

            try { ram = new PerformanceCounter("Memory", "% Committed Bytes In Use"); }
            catch { ram = null; }

            Prime(cpu);
            Prime(ram);
        }

        private static void Prime(PerformanceCounter pc)
        {
            if (pc == null) return;
            try { pc.NextValue(); } catch { }
        }

        private static bool CtrlDown
        {
            // GetAsyncKeyState is required here: the widget is never the foreground
            // window, so Control.ModifierKeys would not reflect the real key state.
            get { return (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0; }
        }

        private void StartHoverWatcher()
        {
            hoverTimer = new Timer();
            hoverTimer.Interval = HoverPollInterval;
            hoverTimer.Tick += delegate { UpdateHoverVisibility(); };
            hoverTimer.Start();
        }

        private void UpdateHoverVisibility()
        {
            if (IsDisposed || !IsHandleCreated) return;

            Point cursor = Cursor.Position;

            if (!Visible)
            {
                if (!hiddenByHover) return;

                // While Ctrl is held the widget is revealed so it can be dragged;
                // otherwise it only comes back once the pointer has clearly left it.
                if (!CtrlDown && HoverZone.Contains(cursor)) return;

                hiddenByHover = false;
                Visible = true;
                RefreshValues();
                Render();
                return;
            }

            if (promptOpen || dragging || CtrlDown) return;

            if (Bounds.Contains(cursor))
            {
                hiddenByHover = true;
                Visible = false;
            }
        }

        private Rectangle HoverZone
        {
            get
            {
                Rectangle zone = Bounds;
                int margin = (int)(HoverReleaseMargin * scale);
                zone.Inflate(margin, margin);
                return zone;
            }
        }

        private ToolStripMenuItem BuildLayoutItem()
        {
            ToolStripMenuItem item = new ToolStripMenuItem(LayoutMenuText(verticalLayout), null,
                delegate { SetVerticalLayout(!verticalLayout); });

            layoutItems.Add(item);
            return item;
        }

        private void SetVerticalLayout(bool vertical)
        {
            if (verticalLayout == vertical) return;

            verticalLayout = vertical;
            SyncLayoutTexts();
            ApplyLayout();
            SaveSettings();
        }

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath),
                    "settings.ini");
            }
        }

        private void LoadSettings()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path)) return;

                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (string.Equals(key, "Layout", StringComparison.OrdinalIgnoreCase))
                        verticalLayout = string.Equals(value, "Vertical", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                string path = SettingsPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path,
                    "Layout=" + (verticalLayout ? "Vertical" : "Horizontal") + "\r\n");
            }
            catch { }
        }

        private void SyncLayoutTexts()
        {
            string text = LayoutMenuText(verticalLayout);
            foreach (ToolStripMenuItem item in layoutItems) item.Text = text;
        }

        private ToolStripMenuItem BuildStartupMenu()
        {
            ToolStripMenuItem item = new ToolStripMenuItem("Démarrer avec Windows");
            item.Checked = IsStartupEnabled;
            item.Click += delegate { ToggleStartup(); };
            startupItems.Add(item);
            return item;
        }

        private void ToggleStartup()
        {
            bool enable = !IsStartupEnabled;
            try
            {
                SetStartupEnabled(enable);
            }
            catch (Exception ex)
            {
                string message = "Impossible de modifier le démarrage automatique :\n" + ex.Message;
                if (Visible)
                    MessageBox.Show(this, message, "PerfWidget", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    MessageBox.Show(message, "PerfWidget", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            SyncStartupChecks();
        }

        private void SyncStartupChecks()
        {
            bool enabled = IsStartupEnabled;
            foreach (ToolStripMenuItem item in startupItems) item.Checked = enabled;
        }

        private static string StartupShortcutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "PerfWidget.lnk");
            }
        }

        private static bool IsStartupEnabled
        {
            get
            {
                try
                {
                    string shortcut = StartupShortcutPath;
                    if (!File.Exists(shortcut)) return false;

                    object shell = CreateShell();
                    object link = null;
                    try
                    {
                        link = OpenShortcut(shell, shortcut);
                        string target = GetShortcutProperty(link, "TargetPath") as string;
                        return target != null &&
                            string.Equals(Path.GetFullPath(target),
                                Path.GetFullPath(Application.ExecutablePath),
                                StringComparison.OrdinalIgnoreCase);
                    }
                    finally
                    {
                        ReleaseComObject(link);
                        ReleaseComObject(shell);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void SetStartupEnabled(bool enable)
        {
            string shortcut = StartupShortcutPath;

            if (!enable)
            {
                if (File.Exists(shortcut)) File.Delete(shortcut);
                return;
            }

            string exe = Application.ExecutablePath;
            object shell = CreateShell();
            object link = null;
            try
            {
                link = OpenShortcut(shell, shortcut);
                SetShortcutProperty(link, "TargetPath", exe);
                SetShortcutProperty(link, "WorkingDirectory", Path.GetDirectoryName(exe));
                SetShortcutProperty(link, "Description", "Moniteur de performance");
                SetShortcutProperty(link, "IconLocation", exe + ",0");
                SetShortcutProperty(link, "WindowStyle", 1);
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                ReleaseComObject(link);
                ReleaseComObject(shell);
            }
        }

        private static object CreateShell()
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("WScript.Shell est introuvable sur ce système.");
            return Activator.CreateInstance(shellType);
        }

        private static object OpenShortcut(object shell, string path)
        {
            return shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { path });
        }

        private static object GetShortcutProperty(object link, string name)
        {
            try
            {
                return link.GetType().InvokeMember(name, BindingFlags.GetProperty, null, link, null);
            }
            catch
            {
                return null;
            }
        }

        private static void SetShortcutProperty(object link, string name, object value)
        {
            link.GetType().InvokeMember(name, BindingFlags.SetProperty, null, link, new[] { value });
        }

        private static void ReleaseComObject(object comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                    Marshal.ReleaseComObject(comObject);
            }
            catch { }
        }

        private void ApplyLayout()
        {
            int w = (int)(LogicalW * scale);
            int h = (int)(LogicalH * scale);

            Size = new Size(w, h);
            Location = ClampToWorkingArea(Location);

            RefreshValues();
            Render();
        }

        private Point ClampToWorkingArea(Point location)
        {
            Rectangle work = Screen.FromRectangle(new Rectangle(location, Size)).WorkingArea;

            int maxX = Math.Max(work.Left, work.Right - Width);
            int maxY = Math.Max(work.Top, work.Bottom - Height);

            return new Point(
                Math.Min(Math.Max(location.X, work.Left), maxX),
                Math.Min(Math.Max(location.Y, work.Top), maxY));
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;

            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    Location = ClampToWorkingArea(Location);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void BuildTrayIcon()
        {
            appIcon = CreateTrayIcon();
            trayIcon = new NotifyIcon();
            trayIcon.Icon = appIcon;
            trayIcon.Text = "Moniteur de performance";
            trayIcon.Visible = true;
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                    ToggleVisible();
            };

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Afficher", null, delegate { ShowWidget(); });
            trayMenu.Items.Add("Minimiser dans la barre d'état", null, delegate { MinimizeToTray(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(BuildLayoutItem());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(BuildStartupMenu());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("À propos", null, delegate { ShowAbout(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Fermer", null, delegate { Application.Exit(); });
            trayMenu.Opening += delegate { SyncLayoutTexts(); SyncStartupChecks(); };
            trayIcon.ContextMenuStrip = trayMenu;
        }

        private void ShowAbout()
        {
            MessageBox.Show(this,
                "Grégory Hargous\n" +
                "gregory.hargous@gmail.com\n" +
                "gregland.net",
                "À propos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void MinimizeToTray()
        {
            hiddenByHover = false;
            Visible = false;
        }

        private void ShowWidget()
        {
            hiddenByHover = false;
            Location = ClampToWorkingArea(Location);
            Visible = true;
            Activate();
        }

        private void ToggleVisible()
        {
            if (Visible) MinimizeToTray();
            else ShowWidget();
        }

        private void RefreshValues()
        {
            cpuVal = Read(cpu);
            ramVal = Read(ram);

            try
            {
                DriveInfo c = new DriveInfo("C");
                long total = c.TotalSize;
                long free = c.AvailableFreeSpace;
                diskVal = total > 0
                    ? Clamp((float)((total - free) / (double)total * 100.0))
                    : -1f;
            }
            catch { diskVal = -1f; }

            try
            {
                PowerStatus ps = SystemInformation.PowerStatus;
                batteryOnAC = ps.PowerLineStatus == PowerLineStatus.Online;
                batteryPct = ps.BatteryLifePercent >= 0f && ps.BatteryLifePercent <= 1f
                    ? Clamp(ps.BatteryLifePercent * 100f)
                    : -1f;
                batterySeconds = ps.BatteryLifeRemaining;
            }
            catch
            {
                batteryOnAC = false;
                batteryPct = -1f;
                batterySeconds = -1;
            }

            RefreshApps();
        }

        private static float Read(PerformanceCounter pc)
        {
            if (pc == null) return -1f;
            try { return Clamp(pc.NextValue()); }
            catch { return -1f; }
        }

        private static float Clamp(float v)
        {
            if (v < 0f) return 0f;
            if (v > 100f) return 100f;
            return v;
        }

        private static string FormatPercent(float v)
        {
            return v < 0f ? "--" : ((int)Math.Round(v)).ToString() + "%";
        }

        private string FormatBatteryText()
        {
            if (batteryOnAC) return "AC";
            if (batterySeconds <= 0) return "--";

            TimeSpan t = TimeSpan.FromSeconds(batterySeconds);
            if (t.TotalHours >= 1.0)
                return string.Format("{0}h{1:00}", (int)t.TotalHours, t.Minutes);

            int mins = (int)Math.Ceiling(t.TotalMinutes);
            return mins.ToString() + "m";
        }

        private sealed class AppSample
        {
            public long CpuTicks;
            public long Memory;
        }

        // Energy is not exposed per application by Windows without an elevated ETW
        // session, so CPU time is used as the estimation basis. Processes from the
        // system session and shell/system processes are never listed or closed.
        private static readonly HashSet<string> ProtectedProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system", "idle", "registry", "memory compression", "secure system",
                "csrss", "smss", "wininit", "winlogon", "services", "lsass", "lsaiso",
                "svchost", "explorer", "dwm", "fontdrvhost", "ctfmon", "sihost",
                "taskhostw", "runtimebroker", "searchindexer", "applicationframehost",
                "perfwidget"
            };

        private void RefreshApps()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastAppSample != 0 &&
                (now - lastAppSample) * 1000.0 / Stopwatch.Frequency < AppSampleIntervalMs)
                return;

            SampleTopApps(now);
        }

        private void SampleTopApps(long now)
        {
            double seconds = lastAppSample == 0
                ? 0.0
                : (now - lastAppSample) / (double)Stopwatch.Frequency;
            lastAppSample = now;

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return; }

            int selfPid;
            int sessionId;
            using (Process self = Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                sessionId = self.SessionId;
            }

            Dictionary<string, AppSample> current =
                new Dictionary<string, AppSample>(StringComparer.OrdinalIgnoreCase);

            foreach (Process p in processes)
            {
                try
                {
                    if (p.Id == selfPid || p.SessionId != sessionId) continue;
                    if (ProtectedProcesses.Contains(p.ProcessName)) continue;

                    long ticks;
                    try { ticks = p.TotalProcessorTime.Ticks; }
                    catch { continue; }

                    long memory = 0;
                    try { memory = p.WorkingSet64; }
                    catch { }

                    AppSample sample;
                    if (!current.TryGetValue(p.ProcessName, out sample))
                    {
                        sample = new AppSample();
                        current[p.ProcessName] = sample;
                    }
                    sample.CpuTicks += ticks;
                    sample.Memory += memory;
                }
                catch { }
                finally { p.Dispose(); }
            }

            // Processor time available to every process during the window, in ticks.
            double capacity = seconds * Environment.ProcessorCount * TimeSpan.TicksPerSecond;

            foreach (KeyValuePair<string, AppSample> pair in current)
            {
                float raw = 0f;
                AppSample previous;
                if (capacity > 0.0 && previousApps.TryGetValue(pair.Key, out previous))
                {
                    long delta = pair.Value.CpuTicks - previous.CpuTicks;
                    if (delta > 0)
                        raw = (float)Math.Min(100.0, delta / capacity * 100.0);
                }

                float smoothed;
                if (appEnergy.TryGetValue(pair.Key, out smoothed))
                    smoothed = smoothed * 0.5f + raw * 0.5f;
                else
                    smoothed = raw;

                appEnergy[pair.Key] = smoothed;
            }

            List<string> vanished = new List<string>();
            foreach (string known in appEnergy.Keys)
                if (!current.ContainsKey(known)) vanished.Add(known);
            foreach (string gone in vanished) appEnergy.Remove(gone);

            previousApps.Clear();
            foreach (KeyValuePair<string, AppSample> pair in current)
                previousApps[pair.Key] = pair.Value;

            List<KeyValuePair<string, float>> ranked = new List<KeyValuePair<string, float>>(appEnergy);
            ranked.Sort(delegate(KeyValuePair<string, float> a, KeyValuePair<string, float> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            for (int i = 0; i < AppRowCount; i++)
            {
                if (i < ranked.Count && ranked[i].Value > 0.05f)
                {
                    topAppNames[i] = ranked[i].Key;
                    topAppCpu[i] = ranked[i].Value;
                    topAppMemory[i] = current[ranked[i].Key].Memory;
                }
                else
                {
                    topAppNames[i] = null;
                    topAppCpu[i] = 0f;
                    topAppMemory[i] = 0L;
                }
            }
        }

        private static string FormatMemory(long bytes)
        {
            if (bytes <= 0) return "--";
            return (bytes / (1024 * 1024)).ToString("N0") + " Mo";
        }

        private static string Trim(Graphics g, string text, Font font, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return string.Empty;
            if (g.MeasureString(text, font).Width <= maxWidth) return text;

            for (int length = text.Length - 1; length > 0; length--)
            {
                string candidate = text.Substring(0, length) + "...";
                if (g.MeasureString(candidate, font).Width <= maxWidth) return candidate;
            }
            return "...";
        }

        private void AskCloseApp(int index)
        {
            string name = topAppNames[index];
            if (string.IsNullOrEmpty(name)) return;

            DialogResult answer;
            promptOpen = true;
            try
            {
                answer = MessageBox.Show(this,
                    "Demander la fermeture de « " + name + " » ?",
                    "PerfWidget",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
            }
            finally
            {
                promptOpen = false;
            }

            if (answer != DialogResult.Yes) return;

            string processName = name;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                TerminateApplications(processName);
            });
        }

        private static void TerminateApplications(string processName)
        {
            int selfPid;
            int sessionId;
            using (Process self = Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                sessionId = self.SessionId;
            }

            Process[] found;
            try { found = Process.GetProcessesByName(processName); }
            catch { return; }

            List<Process> pending = new List<Process>();
            foreach (Process p in found)
            {
                bool asked = false;
                try
                {
                    if (p.Id != selfPid && p.SessionId == sessionId)
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            p.CloseMainWindow();
                            asked = true;
                        }
                        else
                        {
                            p.Kill();
                        }
                    }
                }
                catch { }

                if (asked) pending.Add(p);
                else p.Dispose();
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            bool alive = pending.Count > 0;
            while (alive && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(100);
                alive = false;
                foreach (Process p in pending)
                {
                    try
                    {
                        if (!p.HasExited) { alive = true; break; }
                    }
                    catch { }
                }
            }

            foreach (Process p in pending)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch { }
                p.Dispose();
            }
        }

        private void Render()
        {
            int w = Width;
            int h = Height;
            if (canvas == null || canvas.Width != w || canvas.Height != h)
            {
                if (canvas != null) canvas.Dispose();
                if (gfx != null) gfx.Dispose();
                canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                gfx = Graphics.FromImage(canvas);
            }

            gfx.Clear(Color.Transparent);
            gfx.ResetTransform();
            gfx.ScaleTransform(scale, scale);
            gfx.SmoothingMode = SmoothingMode.AntiAlias;
            gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Draw(gfx);

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = canvas.GetHbitmap(Color.FromArgb(0));
                oldBitmap = Native.SelectObject(memDc, hBitmap);

                Native.POINT dst = new Native.POINT { X = Left, Y = Top };
                Native.POINT src = new Native.POINT { X = 0, Y = 0 };
                Native.SIZE size = new Native.SIZE { Width = w, Height = h };
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                    memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, oldBitmap);
                    Native.DeleteObject(hBitmap);
                }
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void Draw(Graphics g)
        {
            float panelW = PanelWidth;
            float panelH = PanelHeight;

            // Drop shadow
            RectangleF shadow = new RectangleF(ShadowGap, ShadowGap, panelW, panelH);
            using (GraphicsPath sp = Rounded(shadow, CornerRadius))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillPath(sb, sp);

            // Panel
            RectangleF panel = new RectangleF(0f, 0f, panelW, panelH);
            using (GraphicsPath pp = Rounded(panel, CornerRadius))
            {
                using (LinearGradientBrush bg = new LinearGradientBrush(panel,
                    Color.FromArgb(45, 46, 66), Color.FromArgb(28, 29, 42), 90f))
                    g.FillPath(bg, pp);

                using (Pen border = new Pen(Color.FromArgb(90, 92, 118), 1.2f))
                    g.DrawPath(border, pp);
            }

            DrawSegment(g, "CPU", cpuVal, FormatPercent(cpuVal), 0);
            DrawSegment(g, "RAM", ramVal, FormatPercent(ramVal), 1);
            DrawSegment(g, "DISK", diskVal, FormatPercent(diskVal), 2);
            DrawSegment(g, "BAT", batteryPct, FormatBatteryText(), 3);

            for (int i = 0; i < AppRowCount; i++)
                DrawAppRow(g, i);
        }

        private RectangleF AppRowRect(int index)
        {
            return new RectangleF(0f, MetricsHeight + index * AppRowH, PanelWidth, AppRowH);
        }

        private int AppRowAt(Point location)
        {
            for (int i = 0; i < AppRowCount; i++)
            {
                RectangleF row = AppRowRect(i);
                RectangleF device = new RectangleF(
                    row.X * scale, row.Y * scale, row.Width * scale, row.Height * scale);
                if (device.Contains(location)) return i;
            }
            return -1;
        }

        private void DrawAppRow(Graphics g, int index)
        {
            RectangleF row = AppRowRect(index);
            float panelW = PanelWidth;
            float midY = row.Y + row.Height / 2f;

            using (Pen sep = new Pen(Color.FromArgb(60, 61, 82), 1f))
                g.DrawLine(sep, 9f, row.Y, panelW - 9f, row.Y);

            string name = topAppNames[index];
            bool known = !string.IsNullOrEmpty(name);

            Color indicator = known ? BarColor(topAppCpu[index]) : Color.FromArgb(90, 92, 118);
            using (GraphicsPath ip = Rounded(new RectangleF(row.X + 12f, midY - 5f, 5f, 10f), 2.5f))
            using (SolidBrush ib = new SolidBrush(indicator))
                g.FillPath(ib, ip);

            string resources = known
                ? FormatPercent(topAppCpu[index]) + "  " + FormatMemory(topAppMemory[index])
                : "--";

            float resW = g.MeasureString(resources, valueFont).Width;
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush rb = new SolidBrush(Color.FromArgb(200, 201, 216)))
                    g.DrawString(resources, valueFont, rb,
                        new RectangleF(panelW - 12f - resW, row.Y, resW, row.Height), sf);
            }

            float nameX = row.X + 24f;
            float nameW = (panelW - 12f - resW - 10f) - nameX;
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush nb = new SolidBrush(known ? Color.White : Color.FromArgb(158, 159, 178)))
                    g.DrawString(known ? Trim(g, name, labelFont, nameW) : "--", labelFont, nb,
                        new RectangleF(nameX, row.Y, Math.Max(nameW, 8f), row.Height), sf);
            }
        }

        private void DrawSegment(Graphics g, string label, float val, string text, int index)
        {
            float panelW = PanelWidth;
            float metricsH = MetricsHeight;

            RectangleF cell = verticalLayout
                ? new RectangleF(0f, index * (metricsH / 4f), panelW, metricsH / 4f)
                : new RectangleF(index * (panelW / 4f), 0f, panelW / 4f, metricsH);

            // Separator
            if (index > 0)
            {
                using (Pen sep = new Pen(Color.FromArgb(60, 61, 82), 1f))
                {
                    if (verticalLayout)
                        g.DrawLine(sep, 9f, cell.Y, panelW - 9f, cell.Y);
                    else
                        g.DrawLine(sep, cell.X, 9f, cell.X, metricsH - 9f);
                }
            }

            float midY = cell.Y + cell.Height / 2f;

            // Status indicator
            Color c = val >= 0f ? BarColor(val) : Color.FromArgb(90, 92, 118);
            using (GraphicsPath ip = Rounded(new RectangleF(cell.X + 12f, midY - 6f, 6f, 12f), 3f))
            using (SolidBrush ib = new SolidBrush(c))
                g.FillPath(ib, ip);

            // Label
            using (SolidBrush lb = new SolidBrush(Color.FromArgb(158, 159, 178)))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(label, labelFont, lb,
                    new RectangleF(cell.X + 24f, cell.Y, 34f, cell.Height), sf);
            }

            // Value
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush vb = new SolidBrush(val >= 0f ? Color.White : Color.FromArgb(158, 159, 178)))
                    g.DrawString(text, valueFont, vb,
                        new RectangleF(cell.Right - 58f, cell.Y, 48f, cell.Height), sf);
            }
        }

        private static Icon CreateTrayIcon()
        {
            Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (GraphicsPath bgp = Rounded(new RectangleF(0.5f, 0.5f, 15f, 15f), 4f))
                using (SolidBrush bgb = new SolidBrush(Color.FromArgb(44, 45, 66)))
                    g.FillPath(bgb, bgp);

                DrawIconBar(g, 4f, Green);
                DrawIconBar(g, 7f, Amber);
                DrawIconBar(g, 10f, Red);
            }

            IntPtr h = bmp.GetHicon();
            Icon ic;
            try { ic = (Icon)Icon.FromHandle(h).Clone(); }
            finally { Native.DestroyIcon(h); }
            bmp.Dispose();
            return ic;
        }

        private static void DrawIconBar(Graphics g, float y, Color c)
        {
            using (GraphicsPath p = Rounded(new RectangleF(3f, y, 10f, 2f), 1f))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        private static Color BarColor(float v)
        {
            float x = Clamp(v);
            if (x < 60f) return Lerp(Green, Amber, x / 60f);
            return Lerp(Amber, Red, (x - 60f) / 40f);
        }

        private static Color Lerp(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static Color Lighten(Color c, float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            return Color.FromArgb(
                (int)(c.R + (255 - c.R) * amount),
                (int)(c.G + (255 - c.G) * amount),
                (int)(c.B + (255 - c.B) * amount));
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PerfWidget());
        }
    }
}
