// GPU detection: static (one-time) information about each graphics adapter.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GpuS
{
    internal sealed class GpuDevice
    {
        public string Name;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public ulong DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public string LuidKey;                 // "luid_0xHHHHHHHH_0xLLLLLLLL" (matches perf-counter instance names)
        public string PnpId;
        public string DriverVersion, DriverDate, DriverProvider, Location;
        public uint BusNumber = uint.MaxValue;
        public uint PcieCurGen, PcieCurWidth, PcieMaxGen, PcieMaxWidth;

        // Filled asynchronously by the API probe (child process).
        public string DirectX, Vulkan, OpenCL;

        // NVIDIA / NVML
        public IntPtr Nvml;
        public string NvDriverVersion, CudaVersion, BiosVersion, Architecture, ComputeCapability, PciBusId;
        public ulong Bar1Total;        // NVML BAR1 aperture (Resizable BAR on when it is larger than 256 MB)
        public bool HasMemoryTemp;     // NVML reports VRAM temperature for this GPU
        public uint MemoryBusWidth, Shaders, MaxGpuClock, MaxMemClock, MaxVideoClock, PowerLimitW;

        public bool HasNvml { get { return Nvml != IntPtr.Zero; } }
        public bool IsNvidia { get { return VendorId == 0x10DE; } }
        public string VendorName { get { return Vendors.Name(VendorId); } }
        public string SubVendorName { get { return Vendors.Name(SubSysId & 0xFFFF); } }

        // Which memory sensor best describes this GPU's memory use.
        public string MemorySensorName
        {
            get
            {
                if (HasNvml) return "Memory Used";
                return DedicatedVideoMemory >= (512UL << 20) ? "Memory Used (Dedicated)" : "Memory Used (Shared)";
            }
        }

        public string SharedSensorName { get { return HasNvml ? "Shared Memory Used" : "Memory Used (Shared)"; } }

        public string MemoryTitle
        {
            get
            {
                if (HasNvml) return "GPU memory";
                return DedicatedVideoMemory >= (512UL << 20) ? "Dedicated GPU memory" : "Shared GPU memory";
            }
        }

        public override string ToString() { return Name; }
    }

    internal static class Vendors
    {
        private static readonly Dictionary<uint, string> Map = new Dictionary<uint, string>
        {
            { 0x10DE, "NVIDIA" }, { 0x1002, "AMD" }, { 0x1022, "AMD" }, { 0x8086, "Intel" }, { 0x1414, "Microsoft" },
            { 0x5143, "Qualcomm" }, { 0x1028, "Dell" }, { 0x103C, "HP" }, { 0x17AA, "Lenovo" }, { 0x1043, "ASUS" },
            { 0x1458, "Gigabyte" }, { 0x1462, "MSI" }, { 0x3842, "EVGA" }, { 0x19DA, "Zotac" }, { 0x1569, "Palit" },
            { 0x10B0, "Gainward" }, { 0x196E, "PNY" }, { 0x1B4C, "Galax / KFA2" }, { 0x7377, "Colorful" },
            { 0x1DA2, "Sapphire" }, { 0x148C, "PowerColor" }, { 0x1682, "XFX" }, { 0x1849, "ASRock" },
            { 0x174B, "PC Partner" }, { 0x1025, "Acer" }, { 0x144D, "Samsung" }, { 0x1179, "Toshiba" },
            { 0x104D, "Sony" }, { 0x1734, "Fujitsu" }, { 0x152D, "Quanta" }, { 0x1558, "Clevo" }, { 0x1D05, "Tongfang" },
        };

        public static string Name(uint id)
        {
            string n;
            return Map.TryGetValue(id, out n) ? n : string.Format("Unknown (0x{0:X4})", id);
        }
    }

    internal static class GpuDetector
    {
        // Fast, light-weight detection (DXGI enumeration, PnP properties, NVML). No graphics drivers are loaded.
        public static List<GpuDevice> Detect()
        {
            var list = new List<GpuDevice>();
            foreach (var a in EnumerateDxgi(false))
                list.Add(a.Device);
            MatchPnp(list);
            // Remote-desktop sessions can expose a second DXGI view of the same GPU (different LUID,
            // no PnP device). Drop those duplicates.
            list.RemoveAll(x => x.PnpId == null &&
                list.Exists(y => y.PnpId != null && y.VendorId == x.VendorId && y.DeviceId == x.DeviceId));
            foreach (var g in list) ReadPnpProperties(g);
            MatchNvml(list);
            return list;
        }

        private sealed class DxgiAdapter
        {
            public GpuDevice Device;
            public string DirectX;
        }

        private static List<DxgiAdapter> EnumerateDxgi(bool probeDirect3D)
        {
            var result = new List<DxgiAdapter>();
            IDXGIFactory1 factory = null;
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                if (Dxgi.CreateDXGIFactory1(ref iid, out factory) < 0) return result;
                for (uint i = 0; ; i++)
                {
                    IDXGIAdapter1 adapter;
                    if (factory.EnumAdapters1(i, out adapter) < 0 || adapter == null) break;
                    try
                    {
                        DXGI_ADAPTER_DESC1 d;
                        if (adapter.GetDesc1(out d) < 0 || (d.Flags & Dxgi.DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                        var g = new GpuDevice();
                        g.Name = d.Description;
                        g.VendorId = d.VendorId;
                        g.DeviceId = d.DeviceId;
                        g.SubSysId = d.SubSysId;
                        g.Revision = d.Revision;
                        g.DedicatedVideoMemory = d.DedicatedVideoMemory.ToUInt64();
                        g.DedicatedSystemMemory = d.DedicatedSystemMemory.ToUInt64();
                        g.SharedSystemMemory = d.SharedSystemMemory.ToUInt64();
                        g.LuidKey = string.Format("luid_0x{0:X8}_0x{1:X8}", (uint)d.AdapterLuid.HighPart, d.AdapterLuid.LowPart);
                        var a = new DxgiAdapter();
                        a.Device = g;
                        if (probeDirect3D) a.DirectX = Direct3D.DescribeSupport(adapter);
                        result.Add(a);
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            catch (Exception) { }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
            return result;
        }

        private static void MatchPnp(List<GpuDevice> list)
        {
            List<string> ids = CfgMgr.DisplayAdapterIds();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in list)
            {
                string ven = string.Format("VEN_{0:X4}&DEV_{1:X4}", g.VendorId, g.DeviceId);
                string sub = string.Format("SUBSYS_{0:X8}", g.SubSysId);
                string best = null;
                foreach (string id in ids)
                {
                    string u = id.ToUpperInvariant();
                    if (used.Contains(id) || !u.Contains(ven)) continue;
                    if (u.Contains(sub)) { best = id; break; }
                    if (best == null) best = id;
                }
                if (best != null) { g.PnpId = best; used.Add(best); }
            }
        }

        private static void ReadPnpProperties(GpuDevice g)
        {
            if (g.PnpId == null) return;
            g.DriverVersion = CfgMgr.GetString(g.PnpId, CfgMgr.DriverVersion);
            g.DriverDate = CfgMgr.GetDate(g.PnpId, CfgMgr.DriverDate);
            g.DriverProvider = CfgMgr.GetString(g.PnpId, CfgMgr.DriverProvider);
            g.Location = CfgMgr.GetString(g.PnpId, CfgMgr.LocationInfo);
            g.BusNumber = CfgMgr.GetUInt32(g.PnpId, CfgMgr.BusNumber, uint.MaxValue);
            g.PcieCurGen = CfgMgr.GetUInt32(g.PnpId, CfgMgr.PciCurrentLinkSpeed, 0);
            g.PcieCurWidth = CfgMgr.GetUInt32(g.PnpId, CfgMgr.PciCurrentLinkWidth, 0);
            g.PcieMaxGen = CfgMgr.GetUInt32(g.PnpId, CfgMgr.PciMaxLinkSpeed, 0);
            g.PcieMaxWidth = CfgMgr.GetUInt32(g.PnpId, CfgMgr.PciMaxLinkWidth, 0);
        }

        private static void MatchNvml(List<GpuDevice> list)
        {
            if (!list.Exists(x => x.IsNvidia) || !Nvml.Init()) return;
            uint count;
            if (Nvml.nvmlDeviceGetCount_v2(out count) != 0) return;
            string driver = Nvml.DriverVersion();
            // The CUDA version is read in the probe child process: asking NVML for it loads nvcuda.dll
            // (~30 MB image) into the process for good.

            for (uint i = 0; i < count; i++)
            {
                IntPtr h;
                NvmlPciInfo pci;
                if (Nvml.nvmlDeviceGetHandleByIndex_v2(i, out h) != 0 || !Nvml.Pci(h, out pci)) continue;

                // Prefer an exact PCI bus match (handles several identical cards), then fall back to device ID.
                GpuDevice g = list.Find(x => !x.HasNvml && x.IsNvidia && x.BusNumber == pci.Bus && PciId(x) == pci.PciDeviceId);
                if (g == null) g = list.Find(x => !x.HasNvml && x.IsNvidia && PciId(x) == pci.PciDeviceId);
                if (g == null) continue;

                g.Nvml = h;
                g.PciBusId = pci.BusId;
                g.NvDriverVersion = driver;
                g.BiosVersion = Nvml.BiosVersion(h);
                g.Architecture = Nvml.Architecture(h);
                g.ComputeCapability = Nvml.ComputeCapability(h);
                uint v;
                if (Nvml.BusWidth(h, out v)) g.MemoryBusWidth = v;
                if (Nvml.Cores(h, out v)) g.Shaders = v;
                if (Nvml.MaxClock(h, Nvml.CLOCK_GRAPHICS, out v)) g.MaxGpuClock = v;
                if (Nvml.MaxClock(h, Nvml.CLOCK_MEM, out v)) g.MaxMemClock = v;
                if (Nvml.MaxClock(h, Nvml.CLOCK_VIDEO, out v)) g.MaxVideoClock = v;
                if (Nvml.PowerLimit(h, out v)) g.PowerLimitW = v / 1000;
                Nvml.Bar1Total(h, out g.Bar1Total);
                double mt;
                g.HasMemoryTemp = Nvml.MemoryTemperature(h, out mt);
                if (g.PcieMaxGen == 0)
                {
                    Nvml.PcieMaxGen(h, out g.PcieMaxGen);
                    Nvml.PcieMaxWidth(h, out g.PcieMaxWidth);
                    Nvml.PcieCurGen(h, out g.PcieCurGen);
                    Nvml.PcieCurWidth(h, out g.PcieCurWidth);
                }
                NvmlMemory mem;
                if (g.DedicatedVideoMemory == 0 && Nvml.Memory(h, out mem)) g.DedicatedVideoMemory = mem.Total;
            }
        }

        private static uint PciId(GpuDevice g) { return (g.DeviceId << 16) | g.VendorId; }

        // ------------------------------------------------------------ API probe (runs in a child process)
        //
        // Checking DirectX feature level, Vulkan and OpenCL loads the full user-mode graphics driver
        // (tens of MB that never unload). Doing it once in a short-lived child process keeps the
        // monitoring process small.

        public const string ProbeArg = "--probe-apis";

        public static void RunProbe(TextWriter w)
        {
            foreach (var a in EnumerateDxgi(true))
                w.WriteLine("DX\t{0}\t{1}", a.Device.LuidKey, a.DirectX ?? "");
            foreach (var v in Vulkan.Enumerate())
                w.WriteLine("VK\t{0}\t{1}\t{2}", v.VendorId, v.DeviceId, Vulkan.FormatVersion(v.ApiVersion));
            foreach (var c in OpenCL.Enumerate())
                w.WriteLine("CL\t{0}\t{1}", c.VendorId, c.Version ?? "");
            string cuda = Nvml.Init() ? Nvml.CudaVersion() : null;
            if (cuda != null) w.WriteLine("CU\t{0}", cuda);
            w.Flush();
        }

        public static void ApplyProbe(List<GpuDevice> list)
        {
            string output = null;
            try
            {
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, ProbeArg);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    var read = p.StandardOutput.ReadToEndAsync();
                    if (read.Wait(20000)) output = read.Result;
                    else { try { p.Kill(); } catch (Exception) { } }
                }
            }
            catch (Exception) { }

            var usedVk = new HashSet<GpuDevice>();
            var usedCl = new HashSet<GpuDevice>();
            foreach (string line in (output ?? "").Split('\n'))
            {
                string[] f = line.TrimEnd('\r').Split('\t');
                if (f.Length >= 3 && f[0] == "DX")
                {
                    GpuDevice g = list.Find(x => x.LuidKey == f[1]);
                    if (g != null && f[2].Length > 0) g.DirectX = f[2];
                }
                else if (f.Length >= 4 && f[0] == "VK")
                {
                    uint ven, dev;
                    if (!uint.TryParse(f[1], out ven) || !uint.TryParse(f[2], out dev)) continue;
                    GpuDevice g = list.Find(x => !usedVk.Contains(x) && x.VendorId == ven && x.DeviceId == dev);
                    if (g != null) { g.Vulkan = f[3]; usedVk.Add(g); }
                }
                else if (f.Length >= 2 && f[0] == "CU")
                {
                    foreach (var g in list) if (g.HasNvml && f[1].Length > 0) g.CudaVersion = f[1];
                }
                else if (f.Length >= 3 && f[0] == "CL")
                {
                    uint ven;
                    if (!uint.TryParse(f[1], out ven)) continue;
                    GpuDevice g = list.Find(x => !usedCl.Contains(x) && x.VendorId == ven);
                    if (g != null) { g.OpenCL = f[2]; usedCl.Add(g); }
                }
            }
            foreach (var g in list)
            {
                if (g.DirectX == null) g.DirectX = "Not supported";
                if (g.Vulkan == null) g.Vulkan = "Not supported";
                if (g.OpenCL == null) g.OpenCL = "Not supported";
            }
        }
    }
}
