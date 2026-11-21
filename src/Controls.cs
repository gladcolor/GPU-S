// Custom-drawn dark UI: theme, tab strip, pill buttons, hero header with live tiles,
// spec cards and the sensor list. Plain GDI drawing only - no extra libraries.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GpuS
{
    internal static class Theme
    {
        // Light theme.
        public static readonly Color Bg = Color.FromArgb(0xF3, 0xF4, 0xF6);
        public static readonly Color Surface = Color.White;
        public static readonly Color Card = Color.White;
        public static readonly Color CardHover = Color.FromArgb(0xEC, 0xEF, 0xF2);
        public static readonly Color Border = Color.FromArgb(0xDC, 0xDF, 0xE4);
        public static readonly Color Text = Color.FromArgb(0x1F, 0x23, 0x28);
        public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
        public static readonly Color Faint = Color.FromArgb(0xC4, 0xC9, 0xD0);
        public static Color Accent = Color.FromArgb(0x5E, 0x9E, 0x00);

        public static readonly Font Base = SystemFonts.MessageBoxFont;
        public static readonly Font Small = new Font(Base.FontFamily, Base.Size * 0.9f);
        public static readonly Font Semi = new Font("Segoe UI Semibold", Base.Size);
        public static readonly Font Title = new Font("Segoe UI Semibold", Base.Size * 1.65f);
        public static readonly Font Big = new Font("Segoe UI Semibold", Base.Size * 1.85f);
        public static readonly Font Mid = new Font("Segoe UI Semibold", Base.Size * 1.5f);
        public static readonly Color CpuColor = Color.FromArgb(0x11, 0x7D, 0xBB);   // Task Manager-like blue
        public static readonly Color RamColor = Color.FromArgb(0x8B, 0x3F, 0xC9);   // Task Manager-like purple
        public static readonly Font Mono = new Font("Consolas", Base.Size);

        public static Color VendorColor(uint vendorId)
        {
            switch (vendorId)
            {
                case 0x10DE: return Color.FromArgb(0x5E, 0x9E, 0x00);              // NVIDIA
                case 0x1002: case 0x1022: return Color.FromArgb(0xD9, 0x26, 0x2B); // AMD
                case 0x8086: return Color.FromArgb(0x00, 0x6C, 0xD0);              // Intel
                default: return Color.FromArgb(0x7A, 0x5C, 0xE0);
            }
        }

        // Black or white, whichever reads better on the given fill.
        public static Color OnColor(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 160 ? Color.FromArgb(0x12, 0x14, 0x16) : Color.White;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
            g.SmoothingMode = old;
        }

        public static void Draw(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        // Area sparkline with a soft gradient; newest sample on the right.
        public static void Spark(Graphics g, Sensor s, Rectangle r, int step)
        {
            if (s == null || r.Width < 8 || r.Height < 4) return;
            int n = Math.Min(s.HistoryCount, r.Width / step + 1);
            if (n < 2) return;
            double max = s.FixedMax;
            if (max <= 0)
            {
                for (int i = s.HistoryCount - n; i < s.HistoryCount; i++) max = Math.Max(max, s.HistoryAt(i));
                max = max <= 0 ? 1 : max * 1.15;
            }
            var pts = new PointF[n + 2];
            float right = r.Right - 1;
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(0, Math.Min(max, s.HistoryAt(s.HistoryCount - n + i)));
                pts[i] = new PointF(right - (n - 1 - i) * step, (float)(r.Bottom - 1 - v / max * (r.Height - 2)));
            }
            pts[n] = new PointF(right, r.Bottom);
            pts[n + 1] = new PointF(pts[0].X, r.Bottom);

            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2),
                Color.FromArgb(120, Accent), Color.FromArgb(8, Accent), LinearGradientMode.Vertical))
                g.FillPolygon(b, pts);
            var line = new PointF[n];
            Array.Copy(pts, line, n);
            using (var p = new Pen(Accent, 1.6f)) g.DrawLines(p, line);
            g.SmoothingMode = old;
        }

        // Task-Manager-style graph: fixed scale, light grid, filled area, newest sample on the right.
        // 'window' = number of samples spanning the full width. Long windows are reduced to one point per
        // pixel column (max of the samples in that column), so a 1-hour graph costs the same as a 1-minute one.
        public static readonly Color Warn = Color.FromArgb(0xD9, 0x7A, 0x00);
        public static readonly Color Danger = Color.FromArgb(0xD9, 0x2D, 0x20);

        public static readonly Color NetColor = Color.FromArgb(0x00, 0x86, 0x7A);   // teal: distinct from GPU green and warning orange

        // Largest value among the newest 'window' samples.
        public static double WindowMax(Sensor s, int window)
        {
            double max = 0;
            if (s == null) return 0;
            int avail = Math.Min(window, s.HistoryCount);
            for (int a = 0; a < avail; a++) max = Math.Max(max, s.HistoryAt(s.HistoryCount - 1 - a));
            return max;
        }

        public static void TaskGraph(Graphics g, Rectangle r, Sensor s, double max, int window, Color color)
        {
            TaskGraph(g, r, s, max, window, color, null);
        }

        // 'dashed' (optional) is a second series drawn as a dashed line on the same scale (network send).
        public static void TaskGraph(Graphics g, Rectangle r, Sensor s, double max, int window, Color color, Sensor dashed)
        {
            using (var b = new SolidBrush(Mix(Color.White, color, 0.04))) g.FillRectangle(b, r);
            using (var grid = new Pen(Color.FromArgb(40, color)))
            {
                for (int i = 1; i < 10; i++) { int x = r.X + r.Width * i / 10; g.DrawLine(grid, x, r.Y, x, r.Bottom); }
                for (int i = 1; i < 5; i++) { int y = r.Y + r.Height * i / 5; g.DrawLine(grid, r.X, y, r.Right, y); }
            }
            if (s != null && max <= 0) max = s.FixedMax;
            if (s != null && max <= 0) { max = WindowMax(s, window); max = max <= 0 ? 1 : max * 1.15; }
            PointF[] line = Line(r, s, max, window);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (line != null)
            {
                var poly = new PointF[line.Length + 2];
                Array.Copy(line, poly, line.Length);
                poly[line.Length] = new PointF(line[line.Length - 1].X, r.Bottom - 1);
                poly[line.Length + 1] = new PointF(line[0].X, r.Bottom - 1);
                using (var b = new SolidBrush(Color.FromArgb(55, color))) g.FillPolygon(b, poly);
                using (var p = new Pen(color, 1.5f)) g.DrawLines(p, line);
            }
            PointF[] line2 = Line(r, dashed, max, window);
            if (line2 != null)
                using (var p = new Pen(Mix(color, Text, 0.35), 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLines(p, line2);
            g.SmoothingMode = old;
            using (var p = new Pen(Color.FromArgb(170, color))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        // Newest sample at the right edge; one point per pixel column (max of the samples in it).
        private static PointF[] Line(Rectangle r, Sensor s, double max, int window)
        {
            if (s == null || s.HistoryCount < 2 || window < 2 || r.Width <= 4 || max <= 0) return null;
            int avail = Math.Min(window, s.HistoryCount);
            int cols = Math.Max(2, Math.Min(window, r.Width));
            double perCol = window / (double)cols;
            float step = (r.Width - 1) / (float)(cols - 1);
            var pts = new List<PointF>(cols);
            for (int c = cols - 1; c >= 0; c--)
            {
                int a0 = (int)((cols - 1 - c) * perCol);
                if (a0 >= avail) break;
                int a1 = Math.Min(avail, Math.Max(a0 + 1, (int)((cols - c) * perCol)));
                double v = 0;
                for (int a = a0; a < a1; a++) v = Math.Max(v, s.HistoryAt(s.HistoryCount - 1 - a));
                v = Math.Max(0, Math.Min(max, v));
                pts.Add(new PointF(r.X + c * step, (float)(r.Bottom - 1 - v / max * (r.Height - 2))));
            }
            if (pts.Count < 2) return null;
            pts.Reverse();
            return pts.ToArray();
        }
    }

    // ------------------------------------------------------------------ base for scrollable custom views

    internal class DarkScroll : Panel
    {
        public DarkScroll()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            BackColor = Theme.Bg;
            Font = Theme.Base;
        }

        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

        protected void SetContentHeight(int h)
        {
            var s = new Size(0, h);
            if (AutoScrollMinSize != s) AutoScrollMinSize = s;
        }
    }

    // ------------------------------------------------------------------ tab strip

    internal sealed class TabStrip : Control
    {
        private readonly string[] items;
        private readonly Rectangle[] rects;
        private int selected, hover = -1;
        public event EventHandler SelectedChanged;

        public TabStrip(params string[] items)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            this.items = items;
            rects = new Rectangle[items.Length];
            Font = Theme.Semi;
            Height = Theme.Base.Height * 2 + 4;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected || value < 0 || value >= items.Length) return;
                selected = value;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            int pad = Font.Height, x = pad / 2;
            for (int i = 0; i < items.Length; i++)
            {
                int w = TextRenderer.MeasureText(items[i], Font).Width + pad;
                rects[i] = new Rectangle(x, 0, w, Height);
                Color c = i == selected ? Theme.Text : i == hover ? Theme.Mix(Theme.Muted, Theme.Text, 0.6) : Theme.Muted;
                Theme.Draw(g, items[i], Font, rects[i], c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (i == selected)
                    Theme.FillRound(g, new Rectangle(x + pad / 2, Height - 4, w - pad, 3), 2, Theme.Accent);
                x += w;
            }
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        private int Hit(Point p)
        {
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h >= 0) SelectedIndex = h;
        }
    }

    // ------------------------------------------------------------------ pill button

    internal sealed class PillButton : Control
    {
        private bool hover, down, isChecked;

        public PillButton(string text)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Base;
            Cursor = Cursors.Hand;
            Text = text;
        }

        public bool Checked
        {
            get { return isChecked; }
            set { if (isChecked != value) { isChecked = value; Invalidate(); } }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Size ts = TextRenderer.MeasureText(Text, Font);
            Size = new Size(ts.Width + Font.Height + 4, Font.Height + 12);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Color fill = isChecked ? Theme.Accent : down ? Theme.Faint : hover ? Theme.Border : Theme.CardHover;
            if (isChecked && hover) fill = Theme.Mix(Theme.Accent, Color.White, 0.12);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Height / 2, fill);
            Theme.Draw(g, Text, Font, r, isChecked ? Theme.OnColor(Theme.Accent) : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    }

    // ------------------------------------------------------------------ compact header

    internal sealed class HeroPanel : Control
    {
        public string Title = "Detecting graphics adapters…";
        public string Subtitle = "";
        public string Badge = "";

        public HeroPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Base;
            Height = Font.Height + Theme.Title.Height + Theme.Small.Height + Font.Height / 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new LinearGradientBrush(ClientRectangle, Theme.Mix(Color.White, Theme.Accent, 0.12), Color.White,
                LinearGradientMode.Horizontal))
                g.FillRectangle(b, ClientRectangle);
            int pad = Font.Height;

            int badgeW = 0;
            if (Badge.Length > 0)
            {
                Size ts = TextRenderer.MeasureText(Badge, Theme.Semi);
                badgeW = ts.Width + pad;
                var br = new Rectangle(Width - pad - badgeW, (Height - Theme.Semi.Height - 8) / 2, badgeW, Theme.Semi.Height + 8);
                Theme.FillRound(g, br, br.Height / 2, Theme.Accent);
                Theme.Draw(g, Badge, Theme.Semi, br, Theme.OnColor(Theme.Accent), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            int textW = Width - pad * 3 - badgeW;
            int ty = (Height - Theme.Title.Height - Theme.Small.Height) / 2;
            Theme.Draw(g, Title, Theme.Title, new Rectangle(pad, ty, textW, Theme.Title.Height), Theme.Text, TextFormatFlags.Left);
            Theme.Draw(g, Subtitle, Theme.Small, new Rectangle(pad, ty + Theme.Title.Height, textW, Theme.Small.Height),
                Theme.Muted, TextFormatFlags.Left);
        }
    }

    // ------------------------------------------------------------------ Task-Manager-style monitor

    internal sealed class MonitorView : Control
    {
        public SensorSet Sensors;
        public SensorSet System;                     // CPU / system RAM
        public GpuDevice Gpu;
        public List<GpuDevice> AllGpus;              // non-null = "All GPUs" overview
        public Func<GpuDevice, SensorSet> SetFor;
        public int WindowSamples = 60;
        public string SpanText = "60 seconds";

        public MonitorView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Base;
            BackColor = Theme.Bg;
        }

        private Sensor Find(string name) { return Sensors == null ? null : Sensors.Find(name); }

        private static string Val(Sensor s)
        {
            return s == null || s.Count == 0 ? "–" : s.Display(0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            if (AllGpus != null && SetFor != null) { PaintAll(g); return; }
            if (Gpu == null) return;
            int U = Font.Height, pad = U * 3 / 4;
            Sensor load = Find("GPU Load"), mem = Find(Gpu.MemorySensorName), temp = Find("GPU Temperature");

            // Stats grid at the bottom (2 rows x 3 columns).
            int cellH = Theme.Small.Height + Theme.Mid.Height + 4;
            int statsH = cellH * 2 + pad;
            var stats = new Rectangle(pad, Height - pad - statsH, Width - pad * 2, statsH);

            // Graphs: GPU (largest), memory, temperature.
            var area = new Rectangle(pad, pad, Width - pad * 2, stats.Y - pad * 2);
            // Layout: GPU utilization (largest) / GPU memory / [temperature | CPU | system RAM] / stats.
            int h1 = area.Height * 37 / 100, h2 = area.Height * 31 / 100, h3 = area.Height - h1 - h2;
            string span = SpanText;

            // GPU utilization (+ throttle badge). NVML utilization includes CUDA (Task Manager's default view does not).
            string gpuNow = load != null && load.Count > 0 ? load.Value.ToString("0") + "%" : "";
            string gpuMax = Gpu.HasNvml ? Lang.L("all engines incl. CUDA") + "  ·  100%" : "100%";
            int bx = DrawPanel(g, new Rectangle(area.X, area.Y, area.Width, h1), Lang.L("GPU utilization"), gpuNow, Theme.Muted, gpuMax, span, load, 100, null, Theme.Accent);
            if (Sensors != null && Sensors.HasThrottle)
            {
                string warn = Sensors.ThrottleWarning;
                int bh = Theme.Small.Height + 4, by = area.Y + (Theme.Semi.Height + 4 - bh) / 2;
                if (warn != null)
                {
                    string t = "⚠ " + Lang.L(warn);
                    int bw = TextRenderer.MeasureText(t, Theme.Small).Width + U;
                    var br = new Rectangle(bx + U, by, bw, bh);
                    Theme.FillRound(g, br, bh / 2, warn.StartsWith("Power") ? Theme.Warn : Theme.Danger);
                    Theme.Draw(g, t, Theme.Small, br, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                    Theme.Draw(g, "● " + Lang.L("not throttling"), Theme.Small, new Rectangle(bx + U, by, U * 10, bh), Theme.Faint,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            // Memory: exact used / free / percent (+ shared); orange at 90%, red at 95% or when spilling to system RAM.
            double total = mem != null ? mem.FixedMax : 0;
            string memNow = "", memText = "–";
            Color memColor = Theme.Accent, nowColor = Theme.Muted;
            if (mem != null && mem.Count > 0)
            {
                if (total > 0)
                {
                    double pct = mem.Value / total * 100;
                    Sensor freeS = Find("Memory Free");
                    double free = freeS != null && freeS.Count > 0 ? freeS.Value : Math.Max(0, total - mem.Value);
                    memNow = Lang.F("{0:N0} MB used  ·  {1:N0} MB free  ·  {2:0.0}%", mem.Value, free, pct);
                    memText = string.Format("{0:0.00} / {1:0.00} GB", mem.Value / 1024, total / 1024);
                    if (pct >= 95) memColor = nowColor = Theme.Danger;
                    else if (pct >= 90) memColor = nowColor = Theme.Warn;
                }
                else
                {
                    memNow = Lang.F("{0:N0} MB used", mem.Value);
                    memText = string.Format("{0:0.00} GB", mem.Value / 1024);
                }
                Sensor sh = Gpu.HasNvml ? Find(Gpu.SharedSensorName) : null;
                if (sh != null && sh.Count > 0) memNow += "  ·  " + Lang.F("shared {0:N0} MB", sh.Value);
                if (Sensors.Spilling)
                {
                    memNow = "⚠ " + Lang.L("Spilling to system RAM") + "  ·  " + memNow;
                    memColor = nowColor = Theme.Danger;
                }
            }
            DrawPanel(g, new Rectangle(area.X, area.Y + h1, area.Width, h2), Lang.L(Gpu.MemoryTitle), memNow, nowColor,
                total > 0 ? string.Format("{0:N0} MB", total) : "", span, mem, 0, null, memColor);

            string tNow = temp != null && temp.Count > 0 ? temp.Value.ToString("0") + " °C" : "";
            // Bottom row: four equal panels. Headers show the current value only when it fits; the stats grid always has it.
            Sensor cpu = System == null ? null : System.Find("CPU Usage");
            Sensor ram = System == null ? null : System.Find("RAM Used");
            Sensor rx = System == null ? null : System.Find("Net Receive");
            Sensor tx = System == null ? null : System.Find("Net Send");
            int gap = pad, w4 = (area.Width - gap * 3) / 4, y3 = area.Y + h1 + h2;
            DrawPanel(g, new Rectangle(area.X, y3, w4, h3), Lang.L("Temperature"), tNow, Theme.Muted, "100 °C", span, temp, 100,
                temp == null ? Lang.L("Not available (NVIDIA only)") : null, Theme.Accent);
            string cpuNow = cpu != null && cpu.Count > 0 ? cpu.Value.ToString("0") + "%" : "";
            DrawPanel(g, new Rectangle(area.X + w4 + gap, y3, w4, h3), "CPU", cpuNow, Theme.Muted, "100%", span, cpu, 100, null, Theme.CpuColor);
            string ramNow = "", ramText = "–";
            if (ram != null && ram.Count > 0 && ram.FixedMax > 0)
            {
                ramNow = string.Format("{0:0.0} GB", ram.Value / 1024);
                ramText = string.Format("{0:0.0} / {1:0} GB", ram.Value / 1024, ram.FixedMax / 1024);
            }
            DrawPanel(g, new Rectangle(area.X + (w4 + gap) * 2, y3, w4, h3), Lang.L("System RAM"), ramNow, Theme.Muted,
                ram != null && ram.FixedMax > 0 ? string.Format("{0:0} GB", ram.FixedMax / 1024) : "", span, ram, 0, null, Theme.RamColor);
            // Network: receive = filled area, send = dashed line, shared 1-2-5 scale.
            string netText = rx != null && rx.Count > 0 && tx != null ? Units.RatePair(rx.Value, tx.Value) : "–";
            double netMax = Units.NiceScale(Math.Max(Theme.WindowMax(rx, WindowSamples), Theme.WindowMax(tx, WindowSamples)));
            DrawPanel(g, new Rectangle(area.X + (w4 + gap) * 3, y3, area.Width - (w4 + gap) * 3, h3), Lang.L("Network"),
                netText == "–" ? "" : netText, Theme.Muted, rx == null ? "" : Units.Rate(netMax, null), span, rx, netMax, null, Theme.NetColor, tx);

            // Stats: 4 columns x 2 rows (GPU first row, system second row).
            Sensor fan = Find("Fan Speed");
            string tempLabel = fan != null && fan.Count > 0 ? Lang.L("Temperature") + "  ·  " + Lang.L("Fan") + " " + fan.Value.ToString("0") + "%" : Lang.L("Temperature");
            string[] labels = { Lang.L("Utilization"), Lang.L("GPU memory"), tempLabel, Lang.L("Power"), "CPU", Lang.L("System RAM"), Lang.L("Network"), Lang.L("GPU clock") };
            string[] values =
            {
                Val(load), memText, Val(temp), Val(Find("Board Power Draw")),
                cpu != null && cpu.Count > 0 ? cpu.Value.ToString("0") + " %" : "–", ramText, netText, Val(Find("GPU Clock")),
            };
            int colW = stats.Width / 4;
            for (int i = 0; i < 8; i++)
            {
                int x = stats.X + (i % 4) * colW, y = stats.Y + pad + (i / 4) * cellH;
                Theme.Draw(g, labels[i], Theme.Small, new Rectangle(x, y, colW - 6, Theme.Small.Height), Theme.Muted, TextFormatFlags.Left);
                Theme.Draw(g, values[i], Theme.Mid, new Rectangle(x, y + Theme.Small.Height + 1, colW - 6, Theme.Mid.Height), Theme.Text, TextFormatFlags.Left);
            }
        }

        // One card per GPU: utilization and memory graphs side by side.
        private void PaintAll(Graphics g)
        {
            int U = Font.Height, pad = U * 3 / 4, ip = U * 2 / 3, n = Math.Max(1, AllGpus.Count);
            int cardH = Math.Min(U * 11, (Height - pad * (n + 1)) / n);
            if (cardH < U * 4) cardH = U * 4;
            for (int i = 0; i < AllGpus.Count; i++)
            {
                GpuDevice d = AllGpus[i];
                SensorSet s = SetFor(d);
                Sensor load = s.Find("GPU Load"), mem = s.Find(d.MemorySensorName), temp = s.Find("GPU Temperature");
                Color c = Theme.VendorColor(d.VendorId);
                var card = new Rectangle(pad, pad + i * (cardH + pad), Width - pad * 2, cardH);
                Theme.FillRound(g, card, 10, Theme.Card);

                int dot = U / 2, hy = card.Y + ip;
                Theme.FillRound(g, new Rectangle(card.X + ip, hy + (Theme.Semi.Height - dot) / 2, dot, dot), dot / 2, c);
                var parts = new List<string>();
                if (load != null && load.Count > 0) parts.Add("GPU " + load.Value.ToString("0") + "%");
                if (mem != null && mem.Count > 0)
                    parts.Add(mem.FixedMax > 0 ? string.Format("{0:0.0} / {1:0.0} GB", mem.Value / 1024, mem.FixedMax / 1024) : string.Format("{0:0.0} GB", mem.Value / 1024));
                if (temp != null && temp.Count > 0) parts.Add(temp.Value.ToString("0") + " °C");
                if (s.Spilling) parts.Add("⚠ " + Lang.L("Spilling to system RAM"));
                string summary = string.Join("   ·   ", parts.ToArray());
                int sw = TextRenderer.MeasureText(summary, Theme.Semi).Width;
                Theme.Draw(g, d.Name, Theme.Semi, new Rectangle(card.X + ip + dot + 8, hy, card.Width - sw - ip * 3 - dot, Theme.Semi.Height), Theme.Text, TextFormatFlags.Left);
                Theme.Draw(g, summary, Theme.Semi, new Rectangle(card.X, hy, card.Width - ip, Theme.Semi.Height), s.Spilling ? Theme.Danger : Theme.Muted, TextFormatFlags.Right);

                int top = hy + Theme.Semi.Height + ip / 2, gap = ip;
                int gw = (card.Width - ip * 2 - gap) / 2, gh = card.Bottom - ip - top;
                if (gh < 10) continue;
                var r1 = new Rectangle(card.X + ip, top, gw, gh);
                var r2 = new Rectangle(r1.Right + gap, top, card.Right - ip - r1.Right - gap, gh);
                Theme.TaskGraph(g, r1, load, 100, WindowSamples, c);
                Theme.TaskGraph(g, r2, mem, 0, WindowSamples, c);
                Theme.Draw(g, Lang.L("GPU utilization"), Theme.Small, new Rectangle(r1.X + 5, r1.Y + 3, r1.Width - 10, Theme.Small.Height), Theme.Muted, TextFormatFlags.Left);
                Theme.Draw(g, Lang.L(d.MemoryTitle), Theme.Small, new Rectangle(r2.X + 5, r2.Y + 3, r2.Width - 10, Theme.Small.Height), Theme.Muted, TextFormatFlags.Left);
            }
        }

        // Draws one titled graph; returns the x position just after the title text (for badges).
        private int DrawPanel(Graphics g, Rectangle r, string title, string now, Color nowColor, string maxText, string span,
            Sensor s, double max, string empty, Color color)
        {
            return DrawPanel(g, r, title, now, nowColor, maxText, span, s, max, empty, color, null);
        }

        // 'dashed' (network send) adds a dashed series and a small solid/dashed legend under the graph.
        private int DrawPanel(Graphics g, Rectangle r, string title, string now, Color nowColor, string maxText, string span,
            Sensor s, double max, string empty, Color color, Sensor dashed)
        {
            int th = Theme.Semi.Height + 4, bh = Theme.Small.Height + 6, U = Font.Height;
            if (r.Height < th + bh + 10) return r.X;
            int tw = TextRenderer.MeasureText(g, title, Theme.Semi, Size.Empty, TextFormatFlags.NoPadding).Width;
            int mw = TextRenderer.MeasureText(g, maxText, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
            Theme.Draw(g, title, Theme.Semi, new Rectangle(r.X, r.Y, Math.Min(tw + 4, r.Width - mw - 4), th), Theme.Text, TextFormatFlags.Left);
            int x = r.X + tw;
            if (!string.IsNullOrEmpty(now))
            {
                int nw = TextRenderer.MeasureText(g, now, Theme.Semi, Size.Empty, TextFormatFlags.NoPadding).Width;
                if (tw + U + nw + U + mw <= r.Width) // only when it fits whole (no "45 °…")
                {
                    Theme.Draw(g, now, Theme.Semi, new Rectangle(x + U, r.Y, nw + 4, th), nowColor, TextFormatFlags.Left);
                    x += U + nw;
                }
            }
            Theme.Draw(g, maxText, Theme.Small, new Rectangle(r.X, r.Y + 2, r.Width, th), Theme.Muted, TextFormatFlags.Right);
            var gr = new Rectangle(r.X, r.Y + th, r.Width, r.Height - th - bh);
            Theme.TaskGraph(g, gr, s, max, WindowSamples, color, dashed);
            if (empty != null)
                Theme.Draw(g, empty, Font, gr, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int fy = gr.Bottom + 2, fh = Theme.Small.Height;
            if (dashed != null)
            {
                // Legend: ━ ↓   ╌ ↑   (receive solid, send dashed)
                int lx = r.X, ly = fy + fh / 2, seg = U;
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(color, 2f)) g.DrawLine(p, lx, ly, lx + seg, ly);
                Theme.Draw(g, "↓", Theme.Semi, new Rectangle(lx + seg + 3, fy - 1, U, fh + 2), Theme.Text, TextFormatFlags.Left);
                lx += seg + U + 6;
                using (var p = new Pen(Theme.Mix(color, Theme.Text, 0.35), 2f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, lx, ly, lx + seg, ly);
                Theme.Draw(g, "↑", Theme.Semi, new Rectangle(lx + seg + 3, fy - 1, U, fh + 2), Theme.Text, TextFormatFlags.Left);
                g.SmoothingMode = old;
            }
            else
                Theme.Draw(g, span, Theme.Small, new Rectangle(r.X, fy, r.Width, fh), Theme.Muted, TextFormatFlags.Left);
            Theme.Draw(g, "0", Theme.Small, new Rectangle(r.X, fy, r.Width, fh), Theme.Muted, TextFormatFlags.Right);
            return x;
        }
    }

    // ------------------------------------------------------------------ spec cards

    internal sealed class InfoView : DarkScroll
    {
        public sealed class Field
        {
            public string Label, Value;
            public bool Wide;
        }

        public sealed class Section
        {
            public string Title;
            public bool Chips;
            public readonly List<Field> Fields = new List<Field>();
        }

        public List<Section> Sections = new List<Section>();
        public event Action<Field> FieldClicked;
        private readonly List<KeyValuePair<Rectangle, Field>> hits = new List<KeyValuePair<Rectangle, Field>>();
        private Field hover;

        public void SetSections(List<Section> sections)
        {
            Sections = sections;
            SetContentHeight(Render(null));
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetContentHeight(Render(null));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg);
            Render(e.Graphics);
        }

        private static bool Supported(string v)
        {
            return !string.IsNullOrEmpty(v) && !v.StartsWith("Not") && !v.StartsWith("N/A") && !v.StartsWith("Detecting");
        }

        // Lays out (and, when g != null, draws) all cards. Returns the total content height.
        private int Render(Graphics g)
        {
            int U = Font.Height, pad = U * 3 / 4, ip = U * 3 / 4;
            int y0 = g == null ? 0 : AutoScrollPosition.Y, y = y0 + pad;
            int cardW = ClientSize.Width - pad * 2;
            int cols = cardW > U * 40 ? 3 : 2;
            int colW = (cardW - ip * 2) / cols;
            int titleH = Theme.Small.Height + U / 2;
            int cellH = Theme.Small.Height + U + U / 2 + 2;
            int chipH = Theme.Semi.Height + Theme.Small.Height + U;
            if (g != null) hits.Clear();

            foreach (Section sec in Sections)
            {
                int rows = 0, col = 0;
                if (!sec.Chips)
                {
                    foreach (Field f in sec.Fields)
                    {
                        if (f.Wide && col != 0) { rows++; col = 0; }
                        col += f.Wide ? cols : 1;
                        if (col >= cols) { rows++; col = 0; }
                    }
                    if (col > 0) rows++;
                }
                int bodyH = sec.Chips ? chipH : rows * cellH;
                var card = new Rectangle(pad, y, cardW, ip + titleH + bodyH + ip - 2);

                if (g != null && card.Bottom >= 0 && card.Top <= ClientSize.Height)
                {
                    Theme.FillRound(g, card, 10, Theme.Card);
                    Theme.FillRound(g, new Rectangle(card.X + ip, card.Y + ip + 3, 3, Theme.Small.Height - 5), 1, Theme.Accent);
                    Theme.Draw(g, sec.Title.ToUpperInvariant(), Theme.Small, new Rectangle(card.X + ip + 9, card.Y + ip, cardW, Theme.Small.Height),
                        Theme.Muted, TextFormatFlags.Left);
                    int top = card.Y + ip + titleH;
                    if (sec.Chips) DrawChips(g, sec, new Rectangle(card.X + ip, top, cardW - ip * 2, chipH));
                    else DrawCells(g, sec, card.X + ip, top, colW, cellH, cols);
                }
                y = card.Bottom + pad;
            }
            return y - y0;
        }

        private void DrawCells(Graphics g, Section sec, int x0, int top, int colW, int cellH, int cols)
        {
            int col = 0, row = 0;
            foreach (Field f in sec.Fields)
            {
                if (f.Wide && col != 0) { row++; col = 0; }
                int span = f.Wide ? cols : 1;
                var cell = new Rectangle(x0 + col * colW, top + row * cellH, colW * span, cellH);
                hits.Add(new KeyValuePair<Rectangle, Field>(cell, f));
                if (f == hover) Theme.FillRound(g, new Rectangle(cell.X - 4, cell.Y, cell.Width - 4, cell.Height - 2), 6, Theme.CardHover);
                Theme.Draw(g, f.Label, Theme.Small, new Rectangle(cell.X + 4, cell.Y + 3, cell.Width - 14, Theme.Small.Height), Theme.Muted, TextFormatFlags.Left);
                Theme.Draw(g, f.Value, Font, new Rectangle(cell.X + 4, cell.Y + 3 + Theme.Small.Height + 1, cell.Width - 14, Font.Height),
                    Theme.Text, TextFormatFlags.Left);
                col += span;
                if (col >= cols) { col = 0; row++; }
            }
        }

        private void DrawChips(Graphics g, Section sec, Rectangle area)
        {
            int n = sec.Fields.Count, gap = Font.Height / 2;
            if (n == 0) return;
            int w = (area.Width - gap * (n - 1)) / n;
            int dot = Font.Height / 2;
            for (int i = 0; i < n; i++)
            {
                Field f = sec.Fields[i];
                var r = new Rectangle(area.X + i * (w + gap), area.Y, w, area.Height);
                hits.Add(new KeyValuePair<Rectangle, Field>(r, f));
                Theme.FillRound(g, r, 8, f == hover ? Theme.Border : Theme.CardHover);
                bool ok = Supported(f.Value);
                int ip = Font.Height * 2 / 3;
                int ny = r.Y + (r.Height - Theme.Semi.Height - Theme.Small.Height) / 2;
                Theme.FillRound(g, new Rectangle(r.X + ip, ny + (Theme.Semi.Height - dot) / 2, dot, dot), dot / 2, ok ? Theme.Accent : Theme.Faint);
                int tx = r.X + ip + dot + 6;
                Theme.Draw(g, f.Label, Theme.Semi, new Rectangle(tx, ny, r.Right - tx - 4, Theme.Semi.Height), ok ? Theme.Text : Theme.Muted, TextFormatFlags.Left);
                Theme.Draw(g, f.Value, Theme.Small, new Rectangle(tx, ny + Theme.Semi.Height, r.Right - tx - 4, Theme.Small.Height), Theme.Muted, TextFormatFlags.Left);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Field h = null;
            foreach (var kv in hits) if (kv.Key.Contains(e.Location)) { h = kv.Value; break; }
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != null) { hover = null; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (hover != null && FieldClicked != null) FieldClicked(hover);
        }
    }

    // ------------------------------------------------------------------ sensor list

    internal sealed class SensorView : DarkScroll
    {
        private static readonly string[] ModeNames = { "CURRENT", "MINIMUM", "MAXIMUM", "AVERAGE" };
        private SensorSet set;
        private int mode;

        private int RowH { get { return Font.Height + 14; } }
        private int Pad { get { return Font.Height * 3 / 4; } }
        private int ValueRight { get { return Pad + (int)((ClientSize.Width - Pad * 2) * 0.62); } }

        public void SetSensors(SensorSet s)
        {
            set = s;
            int rows = s == null ? 0 : s.List.Count;
            SetContentHeight(RowH + rows * (RowH + 3) + Pad);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Y < RowH && e.X > ValueRight - Font.Height * 7 && e.X < ValueRight + 4)
            {
                mode = (mode + 1) % ModeNames.Length;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = e.Y < RowH && e.X > ValueRight - Font.Height * 7 && e.X < ValueRight + 4 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            int pad = Pad, rh = RowH, w = ClientSize.Width, vr = ValueRight, ip = Font.Height * 2 / 3;
            int gx = vr + pad, y0 = rh + AutoScrollPosition.Y;

            if (set != null)
            {
                for (int i = 0; i < set.List.Count; i++)
                {
                    int y = y0 + i * (rh + 3);
                    if (y + rh < rh || y > ClientSize.Height) continue;
                    Sensor s = set.List[i];
                    var row = new Rectangle(pad, y, w - pad * 2, rh);
                    Theme.FillRound(g, row, 8, Theme.Card);
                    Theme.Draw(g, Lang.L(s.Name), Font, new Rectangle(row.X + ip, y, vr - row.X - ip - Font.Height * 6, rh), Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    Theme.Draw(g, s.Display(mode), Theme.Semi, new Rectangle(vr - Font.Height * 8, y, Font.Height * 8, rh),
                        s.Text != null ? Theme.Muted : Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    if (s.Text == null)
                    {
                        var gr = new Rectangle(gx, y + 5, row.Right - gx - ip / 2, rh - 10);
                        Theme.FillRound(g, gr, 5, Theme.Mix(Theme.Card, Theme.Bg, 0.55));
                        Theme.Spark(g, s, Rectangle.Inflate(gr, -2, -2), 2);
                    }
                }
            }

            // Pinned header.
            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 0, 0, w, rh);
            Theme.Draw(g, Lang.L("SENSOR"), Theme.Small, new Rectangle(pad + ip, 0, w, rh), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Theme.Draw(g, ModeNames[mode].ToUpperInvariant() + "  ▾", Theme.Small, new Rectangle(vr - Font.Height * 8, 0, Font.Height * 8, rh),
                Theme.Accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Theme.Draw(g, Lang.L("HISTORY"), Theme.Small, new Rectangle(gx, 0, w, rh), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (set == null || set.List.Count == 0)
                Theme.Draw(g, "Waiting for data…", Font, new Rectangle(0, rh, w, rh * 2), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
