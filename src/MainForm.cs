using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GpuS
{
    internal sealed class MainForm : Form
    {
        public const string AppName = "GPU-S";
        public const string AppVersion = "1.5";
        public const string Author = "Ning, Huan (宁欢)";
        public const string AuthorEmail = "gladcolor@gmail.com";
        public static readonly Icon AppIcon = MakeIcon();

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "name", "Name" }, { "arch", "Architecture" }, { "shaders", "Shaders" }, { "vendor", "Vendor" },
            { "subvendor", "Subvendor" }, { "devid", "Device ID" }, { "rev", "Revision" }, { "bios", "BIOS Version" },
            { "memsize", "Memory Size" }, { "buswidth", "Memory Bus Width" }, { "sharedmem", "Shared System Memory" },
            { "bus", "Bus Interface" }, { "location", "Location" }, { "rebar", "Resizable BAR" }, { "gpuclock", "GPU Clock (max)" },
            { "memclock", "Memory Clock (max)" }, { "powerlimit", "Power Limit" }, { "driver", "Driver Version" },
            { "driverdate", "Driver Date" }, { "provider", "Driver Provider" }, { "nvdriver", "NVIDIA Driver" },
            { "directx", "DirectX" }, { "vulkan", "Vulkan" }, { "opencl", "OpenCL" }, { "cuda", "CUDA" },
        };

        // Section title, then field keys. "*" = full-width field, title ending in "!" = API chips.
        private static readonly string[][] CardLayout =
        {
            new[] { "Graphics Processor", "name*", "arch", "shaders", "vendor", "subvendor", "devid", "rev", "bios" },
            new[] { "Memory & Bus", "memsize", "buswidth", "sharedmem", "rebar", "bus*", "location*" },
            new[] { "Clocks & Power", "gpuclock", "memclock", "powerlimit" },
            new[] { "Driver", "driver", "driverdate", "provider", "nvdriver" },
            new[] { "Graphics & Compute APIs!", "directx", "vulkan", "opencl", "cuda" },
        };

        private static readonly int[] RefreshMs = { 500, 1000, 2000, 5000 };
        private static readonly string[] RefreshText = { "0.5 s", "1 s", "2 s", "5 s" };
        private static readonly int[] RangeSec = { 60, 600, 3600 };
        private static readonly string[] RangeText = { "1 min", "10 min", "1 hour" };

        private readonly Settings settings = Settings.Load();
        private List<GpuDevice> gpus = new List<GpuDevice>();
        private readonly SensorHub hub = new SensorHub();
        private readonly ProcessSampler procSampler = new ProcessSampler();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly bool elevated;

        private HeroPanel hero;
        private TabStrip tabStrip;
        private Panel monitorPage, sensorsPage;
        private MonitorView monitor;
        private ProcessView processView;
        private InfoView infoView, aboutView;
        private SensorView sensorView;
        private ComboBox gpuCombo;
        private readonly List<PillButton> ratePills = new List<PillButton>();
        private readonly List<PillButton> rangePills = new List<PillButton>();
        private PillButton logPill, miniPill, allPill;
        private Label statusLabel;
        private NotifyIcon tray;
        private MiniForm mini;
        private SensorLogger logger;
        private string logPath;
        private bool tempAlerted, memAlerted, spillAlerted;
        public int StartTab = -1;
        public bool StartAllGpus, StartMini;

        public MainForm()
        {
            elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Text = AppName + " · " + Lang.L("GPU Monitor");
            Font = Theme.Base;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Icon = AppIcon;
            float scale;
            using (Graphics gr = CreateGraphics()) scale = gr.DpiX / 96f;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size((int)(480 * scale), (int)(520 * scale));
            if (Settings.OnScreen(settings.Bounds))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = settings.Bounds;
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(Math.Min((int)(660 * scale), wa.Width * 9 / 10), Math.Min((int)(780 * scale), wa.Height * 9 / 10));
            }

            timer.Interval = Array.IndexOf(RefreshMs, settings.RefreshMs) >= 0 ? settings.RefreshMs : 1000;
            BuildUi();
            UpdateRange();
            timer.Tick += OnTick;
        }

        // ------------------------------------------------------------ UI construction

        private FlowLayoutPanel MakeBar()
        {
            int U = Font.Height;
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Bg, WrapContents = true,
                Padding = new Padding(U * 3 / 4 - 3, U / 2, U / 2, 0),
            };
        }

        private static Label BarLabel(string text)
        {
            return new Label { Text = Lang.L(text), AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 7, 6, 0) };
        }

        private void BuildUi()
        {
            int U = Font.Height;
            hero = new HeroPanel { Dock = DockStyle.Top, Title = Lang.L("Detecting graphics adapters…") };
            tabStrip = new TabStrip(Lang.L("Monitor"), Lang.L("Processes"), Lang.L("Details"), Lang.L("Sensors"), Lang.L("About")) { Dock = DockStyle.Top };
            tabStrip.SelectedChanged += delegate { ShowPage(tabStrip.SelectedIndex); };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // Monitor: time range, All GPUs, Task-Manager-style graphs
            monitorPage = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            FlowLayoutPanel rangeBar = MakeBar();
            rangeBar.Controls.Add(BarLabel("Show"));
            for (int i = 0; i < RangeSec.Length; i++)
            {
                int sec = RangeSec[i];
                var p = new PillButton(Lang.L(RangeText[i])) { Checked = sec == settings.RangeSec, Margin = new Padding(2) };
                p.Click += delegate
                {
                    foreach (var q in rangePills) q.Checked = q == p;
                    settings.RangeSec = sec;
                    UpdateRange();
                };
                rangePills.Add(p);
                rangeBar.Controls.Add(p);
            }
            if (!rangePills.Exists(q => q.Checked)) { rangePills[0].Checked = true; settings.RangeSec = 60; }
            allPill = new PillButton(Lang.L("All GPUs")) { Margin = new Padding(U, 2, 2, 2), Visible = false };
            allPill.Click += delegate
            {
                allPill.Checked = !allPill.Checked;
                monitor.AllGpus = allPill.Checked ? gpus : null;
                OnTick(this, EventArgs.Empty);
            };
            rangeBar.Controls.Add(allPill);
            monitor = new MonitorView { Dock = DockStyle.Fill, SetFor = hub.For, System = hub.System };
            monitorPage.Controls.Add(monitor);
            monitorPage.Controls.Add(rangeBar);

            // Processes
            processView = new ProcessView { Dock = DockStyle.Fill, Visible = false };

            // Details
            infoView = new InfoView { Dock = DockStyle.Fill, Visible = false };
            infoView.FieldClicked += CopyField;

            // Sensors
            sensorsPage = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            FlowLayoutPanel bar = MakeBar();
            bar.Controls.Add(BarLabel("Refresh"));
            for (int i = 0; i < RefreshMs.Length; i++)
            {
                int interval = RefreshMs[i];
                var p = new PillButton(RefreshText[i]) { Checked = interval == timer.Interval, Margin = new Padding(2) };
                p.Click += delegate
                {
                    foreach (var q in ratePills) q.Checked = q == p;
                    timer.Interval = interval;
                    settings.RefreshMs = interval;
                    UpdateRange();
                };
                ratePills.Add(p);
                bar.Controls.Add(p);
            }
            var reset = new PillButton(Lang.L("Reset Min/Max")) { Margin = new Padding(U, 2, 2, 2) };
            reset.Click += delegate
            {
                GpuDevice g = Selected;
                if (g != null) { hub.For(g).ResetStats(); sensorView.Invalidate(); }
            };
            bar.Controls.Add(reset);
            logPill = new PillButton("●  " + Lang.L("Log to CSV")) { Margin = new Padding(2) };
            logPill.Click += delegate { ToggleLog(); };
            bar.Controls.Add(logPill);
            sensorView = new SensorView { Dock = DockStyle.Fill };
            sensorsPage.Controls.Add(sensorView);
            sensorsPage.Controls.Add(bar);

            // About
            aboutView = new InfoView { Dock = DockStyle.Fill, Visible = false };
            aboutView.FieldClicked += CopyField;
            aboutView.SetSections(new List<InfoView.Section>
            {
                AboutSection(AppName + " " + AppVersion,
                    Lang.L("About"), Lang.L("A small GPU monitor for Windows — one file, no installation, no administrator rights."),
                    Lang.L("License"), Lang.L("Free · no ads · no tracking · nothing is sent over the network")),
                AboutSection(Lang.L("Author"), Lang.L("Name"), Author, Lang.L("Email"), AuthorEmail),
                AboutSection(Lang.L("Data sources"),
                    "NVIDIA", "NVML (nvml.dll, installed with the NVIDIA driver)",
                    Lang.L("All GPUs"), "DXGI, Windows GPU performance counters, PnP device properties",
                    Lang.L("Runtime"), ".NET Framework 4.8 (built into Windows 10/11)"),
            });

            content.Controls.Add(monitorPage);
            content.Controls.Add(processView);
            content.Controls.Add(infoView);
            content.Controls.Add(sensorsPage);
            content.Controls.Add(aboutView);

            // Bottom bar
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, BackColor = Theme.Surface,
                Padding = new Padding(U / 2, U / 2, U / 2, U / 4),
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            gpuCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 3, 8, 3) };
            gpuCombo.SelectedIndexChanged += delegate { OnGpuChanged(); };
            miniPill = new PillButton(Lang.L("Mini")) { Margin = new Padding(3), Anchor = AnchorStyles.None };
            miniPill.Click += delegate { ToggleMini(); };
            var settingsPill = new PillButton(Lang.L("Settings")) { Margin = new Padding(3), Anchor = AnchorStyles.None };
            settingsPill.Click += delegate { ShowSettings(); };
            var reportPill = new PillButton(Lang.L("Report") + " ▾") { Margin = new Padding(3), Anchor = AnchorStyles.None };
            var reportMenu = new ContextMenuStrip();
            reportMenu.Items.Add(Lang.L("Copy run summary (Markdown)"), null, delegate
            {
                GpuDevice g = Selected;
                if (g != null) { Clipboard.SetText(hub.For(g).Run.Markdown(g)); SetStatus(Lang.L("Copied to the clipboard")); }
            });
            reportMenu.Items.Add(Lang.L("Copy report to clipboard"), null, delegate
            {
                GpuDevice g = Selected;
                if (g != null) { Clipboard.SetText(BuildReport(g)); SetStatus(Lang.L("Copied to the clipboard")); }
            });
            reportMenu.Items.Add(Lang.L("Save report…"), null, delegate { SaveReport(); });
            reportPill.Click += delegate { reportMenu.Show(reportPill, new Point(0, reportPill.Height)); };
            bottom.Controls.Add(gpuCombo, 0, 0);
            bottom.Controls.Add(miniPill, 1, 0);
            bottom.Controls.Add(settingsPill, 2, 0);
            bottom.Controls.Add(reportPill, 3, 0);

            statusLabel = new Label
            {
                Dock = DockStyle.Bottom, AutoSize = false, Height = Theme.Small.Height + U / 2 + 4, Font = Theme.Small,
                BackColor = Theme.Surface, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(U * 3 / 4, 0, 0, U / 4),
            };

            // Docking order: last added is docked first (outermost).
            Controls.Add(content);
            Controls.Add(tabStrip);
            Controls.Add(hero);
            Controls.Add(bottom);
            Controls.Add(statusLabel);

            // Tray icon (tooltip shows live values; used for alerts).
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add(Lang.L("Show GPU-S"), null, delegate { RestoreMain(); });
            trayMenu.Items.Add(Lang.L("Mini window"), null, delegate { ToggleMini(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Lang.L("Exit"), null, delegate { Close(); });
            tray = new NotifyIcon { Icon = AppIcon, Text = AppName, ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += delegate { RestoreMain(); };
        }

        private void CopyField(InfoView.Field f)
        {
            Clipboard.SetText(f.Value);
            SetStatus(Lang.L("Copied to the clipboard") + ": " + f.Label);
        }

        // title, then label/value pairs (all full width)
        private static InfoView.Section AboutSection(string title, params string[] pairs)
        {
            var sec = new InfoView.Section { Title = title };
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                sec.Fields.Add(new InfoView.Field { Label = pairs[i], Value = pairs[i + 1], Wide = true });
            return sec;
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    Theme.FillRound(g, new Rectangle(1, 1, 30, 30), 7, Color.FromArgb(0x5E, 0x9E, 0x00));
                    Theme.FillRound(g, new Rectangle(7, 7, 18, 18), 3, Color.White);
                    Theme.FillRound(g, new Rectangle(12, 12, 8, 8), 2, Color.FromArgb(0x5E, 0x9E, 0x00));
                }
                return (Icon)Icon.FromHandle(bmp.GetHicon()).Clone();
            }
        }

        private void ShowPage(int i)
        {
            settings.Tab = i;
            monitorPage.Visible = i == 0;
            processView.Visible = i == 1;
            infoView.Visible = i == 2;
            sensorsPage.Visible = i == 3;
            aboutView.Visible = i == 4;
            GpuDevice g = Selected;
            if (g == null) return;
            if (i == 1) RefreshProcesses(g);
            if (i == 3) sensorView.SetSensors(hub.For(g));
        }

        private void UpdateRange()
        {
            int samples = (int)Math.Min(Sensor.HistoryLength, settings.RangeSec * 1000L / timer.Interval);
            double secs = samples * timer.Interval / 1000.0;
            monitor.WindowSamples = samples;
            monitor.SpanText = secs < 120 ? Lang.F("{0:0} seconds", secs)
                : secs < 3600 ? Lang.F("{0:0} minutes", secs / 60) : Lang.L("1 hour");
            monitor.Invalidate();
        }

        private void SetStatus(string text) { statusLabel.Text = text; }

        // ------------------------------------------------------------ Detection

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            int tab = StartTab >= 0 ? StartTab : settings.Tab;
            if (tab > 0 && tab < 5) tabStrip.SelectedIndex = tab;
            SetStatus(Lang.L("Detecting graphics adapters…"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<GpuDevice> list;
                try { list = GpuDetector.Detect(); }
                catch (Exception) { list = new List<GpuDevice>(); }
                BeginInvoke((MethodInvoker)delegate { OnDetected(list); });

                // Slow part (DirectX / Vulkan / OpenCL) runs in a throw-away child process.
                try { GpuDetector.ApplyProbe(list); } catch (Exception) { }
                BeginInvoke((MethodInvoker)delegate { FillOverview(); UpdateStatus(); TrimSoon(); });
            });
        }

        private void OnDetected(List<GpuDevice> list)
        {
            gpus = list;
            gpuCombo.Items.Clear();
            foreach (var g in gpus) gpuCombo.Items.Add(g);
            allPill.Visible = gpus.Count > 1;
            if (StartAllGpus && gpus.Count > 1) { allPill.Checked = true; monitor.AllGpus = gpus; }
            int pick = gpus.FindIndex(g => g.HasNvml);
            if (pick < 0) pick = gpus.Count > 0 ? 0 : -1;
            if (pick >= 0) gpuCombo.SelectedIndex = pick;
            else { hero.Title = Lang.L("No graphics adapter found"); hero.Invalidate(); }
            UpdateStatus();
            timer.Start();
            if (StartMini) ToggleMini();
        }

        private void UpdateStatus()
        {
            if (logger != null) return; // keep "Logging to ..." visible
            string who = elevated ? Lang.L("Running as administrator") : "✓ " + Lang.L("Standard user — no admin rights needed");
            string src = Lang.L(gpus.Exists(g => g.HasNvml) ? "NVML sensors" : "Windows performance counters");
            SetStatus(who + "   ·   " + Lang.F("{0} GPU(s)", gpus.Count) + "   ·   " + src);
        }

        private GpuDevice Selected { get { return gpuCombo.SelectedItem as GpuDevice; } }

        private void OnGpuChanged()
        {
            GpuDevice g = Selected;
            if (g == null) return;
            if (logger != null) ToggleLog(); // log columns are per GPU
            Theme.Accent = Theme.VendorColor(g.VendorId);
            monitor.Gpu = g;
            monitor.Sensors = hub.For(g);
            processView.IsNvidia = g.IsNvidia;
            if (mini != null) { mini.View.Sensors = hub.For(g); mini.View.MemorySensor = g.MemorySensorName; }
            tempAlerted = memAlerted = spillAlerted = false;
            FillOverview();
            sensorView.SetSensors(hub.For(g));
            OnTick(this, EventArgs.Empty);
            Invalidate(true);
        }

        // ------------------------------------------------------------ Values / report

        private static string Na(string s) { return string.IsNullOrEmpty(s) ? Lang.L("N/A") : s; }
        private static string Mb(ulong bytes) { return bytes == 0 ? Lang.L("N/A") : string.Format("{0:N0} MB", bytes / 1048576); }
        private static string Mhz(uint v) { return v == 0 ? Lang.L("N/A") : v + " MHz"; }
        private static string PcieGen(uint g) { return g == 0 ? "?" : g == 1 ? "1.1" : g + ".0"; }

        private static Dictionary<string, string> Values(GpuDevice g)
        {
            var v = new Dictionary<string, string>();
            v["name"] = g.Name;
            v["arch"] = Na(g.Architecture);
            v["rev"] = string.Format("{0:X2}", g.Revision);
            v["vendor"] = g.VendorName;
            v["subvendor"] = g.SubVendorName;
            v["devid"] = string.Format("{0:X4} {1:X4}  ·  {2:X4} {3:X4}", g.VendorId, g.DeviceId, g.SubSysId & 0xFFFF, g.SubSysId >> 16);
            v["bios"] = Na(g.BiosVersion);
            v["bus"] = g.PcieMaxWidth == 0 ? Lang.L("N/A (integrated or not reported)") :
                string.Format("PCIe {0} x{1}   ·   {4} {2} x{3}", PcieGen(g.PcieMaxGen), g.PcieMaxWidth, PcieGen(g.PcieCurGen), g.PcieCurWidth, Lang.L("running at"));
            v["location"] = Na(g.Location);
            v["shaders"] = g.Shaders == 0 ? Lang.L("N/A") : g.Shaders.ToString("N0") + (g.IsNvidia ? " " + Lang.L("CUDA cores") : "");
            v["memsize"] = Mb(g.DedicatedVideoMemory);
            v["buswidth"] = g.MemoryBusWidth == 0 ? Lang.L("N/A") : g.MemoryBusWidth + "-bit";
            v["sharedmem"] = Mb(g.SharedSystemMemory);
            // Without Resizable BAR the CPU sees VRAM through a 256 MB window; with it, through the whole VRAM.
            v["rebar"] = g.Bar1Total == 0 ? Lang.L("N/A") :
                Lang.L(g.Bar1Total > (512UL << 20) ? "Enabled" : "Disabled") + "  ·  BAR1 " + Mb(g.Bar1Total);
            v["powerlimit"] = g.PowerLimitW == 0 ? Lang.L("N/A") : g.PowerLimitW + " W";
            v["gpuclock"] = Mhz(g.MaxGpuClock);
            v["memclock"] = Mhz(g.MaxMemClock);
            v["driver"] = Na(g.DriverVersion);
            v["driverdate"] = Na(g.DriverDate);
            v["provider"] = Na(g.DriverProvider);
            v["nvdriver"] = Na(g.NvDriverVersion);
            v["directx"] = ApiValue(g.DirectX);
            v["vulkan"] = ApiValue(g.Vulkan);
            v["opencl"] = ApiValue(g.OpenCL);
            v["cuda"] = g.CudaVersion == null ? (g.HasNvml && g.DirectX == null ? Lang.L("Detecting…") : Lang.L("Not supported")) :
                g.CudaVersion + (g.ComputeCapability != null ? "  ·  SM " + g.ComputeCapability : "");
            return v;
        }

        private static string ApiValue(string v)
        {
            if (v == null) return Lang.L("Detecting…");
            return v == "Not supported" ? Lang.L("Not supported") : v;
        }

        private void FillOverview()
        {
            GpuDevice g = Selected;
            if (g == null) return;
            Dictionary<string, string> v = Values(g);
            var sections = new List<InfoView.Section>();
            foreach (string[] spec in CardLayout)
            {
                var sec = new InfoView.Section();
                sec.Chips = spec[0].EndsWith("!");
                sec.Title = Lang.L(spec[0].TrimEnd('!'));
                for (int i = 1; i < spec.Length; i++)
                {
                    string key = spec[i].TrimEnd('*');
                    var f = new InfoView.Field();
                    f.Label = Lang.L(Labels[key]);
                    f.Value = v[key];
                    f.Wide = spec[i].EndsWith("*");
                    sec.Fields.Add(f);
                }
                sections.Add(sec);
            }
            infoView.SetSections(sections);

            hero.Title = g.Name;
            hero.Subtitle = string.Format("{0}  ·  {1}  ·  {3} {2}",
                g.Architecture ?? g.VendorName, Mb(g.DedicatedVideoMemory), g.NvDriverVersion ?? Na(g.DriverVersion), Lang.L("driver"));
            hero.Badge = g.VendorName.StartsWith("Unknown") ? "GPU" : g.VendorName.ToUpperInvariant();
            hero.Invalidate();
        }

        // Plain-text report (English, for sharing / bug reports).
        private string BuildReport(GpuDevice g)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("{0} {1} report - {2:yyyy-MM-dd HH:mm:ss}  (by {3})\r\n", AppName, AppVersion, DateTime.Now, Author);
            sb.AppendFormat("OS: {0} ({1})\r\n", Environment.OSVersion.VersionString, Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
            sb.AppendFormat("Privileges: {0}\r\n", elevated ? "administrator" : "standard user");

            Dictionary<string, string> v = Values(g);
            foreach (string[] spec in CardLayout)
            {
                sb.AppendFormat("\r\n[{0}]\r\n", spec[0].TrimEnd('!'));
                for (int i = 1; i < spec.Length; i++)
                {
                    string key = spec[i].TrimEnd('*');
                    sb.AppendFormat("  {0,-22} {1}\r\n", Labels[key] + ":", v[key]);
                }
            }
            sb.AppendLine("\r\n[Details]");
            sb.AppendFormat("  {0,-22} {1}\r\n", "PnP Instance:", Na(g.PnpId));
            sb.AppendFormat("  {0,-22} {1}\r\n", "PCI Bus ID:", Na(g.PciBusId));
            sb.AppendFormat("  {0,-22} {1}\r\n", "Adapter LUID:", g.LuidKey);
            sb.AppendFormat("  {0,-22} {1}\r\n", "Sensor source:", g.HasNvml ? "NVML (nvml.dll)" : "Windows performance counters");

            SensorSet s = hub.For(g);
            if (s.List.Count > 0)
            {
                sb.AppendLine("\r\n[Sensors]                     current        min        max        avg");
                foreach (Sensor x in s.List)
                    sb.AppendFormat("  {0,-24} {1,11} {2,10} {3,10} {4,10}\r\n", x.Name, x.Display(0), x.Display(1), x.Display(2), x.Display(3));
            }
            sb.AppendLine().Append(s.Run.Markdown(g));
            return sb.ToString();
        }

        private void SaveReport()
        {
            GpuDevice g = Selected;
            if (g == null) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Text file (*.txt)|*.txt";
                dlg.FileName = string.Format("GPU-S_report_{0:yyyyMMdd_HHmmss}.txt", DateTime.Now);
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, BuildReport(g), Encoding.UTF8);
                SetStatus(Lang.F("Saved: {0}", dlg.FileName));
            }
        }

        // ------------------------------------------------------------ Sampling, tray, alerts

        private void OnTick(object sender, EventArgs e)
        {
            GpuDevice g = Selected;
            if (g == null) return;
            try
            {
                hub.Sample(g);
                if (monitor.AllGpus != null)
                    foreach (GpuDevice other in gpus) if (other != g) hub.Sample(other);
            }
            catch (Exception) { }
            SensorSet s = hub.For(g);
            if (logger != null)
            {
                try { logger.Write(s); }
                catch (Exception) { ToggleLog(); }
            }
            UpdateTray(g, s);
            CheckAlerts(g, s);

            // Repaint only what is on screen.
            if (mini != null && mini.Visible) mini.View.Invalidate();
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            if (monitorPage.Visible) monitor.Invalidate();
            else if (processView.Visible) RefreshProcesses(g);
            else if (sensorsPage.Visible) sensorView.SetSensors(s);
        }

        private void RefreshProcesses(GpuDevice g)
        {
            Sensor mem = hub.For(g).Find("Memory Used");
            double total = mem != null && mem.FixedMax > 0 ? mem.FixedMax : g.DedicatedVideoMemory / 1048576.0;
            try { processView.SetItems(procSampler.Sample(g.LuidKey), total); } catch (Exception) { }
        }

        private void UpdateTray(GpuDevice g, SensorSet s)
        {
            var sb = new StringBuilder(AppName);
            Sensor load = s.Find("GPU Load"), mem = s.Find(g.MemorySensorName), temp = s.Find("GPU Temperature");
            if (load != null && load.Count > 0) sb.AppendFormat("  GPU {0:0}%", load.Value);
            if (mem != null && mem.Count > 0) sb.AppendFormat("  {0:0.0}{1} GB", mem.Value / 1024, mem.FixedMax > 0 ? string.Format("/{0:0.0}", mem.FixedMax / 1024) : "");
            if (temp != null && temp.Count > 0) sb.AppendFormat("  {0:0}°C", temp.Value);
            if (s.Spilling) sb.Append("  ⚠");
            string t = sb.ToString();
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; // NotifyIcon limit
        }

        private void CheckAlerts(GpuDevice g, SensorSet s)
        {
            Sensor temp = s.Find("GPU Temperature");
            if (settings.TempAlert && temp != null && temp.Count > 0)
            {
                if (!tempAlerted && temp.Value >= settings.TempLimit)
                {
                    tempAlerted = true;
                    tray.ShowBalloonTip(8000, Lang.L("GPU temperature is high"),
                        Lang.F("{0} is at {1:0} °C (alert at {2} °C).", g.Name, temp.Value, settings.TempLimit), ToolTipIcon.Warning);
                }
                else if (tempAlerted && temp.Value < settings.TempLimit - 3) tempAlerted = false;
            }
            Sensor mem = s.Find(g.MemorySensorName);
            if (settings.MemAlert && mem != null && mem.Count > 0 && mem.FixedMax > 0)
            {
                double pct = mem.Value / mem.FixedMax * 100;
                if (!memAlerted && pct >= settings.MemLimit)
                {
                    memAlerted = true;
                    tray.ShowBalloonTip(8000, Lang.L("GPU memory is almost full"),
                        Lang.F("{0}: {1:N0} of {2:N0} MB used ({3:0}%).", g.Name, mem.Value, mem.FixedMax, pct), ToolTipIcon.Warning);
                }
                else if (memAlerted && pct < settings.MemLimit - 3) memAlerted = false;
            }
            if (settings.SpillAlert)
            {
                if (!spillAlerted && s.Spilling)
                {
                    spillAlerted = true;
                    tray.ShowBalloonTip(12000, Lang.L("GPU memory is spilling into system RAM"),
                        Lang.F("{0}: about {1:N0} MB moved to shared system memory, so training may slow down a lot. Reduce the batch size, or set 'CUDA - Sysmem Fallback Policy' to 'Prefer No Sysmem Fallback' in the NVIDIA Control Panel.", g.Name, s.SpillMb),
                        ToolTipIcon.Warning);
                }
                else if (spillAlerted && !s.Spilling && s.SpillMb < 256) spillAlerted = false;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && settings.MinimizeToTray) Hide();
        }

        // Detection, the first paint and JIT compilation touch memory that is never needed again.
        // Once startup is over, hand those pages back to Windows; the monitor then runs on what it really uses.
        private void TrimSoon()
        {
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += delegate
            {
                t.Dispose();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Kernel32.SetProcessWorkingSetSize(Kernel32.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            };
            t.Start();
        }

        private void RestoreMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ToggleMini()
        {
            if (mini != null) { mini.Close(); return; }
            GpuDevice g = Selected;
            var m = new MiniForm();
            m.View.Sensors = g == null ? null : hub.For(g);
            m.View.System = hub.System;
            if (g != null) m.View.MemorySensor = g.MemorySensorName;
            if (Settings.OnScreen(settings.MiniBounds)) m.Bounds = settings.MiniBounds;
            else
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                m.Location = new Point(wa.Right - m.Width - 16, wa.Bottom - m.Height - 16);
            }
            m.View.DoubleClick += delegate { RestoreMain(); };
            m.FormClosed += delegate { settings.MiniBounds = m.Bounds; mini = null; miniPill.Checked = false; };
            mini = m;
            miniPill.Checked = true;
            m.Show();
            WindowState = FormWindowState.Minimized;
        }

        private void ShowSettings()
        {
            using (var f = new SettingsForm(settings))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                bool languageChanged = f.Apply(settings);
                settings.Save();
                tempAlerted = memAlerted = spillAlerted = false;
                SetStatus(Lang.L("Settings saved"));
                if (languageChanged &&
                    MessageBox.Show(this, Lang.L("Restart GPU-S now to change the language?"), AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    Application.Restart();
                }
            }
        }

        // ------------------------------------------------------------ Logging (nvidia-smi CSV format) + run summary

        private void ToggleLog()
        {
            GpuDevice g = Selected;
            if (logger != null)
            {
                logger.Dispose();
                logger = null;
                logPill.Checked = false;
                logPill.Text = "●  " + Lang.L("Log to CSV");
                // Save the run summary next to the CSV.
                string summary = CliLogger.SummaryPathFor(logPath);
                try
                {
                    if (g != null) File.WriteAllText(summary, hub.For(g).Run.Markdown(g), new UTF8Encoding(false));
                    SetStatus(Lang.F("Saved: {0}", summary));
                }
                catch (Exception) { UpdateStatus(); }
                return;
            }
            if (g == null) return;
            string path;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV file (*.csv)|*.csv";
                dlg.FileName = string.Format("GPU-S_log_{0:yyyyMMdd_HHmmss}.csv", DateTime.Now);
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            try { logger = new SensorLogger(new StreamWriter(path, false, new UTF8Encoding(false)), g, settings.LogNoUnits); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            logPath = path;
            hub.For(g).Run = new RunStats(); // the summary covers exactly the logged run
            logPill.Checked = true;
            logPill.Text = "■  " + Lang.L("Stop logging");
            SetStatus(Lang.F("Logging to {0}", path));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (logger != null) ToggleLog(); // writes the run summary
            settings.Bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (mini != null) settings.MiniBounds = mini.Bounds;
            settings.Save();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (mini != null) mini.Close();
            tray.Visible = false;
            tray.Dispose();
            hub.Dispose();
            procSampler.Dispose();
            base.OnFormClosed(e);
        }
    }
}
