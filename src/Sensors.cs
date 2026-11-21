// Live sensors. Only the GPU being viewed is sampled, so idle cost is tiny.
//   NVIDIA:  NVML only (~15 cheap driver calls per sample).
//   Others:  Windows "GPU Engine" / "GPU Adapter Memory" counters, opened lazily on first use.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuS
{
    internal sealed class Sensor
    {
        public const int HistoryLength = 7200; // 1 hour at 0.5 s (float = 28 KB per sensor)

        public readonly string Name, Unit, Format;
        public readonly double FixedMax;   // graph scale; 0 = auto
        public double Value = double.NaN;
        public string Text;                // text-only sensor (e.g. PerfCap reason)
        public double Min, Max, Sum;
        public long Count;
        private readonly float[] history = new float[HistoryLength];
        private int head, filled;

        public Sensor(string name, string unit, string format, double fixedMax)
        {
            Name = name; Unit = unit; Format = format; FixedMax = fixedMax;
            ResetStats();
        }

        public void Push(double v)
        {
            Value = v;
            if (v < Min) Min = v;
            if (v > Max) Max = v;
            Sum += v;
            Count++;
            history[head] = (float)v;
            head = (head + 1) % HistoryLength;
            if (filled < HistoryLength) filled++;
        }

        public void ResetStats()
        {
            Min = double.MaxValue; Max = double.MinValue; Sum = 0; Count = 0;
        }

        public int HistoryCount { get { return filled; } }

        // i = 0 is the oldest retained sample.
        public double HistoryAt(int i)
        {
            return history[(head - filled + i + HistoryLength) % HistoryLength];
        }

        public string Display(int mode) // 0 current, 1 min, 2 max, 3 average
        {
            if (Text != null) return Text;
            if (Count == 0) return "-";
            double v = mode == 1 ? Min : mode == 2 ? Max : mode == 3 ? Sum / Count : Value;
            string s = v.ToString(Format);
            return Unit.Length > 0 ? s + " " + Unit : s;
        }
    }

    // Formatting for network rates and byte counts (binary units, as in Explorer and browsers on Windows).
    internal static class Units
    {
        private static readonly string[] Names = { "B", "KB", "MB", "GB", "TB" };

        private static int Level(double v)
        {
            int k = 0;
            while (k < 4 && Math.Abs(v) >= 1000) { v /= 1024; k++; }
            return k;
        }

        private static string Fmt(double v, int k, IFormatProvider f)
        {
            double x = v / Math.Pow(1024, k);
            return x.ToString(x >= 100 || k == 0 ? "0" : x >= 10 ? "0.#" : "0.##", f) + " " + Names[k];
        }

        public static string Bytes(double v, IFormatProvider f) { return Fmt(v, Level(v), f); }
        public static string Rate(double v, IFormatProvider f) { return Fmt(v, Math.Max(1, Level(v)), f) + "/s"; }

        // Both rates in one unit (picked by the larger), e.g. "↓ 12.3  ↑ 0.4 MB/s".
        public static string RatePair(double rx, double tx)
        {
            int k = Math.Max(1, Level(Math.Max(rx, tx)));
            double d = Math.Pow(1024, k);
            string fmt = Math.Max(rx, tx) / d >= 100 ? "0" : "0.0";
            return "↓ " + (rx / d).ToString(fmt) + "  ↑ " + (tx / d).ToString(fmt) + " " + Names[k] + "/s";
        }

        // Graph scale: next 1-2-5 step above v, at least 100 KB/s so idle noise stays flat.
        public static double NiceScale(double v)
        {
            v = Math.Max(v, 100 * 1024);
            int k = Level(v);
            double unit = Math.Pow(1024, k), x = v / unit, p = Math.Pow(10, Math.Floor(Math.Log10(x)));
            double n = x <= p ? p : x <= 2 * p ? 2 * p : x <= 5 * p ? 5 * p : 10 * p;
            if (n >= 1000) { n = 1; unit *= 1024; } // 1000 KB/s -> 1 MB/s
            return n * unit;
        }
    }

    internal sealed class SensorSet
    {
        public readonly List<Sensor> List = new List<Sensor>();
        private readonly Dictionary<string, Sensor> byName = new Dictionary<string, Sensor>();
        public bool HasThrottle;     // NVML throttle reasons available
        public ulong ThrottleRaw;    // nvmlClocksThrottleReasons bit mask
        public RunStats Run = new RunStats();
        public bool Spilling;        // dedicated memory nearly full and shared (system RAM) use grew
        public double SpillMb, SharedBaseline = double.MaxValue;
        public double NetRxTotal, NetTxTotal; // system set only: bytes since GPU-S started

        // Short description of throttling that actually limits performance, or null.
        public string ThrottleWarning
        {
            get
            {
                if (!HasThrottle) return null;
                if ((ThrottleRaw & 0x1) != 0) return null; // GPU idle: other bits are not a real slowdown
                if ((ThrottleRaw & 0x60) != 0) return "Thermal throttling";
                if ((ThrottleRaw & 0x08) != 0) return "HW slowdown";
                if ((ThrottleRaw & 0x84) != 0) return "Power limit";
                return null;
            }
        }

        public Sensor Get(string name, string unit, string format, double fixedMax)
        {
            Sensor s;
            if (!byName.TryGetValue(name, out s))
            {
                s = new Sensor(name, unit, format, fixedMax);
                byName[name] = s;
                List.Add(s);
            }
            return s;
        }

        public Sensor Find(string name)
        {
            Sensor s;
            return byName.TryGetValue(name, out s) ? s : null;
        }

        public void ResetStats()
        {
            foreach (var s in List) s.ResetStats();
            Run = new RunStats();
        }
    }

    // Accumulates a run summary (for papers / job tuning): peak memory, average load, max temperature, throttling.
    internal sealed class RunStats
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public readonly DateTime Start = DateTime.Now;
        public long Samples, UtilN, PowerN, ThrottleKnown, PowerThrottled, ThermalThrottled, SpillSamples;
        public double UtilSum, UtilMax, MemPeak, MemTotal, SharedPeak, TempMax = double.NaN, PowerSum, PowerMax;
        public long CpuN;
        public double CpuSum, CpuMax, RamPeak, RamTotal;
        public double RxStart = double.NaN, TxStart, RxEnd, TxEnd, RxPeak, TxPeak;
        public bool HasNet;

        public void Add(SensorSet s, GpuDevice g, SensorSet sys)
        {
            Samples++;
            if (sys != null)
            {
                if (double.IsNaN(RxStart)) { RxStart = sys.NetRxTotal; TxStart = sys.NetTxTotal; }
                RxEnd = sys.NetRxTotal; TxEnd = sys.NetTxTotal;
                Sensor n = sys.Find("Net Receive");
                if (n != null && n.Count > 0) { HasNet = true; RxPeak = Math.Max(RxPeak, n.Value); }
                n = sys.Find("Net Send");
                if (n != null && n.Count > 0) TxPeak = Math.Max(TxPeak, n.Value);
            }
            Sensor c = sys == null ? null : sys.Find("CPU Usage");
            if (c != null && c.Count > 0) { CpuSum += c.Value; CpuN++; CpuMax = Math.Max(CpuMax, c.Value); }
            c = sys == null ? null : sys.Find("RAM Used");
            if (c != null && c.Count > 0) { RamPeak = Math.Max(RamPeak, c.Value); RamTotal = c.FixedMax; }
            Sensor x = s.Find("GPU Load");
            if (x != null && x.Count > 0) { UtilSum += x.Value; UtilN++; UtilMax = Math.Max(UtilMax, x.Value); }
            x = s.Find(g.MemorySensorName);
            if (x != null && x.Count > 0) { MemPeak = Math.Max(MemPeak, x.Value); MemTotal = x.FixedMax; }
            x = s.Find(g.SharedSensorName);
            if (x != null && x.Count > 0) SharedPeak = Math.Max(SharedPeak, x.Value);
            x = s.Find("GPU Temperature");
            if (x != null && x.Count > 0) TempMax = double.IsNaN(TempMax) ? x.Value : Math.Max(TempMax, x.Value);
            x = s.Find("Board Power Draw");
            if (x != null && x.Count > 0) { PowerSum += x.Value; PowerN++; PowerMax = Math.Max(PowerMax, x.Value); }
            if (s.HasThrottle && (s.ThrottleRaw & 0x1) == 0) // ignore idle samples
            {
                ThrottleKnown++;
                if ((s.ThrottleRaw & 0x84) != 0) PowerThrottled++;
                if ((s.ThrottleRaw & 0x68) != 0) ThermalThrottled++;
            }
            if (s.Spilling) SpillSamples++;
        }

        private static string Duration(TimeSpan d)
        {
            if (d.TotalHours >= 1) return string.Format(Inv, "{0} h {1:00} min", (int)d.TotalHours, d.Minutes);
            if (d.TotalMinutes >= 1) return string.Format(Inv, "{0} min {1:00} s", (int)d.TotalMinutes, d.Seconds);
            return string.Format(Inv, "{0} s", (int)d.TotalSeconds);
        }

        private static void Row(StringBuilder sb, string metric, string value)
        {
            sb.Append("| ").Append(metric).Append(" | ").Append(value).AppendLine(" |");
        }

        // English Markdown table, ready to paste into a paper draft, issue or lab notebook.
        public string Markdown(GpuDevice g)
        {
            var sb = new StringBuilder();
            DateTime now = DateTime.Now;
            sb.AppendLine("### GPU run summary — " + g.Name);
            sb.AppendLine();
            sb.AppendLine(string.Format(Inv, "{0:yyyy-MM-dd HH:mm:ss} to {1:HH:mm:ss} ({2}, {3:N0} samples)", Start, now, Duration(now - Start), Samples));
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|---|---|");
            Row(sb, "GPU utilization", UtilN > 0 ? string.Format(Inv, "avg {0:0.0} %, max {1:0} %", UtilSum / UtilN, UtilMax) : "n/a");
            Row(sb, "Peak GPU memory", MemTotal > 0
                ? string.Format(Inv, "{0:N0} / {1:N0} MiB ({2:0.0} %)", MemPeak, MemTotal, 100 * MemPeak / MemTotal)
                : string.Format(Inv, "{0:N0} MiB", MemPeak));
            if (SharedPeak > 0) Row(sb, "Peak shared (system RAM) GPU memory", string.Format(Inv, "{0:N0} MiB", SharedPeak));
            if (!double.IsNaN(TempMax)) Row(sb, "Max temperature", string.Format(Inv, "{0:0} °C", TempMax));
            if (PowerN > 0) Row(sb, "Board power", string.Format(Inv, "avg {0:0.0} W, max {1:0.0} W", PowerSum / PowerN, PowerMax));
            if (ThrottleKnown > 0)
                Row(sb, "Time throttled", string.Format(Inv, "power limit {0:0.0} %, thermal/HW {1:0.0} %",
                    100.0 * PowerThrottled / ThrottleKnown, 100.0 * ThermalThrottled / ThrottleKnown));
            if (g.DedicatedVideoMemory >= (2UL << 30))
                Row(sb, "Spilled into system RAM", string.Format(Inv, "{0:0.0} % of the time", Samples > 0 ? 100.0 * SpillSamples / Samples : 0));
            if (CpuN > 0) Row(sb, "CPU utilization", string.Format(Inv, "avg {0:0.0} %, max {1:0} %", CpuSum / CpuN, CpuMax));
            if (RamTotal > 0) Row(sb, "Peak system RAM", string.Format(Inv, "{0:N0} / {1:N0} MiB ({2:0.0} %)", RamPeak, RamTotal, 100 * RamPeak / RamTotal));
            if (HasNet)
                Row(sb, "Network (physical adapters)", string.Format(Inv, "received {0} (peak {1}), sent {2} (peak {3})",
                    Units.Bytes(RxEnd - RxStart, Inv), Units.Rate(RxPeak, Inv), Units.Bytes(TxEnd - TxStart, Inv), Units.Rate(TxPeak, Inv)));
            if (g.CudaVersion == null && g.HasNvml) g.CudaVersion = Nvml.CudaVersion(); // command-line logging has no probe
            string drv = g.NvDriverVersion ?? g.DriverVersion ?? "n/a";
            Row(sb, "Driver", g.CudaVersion != null ? drv + ", CUDA " + g.CudaVersion : drv);
            sb.AppendLine();
            sb.AppendLine("_Measured with GPU-S " + MainForm.AppVersion + " (NVML; same readings as nvidia-smi)._");
            return sb.ToString();
        }
    }

    internal sealed class SensorHub : IDisposable
    {
        private readonly Dictionary<GpuDevice, SensorSet> sets = new Dictionary<GpuDevice, SensorSet>();
        private IntPtr query, engineCounter, dedicatedCounter, sharedCounter;
        private bool pdhTried;

        public SensorSet For(GpuDevice g)
        {
            SensorSet s;
            if (!sets.TryGetValue(g, out s)) { s = new SensorSet(); sets[g] = s; }
            return s;
        }

        public void Sample(GpuDevice g)
        {
            SampleSystem();
            SensorSet s = For(g);
            if (g.HasNvml) { SampleNvml(g.Nvml, s); SampleShared(g, s); }
            else SamplePdh(g, s);
            UpdateSpill(g, s);
            s.Run.Add(s, g, System);
        }

        // ------------------------------------------------------------ CPU / system RAM

        public readonly SensorSet System = new SensorSet();
        private long lastIdle, lastKernel, lastUser;
        private DateTime lastSystem = DateTime.MinValue;

        public void SampleSystem()
        {
            if ((DateTime.UtcNow - lastSystem).TotalMilliseconds < 250) return; // once per tick, even in All-GPUs mode
            lastSystem = DateTime.UtcNow;
            long idle, kernel, user;
            if (Kernel32.GetSystemTimes(out idle, out kernel, out user))
            {
                if (lastKernel != 0)
                {
                    long total = (kernel - lastKernel) + (user - lastUser); // kernel time includes idle time
                    long idleD = idle - lastIdle;
                    if (total > 0) System.Get("CPU Usage", "%", "0", 100).Push(Math.Max(0, Math.Min(100, 100.0 * (total - idleD) / total)));
                }
                lastIdle = idle; lastKernel = kernel; lastUser = user;
            }
            var m = new Kernel32.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Kernel32.MEMORYSTATUSEX));
            if (Kernel32.GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
                System.Get("RAM Used", "MB", "N0", m.ullTotalPhys / 1048576.0).Push((m.ullTotalPhys - m.ullAvailPhys) / 1048576.0);
            SampleNetwork();
        }

        private ulong lastRx, lastTx;
        private DateTime lastNet = DateTime.MinValue;
        private bool netFailed;

        // Receive / send rates in bytes per second, plus running byte totals for the run summary.
        private void SampleNetwork()
        {
            if (netFailed) return;
            ulong rx, tx;
            try { if (!IpHelper.Totals(out rx, out tx)) return; }
            catch (Exception) { netFailed = true; return; }
            DateTime now = DateTime.UtcNow;
            if (lastNet != DateTime.MinValue)
            {
                double dt = (now - lastNet).TotalSeconds;
                bool ok = rx >= lastRx && tx >= lastTx && dt > 0; // an adapter going down can lower the sum
                double dRx = ok ? rx - lastRx : 0, dTx = ok ? tx - lastTx : 0;
                System.NetRxTotal += dRx; System.NetTxTotal += dTx;
                System.Get("Net Receive", "B/s", "N0", 0).Push(ok ? dRx / dt : 0);
                System.Get("Net Send", "B/s", "N0", 0).Push(ok ? dTx / dt : 0);
            }
            lastRx = rx; lastTx = tx; lastNet = now;
        }

        // NVIDIA: one tiny counter (a few instances) for shared GPU memory, used to detect sysmem fallback.
        private IntPtr sharedQuery, sharedOnlyCounter;
        private bool sharedTried;

        private void SampleShared(GpuDevice g, SensorSet s)
        {
            if (!sharedTried)
            {
                sharedTried = true;
                if (Pdh.PdhOpenQueryW(null, IntPtr.Zero, out sharedQuery) != 0) sharedQuery = IntPtr.Zero;
                else Pdh.PdhAddEnglishCounterW(sharedQuery, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out sharedOnlyCounter);
            }
            if (sharedQuery == IntPtr.Zero || Pdh.PdhCollectQueryData(sharedQuery) != 0) return;
            PushMemory(s, "Shared Memory Used", sharedOnlyCounter, g.LuidKey, g.SharedSystemMemory / 1048576.0);
        }

        // Windows "Sysmem Fallback": when VRAM is full the driver silently moves allocations to system RAM,
        // which makes training much slower. Flag it when dedicated memory is >= 90% and shared use grew >= 512 MB.
        private static void UpdateSpill(GpuDevice g, SensorSet s)
        {
            s.Spilling = false;
            if (g.DedicatedVideoMemory < (2UL << 30)) return; // integrated GPUs live in shared memory
            Sensor mem = s.Find(g.MemorySensorName), sh = s.Find(g.SharedSensorName);
            if (mem == null || sh == null || mem.Count == 0 || sh.Count == 0 || mem.FixedMax <= 0) return;
            if (sh.Value < s.SharedBaseline) s.SharedBaseline = sh.Value;
            s.SpillMb = sh.Value - s.SharedBaseline;
            s.Spilling = mem.Value / mem.FixedMax >= 0.9 && s.SpillMb >= 512;
        }

        // ------------------------------------------------------------ NVIDIA

        private static void SampleNvml(IntPtr h, SensorSet s)
        {
            uint v;
            if (Nvml.Clock(h, Nvml.CLOCK_GRAPHICS, out v)) s.Get("GPU Clock", "MHz", "0", 0).Push(v);
            if (Nvml.Clock(h, Nvml.CLOCK_MEM, out v)) s.Get("Memory Clock", "MHz", "0", 0).Push(v);
            if (Nvml.Clock(h, Nvml.CLOCK_VIDEO, out v)) s.Get("Video Clock", "MHz", "0", 0).Push(v);
            if (Nvml.Temperature(h, out v)) s.Get("GPU Temperature", "°C", "0", 100).Push(v);
            if (Nvml.FanSpeed(h, out v)) s.Get("Fan Speed", "%", "0", 100).Push(v);

            uint mw, limit;
            if (Nvml.PowerUsage(h, out mw))
            {
                s.Get("Board Power Draw", "W", "0.0", 0).Push(mw / 1000.0);
                if (Nvml.PowerLimit(h, out limit) && limit > 0)
                    s.Get("Power (% of limit)", "%", "0.0", 100).Push(100.0 * mw / limit);
            }

            NvmlUtilization u;
            if (Nvml.Utilization(h, out u))
            {
                s.Get("GPU Load", "%", "0", 100).Push(u.Gpu);
                s.Get("Memory Controller Load", "%", "0", 100).Push(u.Memory);
            }
            if (Nvml.EncoderUtil(h, out v)) s.Get("Video Encoder Load", "%", "0", 100).Push(v);
            if (Nvml.DecoderUtil(h, out v)) s.Get("Video Decoder Load", "%", "0", 100).Push(v);

            NvmlMemory m;
            if (Nvml.Memory(h, out m) && m.Total > 0)
            {
                s.Get("Memory Used", "MB", "N0", m.Total / 1048576.0).Push(m.Used / 1048576.0);
                s.Get("Memory Free", "MB", "N0", m.Total / 1048576.0).Push(m.Free / 1048576.0); // excludes driver-reserved
                s.Get("Memory Used (%)", "%", "0.0", 100).Push(100.0 * m.Used / m.Total);
            }

            if (Nvml.PState(h, out v) && v < 32) s.Get("Performance State", "", "'P'0", 0).Push(v);

            ulong reasons;
            if (Nvml.ThrottleReasons(h, out reasons))
            {
                Sensor r = s.Get("PerfCap Reason", "", "0", 0);
                r.Push(reasons == 0 ? 0 : 1);
                r.Text = PerfCapText(reasons);
                s.HasThrottle = true;
                s.ThrottleRaw = reasons;
            }
        }

        private static string PerfCapText(ulong r)
        {
            if (r == 0) return "None";
            var sb = new StringBuilder();
            Add(sb, r, 0x001, "Idle");
            Add(sb, r, 0x002, "AppClk");
            Add(sb, r, 0x004, "Power");
            Add(sb, r, 0x008, "HW Slowdown");
            Add(sb, r, 0x010, "SyncBoost");
            Add(sb, r, 0x020, "Thermal");
            Add(sb, r, 0x040, "HW Thermal");
            Add(sb, r, 0x080, "Power Brake");
            Add(sb, r, 0x100, "Display Clk");
            return sb.Length > 0 ? sb.ToString() : string.Format("0x{0:X}", r);
        }

        private static void Add(StringBuilder sb, ulong r, ulong bit, string name)
        {
            if ((r & bit) == 0) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name);
        }

        // ------------------------------------------------------------ Generic (performance counters)

        private static readonly string[] EngineOrder = { "3D", "Compute", "Copy", "VideoDecode", "VideoEncode", "VideoProcessing" };
        private static readonly string[] EngineLabel = { "3D Engine", "Compute Engine", "Copy Engine", "Video Decode", "Video Encode", "Video Processing" };

        private void EnsurePdh()
        {
            if (pdhTried) return;
            pdhTried = true;
            if (Pdh.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
            Pdh.PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out engineCounter);
            Pdh.PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out dedicatedCounter);
            Pdh.PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out sharedCounter);
            Pdh.PdhCollectQueryData(query); // rate counters need a first sample
        }

        private void SamplePdh(GpuDevice g, SensorSet s)
        {
            EnsurePdh();
            if (query == IntPtr.Zero || Pdh.PdhCollectQueryData(query) != 0) return;
            string luid = g.LuidKey;

            // Instance: pid_<n>_luid_0x..._0x..._phys_0_eng_<i>_engtype_<type>. Sum across processes per engine,
            // then take the busiest engine of each type (same method as Task Manager).
            var perEngine = new Dictionary<string, double>();
            foreach (var kv in Pdh.ReadArray(engineCounter))
            {
                string n = kv.Key;
                if (n.IndexOf(luid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int e = n.IndexOf("_eng_", StringComparison.Ordinal);
                if (e < 0) continue;
                string key = n.Substring(e);
                double cur;
                perEngine.TryGetValue(key, out cur);
                perEngine[key] = cur + kv.Value;
            }
            var perType = new Dictionary<string, double>();
            double load = 0;
            foreach (var kv in perEngine)
            {
                int t = kv.Key.IndexOf("_engtype_", StringComparison.Ordinal);
                if (t < 0) continue;
                string type = kv.Key.Substring(t + 9);
                if (type.StartsWith("Compute", StringComparison.OrdinalIgnoreCase)) type = "Compute";
                double v = Math.Min(100, kv.Value), cur;
                perType.TryGetValue(type, out cur);
                perType[type] = Math.Max(cur, v);
                load = Math.Max(load, v);
            }

            s.Get("GPU Load", "%", "0", 100).Push(load);
            for (int i = 0; i < EngineOrder.Length; i++)
            {
                double v;
                if (perType.TryGetValue(EngineOrder[i], out v)) s.Get(EngineLabel[i], "%", "0", 100).Push(v);
            }

            PushMemory(s, "Memory Used (Dedicated)", dedicatedCounter, luid, g.DedicatedVideoMemory / 1048576.0);
            PushMemory(s, "Memory Used (Shared)", sharedCounter, luid, g.SharedSystemMemory / 1048576.0);
        }

        private static void PushMemory(SensorSet s, string name, IntPtr counter, string luid, double totalMb)
        {
            double sum = 0;
            bool any = false;
            foreach (var kv in Pdh.ReadArray(counter))
            {
                if (kv.Key.IndexOf(luid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sum += kv.Value;
                any = true;
            }
            if (any) s.Get(name, "MB", "N0", totalMb).Push(sum / 1048576.0);
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { Pdh.PdhCloseQuery(query); query = IntPtr.Zero; }
            if (sharedQuery != IntPtr.Zero) { Pdh.PdhCloseQuery(sharedQuery); sharedQuery = IntPtr.Zero; }
        }
    }
}
