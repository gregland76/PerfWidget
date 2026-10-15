using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
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
        private const int LogicalW = 300;
        private const int LogicalH = 32;

        private static readonly Color Green = Color.FromArgb(61, 220, 132);
        private static readonly Color Amber = Color.FromArgb(255, 193, 7);
        private static readonly Color Red = Color.FromArgb(255, 82, 82);

        private PerformanceCounter cpu;
        private PerformanceCounter ram;

        private float cpuVal = -1f;
        private float ramVal = -1f;
        private float diskVal = -1f;

        private Bitmap canvas;
        private Graphics gfx;
        private float scale = 1f;

        private Font labelFont;
        private Font valueFont;

        private bool dragging;
        private Point dragOffset;

        private Timer timer;
        private ContextMenuStrip widgetMenu;
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

            int w = (int)(LogicalW * scale);
            int h = (int)(LogicalH * scale);
            Size = new Size(w, h);

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - w - 16, work.Top + 16);

            labelFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);
            valueFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);

            InitCounters();
            BuildWidgetMenu();
            BuildTrayIcon();

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

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragOffset = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point cur = Cursor.Position;
                Location = new Point(cur.X - dragOffset.X, cur.Y - dragOffset.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
                dragging = false;
            else if (e.Button == MouseButtons.Right)
                widgetMenu.Show(this, e.Location);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (timer != null) { timer.Stop(); timer.Dispose(); }
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

        private void BuildWidgetMenu()
        {
            widgetMenu = new ContextMenuStrip();
            widgetMenu.Items.Add("Minimiser dans la barre d'état", null, delegate { MinimizeToTray(); });
            widgetMenu.Items.Add(new ToolStripSeparator());
            widgetMenu.Items.Add("À propos", null, delegate { ShowAbout(); });
            widgetMenu.Items.Add(new ToolStripSeparator());
            widgetMenu.Items.Add("Fermer", null, delegate { Application.Exit(); });
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
            trayMenu.Items.Add("À propos", null, delegate { ShowAbout(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Fermer", null, delegate { Application.Exit(); });
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
            Visible = false;
        }

        private void ShowWidget()
        {
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
            // Drop shadow
            RectangleF shadow = new RectangleF(4f, 4f, 296f, 28f);
            using (GraphicsPath sp = Rounded(shadow, 14f))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillPath(sb, sp);

            // Panel
            RectangleF panel = new RectangleF(0f, 0f, 296f, 28f);
            using (GraphicsPath pp = Rounded(panel, 14f))
            {
                using (LinearGradientBrush bg = new LinearGradientBrush(panel,
                    Color.FromArgb(45, 46, 66), Color.FromArgb(28, 29, 42), 90f))
                    g.FillPath(bg, pp);

                using (Pen border = new Pen(Color.FromArgb(90, 92, 118), 1.2f))
                    g.DrawPath(border, pp);
            }

            DrawSegment(g, "CPU", cpuVal, 0);
            DrawSegment(g, "RAM", ramVal, 1);
            DrawSegment(g, "DISK", diskVal, 2);
        }

        private void DrawSegment(Graphics g, string label, float val, int index)
        {
            const float panelW = 296f;
            const float panelH = 28f;
            float colW = panelW / 3f;
            float x = index * colW;

            // Separator
            if (index > 0)
            {
                using (Pen sep = new Pen(Color.FromArgb(60, 61, 82), 1f))
                    g.DrawLine(sep, x, 9f, x, panelH - 9f);
            }

            // Status indicator
            Color c = val >= 0f ? BarColor(val) : Color.FromArgb(90, 92, 118);
            using (GraphicsPath ip = Rounded(new RectangleF(x + 12f, 8f, 6f, 12f), 3f))
            using (SolidBrush ib = new SolidBrush(c))
                g.FillPath(ib, ip);

            // Label
            using (SolidBrush lb = new SolidBrush(Color.FromArgb(158, 159, 178)))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(label, labelFont, lb, new RectangleF(x + 24f, 0f, 34f, panelH), sf);
            }

            // Value
            string txt = val < 0f ? "--" : ((int)Math.Round(val)).ToString() + "%";
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush vb = new SolidBrush(val >= 0f ? Color.White : Color.FromArgb(158, 159, 178)))
                    g.DrawString(txt, valueFont, vb,
                        new RectangleF(x + colW - 40f, 0f, 30f, panelH), sf);
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
