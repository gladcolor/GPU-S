// Extras: per-process GPU usage, persisted settings, nvidia-smi-style CSV logging,
// command-line (tray) logging, mini window and settings dialog.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuS
{
    // ------------------------------------------------------------------ per-process GPU usage

    internal sealed class GpuProcess
    {
        public int Pid;
        public string Name;
        public double GpuPct, DedicatedMb, SharedMb;
    }

    // Uses the same Windows counters as Task Manager's "GPU" columns. Opened lazily and only
    // sampled while the Processes tab is visible.
    internal sealed class ProcessSampler : IDisposable
    {
        private IntPtr query, engine, dedicated, shared;
        private bool tried;
        private readonly Dictionary<int, string> names = new Dictionary<int, string>();

        private static int ParsePid(string instance)
        {
            if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return -1;
            int e = instance.IndexOf('_', 4);
            int pid;
            return e > 4 && int.TryParse(instance.Substring(4, e - 4), out pid) ? pid : -1;
        }

        private static GpuProcess Get(Dictionary<int, GpuProcess> map, int pid)
        {
            GpuProcess p;
            if (!map.TryGetValue(pid, out p)) { p = new GpuProcess(); p.Pid = pid; map[pid] = p; }
            return p;
        }

        public List<GpuProcess> Sample(string luid)
        {
            var result = new List<GpuProcess>();
            if (!tried)
            {
                tried = true;
                if (Pdh.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero;
                else
                {
                    Pdh.PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out engine);
                    Pdh.PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out dedicated);
                    Pdh.PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out shared);
                }
            }
            if (query == IntPtr.Zero || Pdh.PdhCollectQueryData(query) != 0) return result;

            var map = new Dictionary<int, GpuProcess>();
            foreach (var kv in Pdh.ReadArray(engine))
            {
                if (kv.Key.IndexOf(luid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int pid = ParsePid(kv.Key);
                if (pid <= 0) continue;
                GpuProcess p = Get(map, pid);
                p.GpuPct = Math.Max(p.GpuPct, Math.Min(100, kv.Value)); // busiest engine, like Task Manager
            }
            foreach (var kv in Pdh.ReadArray(dedicated))
            {
                int pid = ParsePid(kv.Key);
                if (pid > 0 && kv.Key.IndexOf(luid, StringComparison.OrdinalIgnoreCase) >= 0) Get(map, pid).DedicatedMb += kv.Value / 1048576.0;
            }
            foreach (var kv in Pdh.ReadArray(shared))
            {
                int pid = ParsePid(kv.Key);
                if (pid > 0 && kv.Key.IndexOf(luid, StringComparison.OrdinalIgnoreCase) >= 0) Get(map, pid).SharedMb += kv.Value / 1048576.0;
            }
            foreach (GpuProcess p in map.Values)
            {
                if (p.DedicatedMb < 0.1 && p.SharedMb < 0.1 && p.GpuPct < 0.1) continue;
                p.Name = NameOf(p.Pid);
                result.Add(p);
            }
            result.Sort(delegate(GpuProcess a, GpuProcess b)
            {
                int c = b.DedicatedMb.CompareTo(a.DedicatedMb);
                return c != 0 ? c : b.GpuPct.CompareTo(a.GpuPct);
            });
            return result;
        }

        // Process names are cached; the process list is only re-read when an unknown PID shows up.
        private string NameOf(int pid)
        {
            string n;
            if (names.TryGetValue(pid, out n)) return n;
            names.Clear();
            foreach (Process pr in Process.GetProcesses())
            {
                try { names[pr.Id] = pr.ProcessName; } catch (Exception) { }
                pr.Dispose();
            }
            if (!names.TryGetValue(pid, out n)) { n = "PID " + pid; names[pid] = n; }
            return n;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { Pdh.PdhCloseQuery(query); query = IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------------ settings (HKCU, no admin, no files)

    internal sealed class Settings
    {
        public int RefreshMs = 1000, RangeSec = 60, Tab, TempLimit = 85, MemLimit = 90;
        public bool MinimizeToTray, TempAlert = true, MemAlert, LogNoUnits, SpillAlert = true;
        public string Language = "auto";
        public Rectangle Bounds, MiniBounds;
        private const string KeyPath = @"Software\GPU-S";

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return s;
                    s.RefreshMs = I(k, "RefreshMs", s.RefreshMs);
                    s.RangeSec = I(k, "RangeSec", s.RangeSec);
                    s.Tab = I(k, "Tab", s.Tab);
                    s.TempLimit = I(k, "TempLimit", s.TempLimit);
                    s.MemLimit = I(k, "MemLimit", s.MemLimit);
                    s.MinimizeToTray = I(k, "MinimizeToTray", 0) != 0;
                    s.TempAlert = I(k, "TempAlert", 1) != 0;
                    s.MemAlert = I(k, "MemAlert", 0) != 0;
                    s.LogNoUnits = I(k, "LogNoUnits", 0) != 0;
                    s.SpillAlert = I(k, "SpillAlert", 1) != 0;
                    s.Language = k.GetValue("Language") as string ?? "auto";
                    s.Bounds = R(k, "Bounds");
                    s.MiniBounds = R(k, "MiniBounds");
                }
            }
            catch (Exception) { }
            return s;
        }

        public void Save()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("RefreshMs", RefreshMs);
                    k.SetValue("RangeSec", RangeSec);
                    k.SetValue("Tab", Tab);
                    k.SetValue("TempLimit", TempLimit);
                    k.SetValue("MemLimit", MemLimit);
                    k.SetValue("MinimizeToTray", MinimizeToTray ? 1 : 0);
                    k.SetValue("TempAlert", TempAlert ? 1 : 0);
                    k.SetValue("MemAlert", MemAlert ? 1 : 0);
                    k.SetValue("LogNoUnits", LogNoUnits ? 1 : 0);
                    k.SetValue("SpillAlert", SpillAlert ? 1 : 0);
                    k.SetValue("Language", Language ?? "auto");
                    k.SetValue("Bounds", string.Format("{0},{1},{2},{3}", Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height));
                    k.SetValue("MiniBounds", string.Format("{0},{1},{2},{3}", MiniBounds.X, MiniBounds.Y, MiniBounds.Width, MiniBounds.Height));
                }
            }
            catch (Exception) { }
        }

        private static int I(RegistryKey k, string name, int fallback)
        {
            object o = k.GetValue(name);
            return o is int ? (int)o : fallback;
        }

        private static Rectangle R(RegistryKey k, string name)
        {
            var s = k.GetValue(name) as string;
            if (s == null) return Rectangle.Empty;
            string[] p = s.Split(',');
            int x, y, w, h;
            if (p.Length == 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out w) && int.TryParse(p[3], out h))
                return new Rectangle(x, y, w, h);
            return Rectangle.Empty;
        }

        public static bool OnScreen(Rectangle r)
        {
            if (r.Width < 100 || r.Height < 60) return false;
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(r)) return true;
            return false;
        }
    }

    // ------------------------------------------------------------------ CSV logging (nvidia-smi format for NVIDIA)

    internal sealed class SensorLogger : IDisposable
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string NA = "[N/A]";
        private static readonly string[] SmiHeader =
        {
            "timestamp", "name", "pci.bus_id", "utilization.gpu [%]", "utilization.memory [%]", "memory.total [MiB]",
            "memory.used [MiB]", "memory.free [MiB]", "temperature.gpu", "power.draw [W]", "clocks.current.graphics [MHz]",
            "clocks.current.memory [MHz]", "fan.speed [%]", "pstate", "clocks_throttle_reasons.active",
        };

        private readonly TextWriter w;
        private readonly GpuDevice gpu;
        private readonly bool noUnits;
        private List<Sensor> columns;   // generic (non-NVIDIA) format only
        public long Rows;

        public SensorLogger(TextWriter writer, GpuDevice g, bool noUnits)
        {
            w = writer;
            gpu = g;
            this.noUnits = noUnits;
        }

        public void Write(SensorSet s)
        {
            if (gpu.HasNvml) WriteSmi(s);
            else WriteGeneric(s);
            w.Flush();
            Rows++;
        }

        // Same columns and value formatting as:
        //   nvidia-smi --query-gpu=timestamp,name,pci.bus_id,utilization.gpu,... --format=csv[,nounits] -l 1
        private void WriteSmi(SensorSet s)
        {
            if (Rows == 0) w.WriteLine(string.Join(", ", SmiHeader));
            Sensor mem = s.Find("Memory Used");
            double total = mem != null ? mem.FixedMax : 0;
            Sensor ps = s.Find("Performance State");
            var f = new List<string>
            {
                DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.fff", Inv),
                gpu.Name,
                gpu.PciBusId ?? NA,
                V(s.Find("GPU Load"), "0", "%"),
                V(s.Find("Memory Controller Load"), "0", "%"),
                total > 0 ? U(total.ToString("0", Inv), "MiB") : NA,
                V(mem, "0", "MiB"),
                V(s.Find("Memory Free"), "0", "MiB"),
                V(s.Find("GPU Temperature"), "0", null),
                V(s.Find("Board Power Draw"), "0.00", "W"),
                V(s.Find("GPU Clock"), "0", "MHz"),
                V(s.Find("Memory Clock"), "0", "MHz"),
                V(s.Find("Fan Speed"), "0", "%"),
                ps != null && ps.Count > 0 ? "P" + ps.Value.ToString("0", Inv) : NA,
                s.HasThrottle ? "0x" + s.ThrottleRaw.ToString("X16") : NA,
            };
            w.WriteLine(string.Join(", ", f));
        }

        private void WriteGeneric(SensorSet s)
        {
            if (columns == null)
            {
                columns = new List<Sensor>(s.List);
                var h = new List<string> { "timestamp", "name" };
                foreach (Sensor c in columns) h.Add(c.Unit.Length > 0 ? c.Name + " [" + c.Unit + "]" : c.Name);
                w.WriteLine(string.Join(", ", h));
            }
            var f = new List<string> { DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.fff", Inv), gpu.Name };
            foreach (Sensor c in columns) f.Add(c.Text ?? V(c, "0.##", c.Unit.Length > 0 ? c.Unit : null));
            w.WriteLine(string.Join(", ", f));
        }

        private string V(Sensor x, string fmt, string unit)
        {
            return x == null || x.Count == 0 ? NA : U(x.Value.ToString(fmt, Inv), unit);
        }

        private string U(string v, string unit)
        {
            return unit == null || noUnits ? v : v + " " + unit;
        }

        public void Dispose()
        {
            try { w.Dispose(); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ command-line logging (no window, tray icon)

    internal sealed class CliLogger : ApplicationContext
    {
        public const string Usage =
            "GPU-S command-line logging (nvidia-smi CSV format on NVIDIA GPUs)\n" +
            "  GPU-S.exe --log <file.csv | -> [--interval <sec>] [--duration <sec>] [--gpu <index>] [--nounits]\n" +
            "  Stop with Ctrl+C, the tray icon menu, or --duration.";

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);

        private readonly SensorHub hub = new SensorHub();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray;
        private readonly SensorLogger logger;
        private readonly GpuDevice gpu;
        private readonly DateTime end;
        private readonly string summaryPath;
        private volatile bool stop;
        private bool stopped;

        // "run.csv" -> "run.summary.md"
        public static string SummaryPathFor(string csvPath)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(csvPath)), Path.GetFileNameWithoutExtension(csvPath) + ".summary.md");
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static double Num(string s, double fallback)
        {
            double v;
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static int Run(string[] args)
        {
            string path = Arg(args, "--log");
            bool toStdout = path == "-";
            if (!toStdout) AttachConsole(0xFFFFFFFF); // print messages in the console we were started from (if any)
            if (string.IsNullOrEmpty(path)) { Console.Error.WriteLine(Usage); return 1; }

            List<GpuDevice> gpus = GpuDetector.Detect();
            int index = (int)Num(Arg(args, "--gpu"), -1);
            GpuDevice g = index >= 0 && index < gpus.Count ? gpus[index] : (gpus.Find(x => x.HasNvml) ?? (gpus.Count > 0 ? gpus[0] : null));
            if (g == null) { Console.Error.WriteLine("GPU-S: no GPU found."); return 2; }

            TextWriter w;
            try { w = toStdout ? Console.Out : new StreamWriter(path, false, new UTF8Encoding(false)); }
            catch (Exception ex) { Console.Error.WriteLine("GPU-S: cannot open " + path + ": " + ex.Message); return 3; }

            bool noUnits = Array.Exists(args, a => a.Equals("--nounits", StringComparison.OrdinalIgnoreCase));
            double interval = Math.Max(0.1, Num(Arg(args, "--interval"), 1));
            double duration = Num(Arg(args, "--duration"), 0);
            if (!toStdout)
                Console.WriteLine("GPU-S: logging \"{0}\" to {1} every {2} s. Stop with Ctrl+C or the tray icon.", g.Name, path, interval);
            Application.Run(new CliLogger(g, w, interval, duration, noUnits, toStdout ? "stdout" : Path.GetFileName(path),
                toStdout ? null : SummaryPathFor(path)));
            return 0;
        }

        private CliLogger(GpuDevice g, TextWriter w, double interval, double duration, bool noUnits, string name, string summary)
        {
            gpu = g;
            summaryPath = summary;
            logger = new SensorLogger(w, g, noUnits);
            end = duration > 0 ? DateTime.Now.AddSeconds(duration) : DateTime.MaxValue;

            var menu = new ContextMenuStrip();
            menu.Items.Add(Lang.L("Stop logging"), null, delegate { Stop(); });
            string tip = "GPU-S logging to " + name;
            tray = new NotifyIcon { Icon = GpuS.MainForm.AppIcon, Text = tip.Length > 63 ? tip.Substring(0, 63) : tip, ContextMenuStrip = menu, Visible = true };
            try { Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; stop = true; }; }
            catch (Exception) { }

            timer.Interval = (int)(interval * 1000);
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
        }

        private void Tick()
        {
            if (stop || DateTime.Now >= end) { Stop(); return; }
            try
            {
                hub.Sample(gpu);
                logger.Write(hub.For(gpu));
            }
            catch (Exception) { Stop(); }
        }

        private void Stop()
        {
            if (stopped) return;
            stopped = true;
            timer.Stop();
            logger.Dispose();
            // Run summary (Markdown) next to the log, and on the console.
            try
            {
                string md = hub.For(gpu).Run.Markdown(gpu);
                if (summaryPath != null) File.WriteAllText(summaryPath, md, new UTF8Encoding(false));
                if (summaryPath != null) Console.WriteLine("\nGPU-S: summary saved to " + summaryPath + "\n\n" + md);
            }
            catch (Exception) { }
            hub.Dispose();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ mini window

    internal sealed class MiniForm : Form
    {
        public readonly MiniView View = new MiniView();

        public MiniForm()
        {
            Text = "GPU-S";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Font = Theme.Base;
            BackColor = Theme.Bg;
            View.Dock = DockStyle.Fill;
            Controls.Add(View);
            int U = Font.Height;
            ClientSize = new Size(U * 17, View.RowH * MiniView.Rows + U / 2);
            MinimumSize = new Size(U * 12, View.RowH * MiniView.Rows);
        }
    }

    // ------------------------------------------------------------------ settings dialog

    internal sealed class SettingsForm : Form
    {
        private readonly CheckBox tray, tempAlert, memAlert, spillAlert, noUnits;
        private readonly NumericUpDown temp, mem;
        private readonly ComboBox language;

        public SettingsForm(Settings s)
        {
            Text = "GPU-S · " + Lang.L("Settings");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.Base;
            BackColor = Color.White;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            int U = Font.Height;

            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = new Padding(U), Dock = DockStyle.Fill };
            var langLabel = new Label { Text = Lang.L("Language"), AutoSize = true, Anchor = AnchorStyles.Left };
            language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = U * 9 };
            language.Items.Add(Lang.L("Auto (system)"));
            foreach (string n in Lang.Names) language.Items.Add(n);
            int li = Array.IndexOf(Lang.Codes, s.Language);
            language.SelectedIndex = li >= 0 ? li + 1 : 0;

            tray = Check("Minimize to the notification area (tray)", s.MinimizeToTray);
            tempAlert = Check("Alert when GPU temperature reaches", s.TempAlert);
            temp = new NumericUpDown { Minimum = 40, Maximum = 110, Value = Math.Max(40, Math.Min(110, s.TempLimit)), Width = U * 4 };
            memAlert = Check("Alert when GPU memory use reaches", s.MemAlert);
            mem = new NumericUpDown { Minimum = 10, Maximum = 100, Value = Math.Max(10, Math.Min(100, s.MemLimit)), Width = U * 4 };
            spillAlert = Check("Alert when GPU memory spills into system RAM", s.SpillAlert);
            noUnits = Check("Log values without units (like nvidia-smi --format=csv,nounits)", s.LogNoUnits);

            int r = 0;
            t.Controls.Add(langLabel, 0, r); t.Controls.Add(language, 1, r); t.SetColumnSpan(language, 2); r++;
            t.Controls.Add(tray, 0, r); t.SetColumnSpan(tray, 3); r++;
            t.Controls.Add(tempAlert, 0, r); t.Controls.Add(temp, 1, r); t.Controls.Add(Unit("°C"), 2, r); r++;
            t.Controls.Add(memAlert, 0, r); t.Controls.Add(mem, 1, r); t.Controls.Add(Unit("%"), 2, r); r++;
            t.Controls.Add(spillAlert, 0, r); t.SetColumnSpan(spillAlert, 3); r++;
            t.Controls.Add(noUnits, 0, r); t.SetColumnSpan(noUnits, 3); r++;

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, U, 0, 0) };
            var cancel = new Button { Text = Lang.L("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = Lang.L("OK"), DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            t.Controls.Add(buttons, 0, r); t.SetColumnSpan(buttons, 3);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(t);
        }

        private static CheckBox Check(string text, bool value)
        {
            return new CheckBox { Text = Lang.L(text), AutoSize = true, Checked = value, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4) };
        }

        private static Label Unit(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
        }

        // Returns true when the language changed (needs a restart).
        public bool Apply(Settings s)
        {
            s.MinimizeToTray = tray.Checked;
            s.TempAlert = tempAlert.Checked;
            s.TempLimit = (int)temp.Value;
            s.MemAlert = memAlert.Checked;
            s.MemLimit = (int)mem.Value;
            s.SpillAlert = spillAlert.Checked;
            s.LogNoUnits = noUnits.Checked;
            string lang = language.SelectedIndex <= 0 ? "auto" : Lang.Codes[language.SelectedIndex - 1];
            bool changed = lang != s.Language;
            s.Language = lang;
            return changed;
        }
    }
}
