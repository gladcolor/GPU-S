// Process list and mini-window views (custom drawn, light theme).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GpuS
{
    internal sealed class ProcessView : DarkScroll
    {
        private List<GpuProcess> items = new List<GpuProcess>();
        private double totalMb;
        private bool sampled;

        private int RowH { get { return Font.Height + 14; } }
        private int Pad { get { return Font.Height * 3 / 4; } }

        public bool IsNvidia;
        private const string Note = "GPU % covers graphics and video work only: Windows does not report CUDA compute per process (WDDM driver mode). Memory is exact; the total GPU load is on Monitor.";

        public void SetItems(List<GpuProcess> list, double totalMemoryMb)
        {
            items = list;
            totalMb = totalMemoryMb;
            sampled = true;
            SetContentHeight(RowH + list.Count * (RowH + 3) + Pad + Font.Height * 4);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            int pad = Pad, rh = RowH, ip = Font.Height * 2 / 3;
            int w = ClientSize.Width - pad * 2;
            // Right edges of the numeric columns.
            int pidR = pad + w * 50 / 100, gpuR = pad + w * 64 / 100, dedR = pad + w * 83 / 100, shR = pad + w - ip;
            int y0 = rh + AutoScrollPosition.Y;
            var right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter;

            for (int i = 0; i < items.Count; i++)
            {
                int y = y0 + i * (rh + 3);
                if (y + rh < rh || y > ClientSize.Height) continue;
                GpuProcess p = items[i];
                var row = new Rectangle(pad, y, w, rh);
                Theme.FillRound(g, row, 8, Theme.Card);
                if (totalMb > 0 && p.DedicatedMb > 0) // memory share bar along the bottom of the row
                {
                    int bw = (int)Math.Min(w - 16, (w - 16) * p.DedicatedMb / totalMb);
                    Theme.FillRound(g, new Rectangle(pad + 8, y + rh - 4, Math.Max(2, bw), 2), 1, Theme.Accent);
                }
                Theme.Draw(g, p.Name, Font, new Rectangle(pad + ip, y, pidR - pad - ip - Font.Height * 4, rh), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Theme.Draw(g, p.Pid.ToString(), Font, new Rectangle(pidR - Font.Height * 4, y, Font.Height * 4, rh), Theme.Muted, right);
                // CUDA compute is invisible to the per-process counters on Windows; show "–" rather than a misleading 0 %.
                string gpu = IsNvidia && p.GpuPct < 0.5 ? "–" : p.GpuPct.ToString("0") + " %";
                Theme.Draw(g, gpu, Theme.Semi, new Rectangle(pidR, y, gpuR - pidR, rh), Theme.Text, right);
                Theme.Draw(g, p.DedicatedMb.ToString("N0") + " MB", Theme.Semi, new Rectangle(gpuR, y, dedR - gpuR, rh), Theme.Text, right);
                Theme.Draw(g, p.SharedMb.ToString("N0") + " MB", Font, new Rectangle(dedR, y, shR - dedR, rh),
                    p.SharedMb >= 512 && IsNvidia ? Theme.Warn : Theme.Muted, right);
            }

            int ny = y0 + items.Count * (rh + 3) + pad / 2;
            if (IsNvidia && sampled)
                TextRenderer.DrawText(g, Lang.L(Note), Theme.Small, new Rectangle(pad + ip, ny, w - ip * 2, Font.Height * 4), Theme.Muted,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 0, 0, ClientSize.Width, rh);
            Theme.Draw(g, Lang.L("PROCESS"), Theme.Small, new Rectangle(pad + ip, 0, w, rh), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Theme.Draw(g, "PID", Theme.Small, new Rectangle(pidR - Font.Height * 4, 0, Font.Height * 4, rh), Theme.Muted, right);
            Theme.Draw(g, "GPU", Theme.Small, new Rectangle(pidR, 0, gpuR - pidR, rh), Theme.Muted, right);
            Theme.Draw(g, Lang.L("DEDICATED") + " ▾", Theme.Small, new Rectangle(gpuR, 0, dedR - gpuR, rh), Theme.Accent, right);
            Theme.Draw(g, Lang.L("SHARED"), Theme.Small, new Rectangle(dedR, 0, shR - dedR, rh), Theme.Muted, right);

            if (items.Count == 0)
                Theme.Draw(g, Lang.L(sampled ? "No process is using this GPU" : "Collecting…"), Font, new Rectangle(0, rh, ClientSize.Width, rh * 2),
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    internal sealed class MiniView : Control
    {
        public SensorSet Sensors;
        public string MemorySensor = "Memory Used";

        public MiniView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Base;
            BackColor = Theme.Bg;
        }

        public int RowH { get { return Font.Height + 12; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            int U = Font.Height, pad = U / 2, rh = RowH;
            Sensor load = Sensors == null ? null : Sensors.Find("GPU Load");
            Sensor mem = Sensors == null ? null : Sensors.Find(MemorySensor);
            Sensor temp = Sensors == null ? null : Sensors.Find("GPU Temperature");

            string memText = "–";
            Color memColor = Theme.Text;
            if (mem != null && mem.Count > 0)
            {
                memText = mem.FixedMax > 0 ? string.Format("{0:0.0}/{1:0.0} GB", mem.Value / 1024, mem.FixedMax / 1024) : string.Format("{0:0.0} GB", mem.Value / 1024);
                double pct = mem.FixedMax > 0 ? mem.Value / mem.FixedMax * 100 : 0;
                if (pct >= 95) memColor = Theme.Danger; else if (pct >= 90) memColor = Theme.Warn;
            }
            string warn = Sensors == null ? null : Sensors.ThrottleWarning;
            Row(g, 0, "GPU", load == null || load.Count == 0 ? "–" : load.Value.ToString("0") + " %", Theme.Text, load);
            Row(g, 1, "MEM", memText, memColor, mem);
            Row(g, 2, "TEMP", temp == null || temp.Count == 0 ? "–" : temp.Value.ToString("0") + " °C", warn != null ? Theme.Danger : Theme.Text, temp);
        }

        private void Row(Graphics g, int i, string label, string value, Color valueColor, Sensor s)
        {
            int U = Font.Height, pad = U / 2, rh = RowH, y = pad / 2 + i * rh;
            int valR = pad + U * 9;
            Theme.Draw(g, label, Theme.Small, new Rectangle(pad, y, U * 3, rh), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Theme.Draw(g, value, Theme.Semi, new Rectangle(pad + U * 2, y, valR - pad - U * 2, rh), valueColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var gr = new Rectangle(valR + pad, y + 4, Width - valR - pad * 2, rh - 8);
            if (gr.Width < 10) return;
            Theme.FillRound(g, gr, 4, Theme.Card);
            Theme.Spark(g, s, Rectangle.Inflate(gr, -2, -2), 2);
        }
    }
}
