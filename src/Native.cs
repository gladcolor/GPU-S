// Native interop for GPU-S. Every API used here works for a standard (non-admin) user:
//   DXGI / D3D12 / D3D11  - adapter enumeration, memory sizes, DirectX feature level
//   cfgmgr32              - PnP device properties (driver, PCIe link, location)
//   pdh                   - "GPU Engine" / "GPU Adapter Memory" performance counters (what Task Manager uses)
//   nvml                  - NVIDIA Management Library (clocks, temperature, power, fan) - NVIDIA only
//   vulkan-1 / OpenCL     - API version probing
// Written in C# 5 so it builds with the csc.exe that ships with Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuS
{
    // Network byte counters (iphlpapi GetIfTable2; no admin). Sums physical adapters that are up,
    // skipping loopback and the filter-driver copies of each NIC so traffic is not counted twice.
    internal static class IpHelper
    {
        [DllImport("iphlpapi.dll")] private static extern int GetIfTable2(out IntPtr table);
        [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr memory);

        // MIB_IF_ROW2 (x64): 1352 bytes; the table header (NumEntries) is padded to 8 bytes.
        private const int RowSize = 1352, OffType = 1128, OffFlags = 1152, OffOper = 1156, OffIn = 1208, OffOut = 1280;

        public static bool Totals(out ulong rx, out ulong tx)
        {
            rx = tx = 0;
            IntPtr t;
            if (GetIfTable2(out t) != 0 || t == IntPtr.Zero) return false;
            try
            {
                int n = Marshal.ReadInt32(t);
                for (int i = 0; i < n && i < 4096; i++)
                {
                    IntPtr r = t + 8 + i * RowSize;
                    byte flags = Marshal.ReadByte(r, OffFlags);          // bit 0 HardwareInterface, bit 1 FilterInterface
                    if ((flags & 1) == 0 || (flags & 2) != 0) continue;
                    if (Marshal.ReadInt32(r, OffType) == 24 || Marshal.ReadInt32(r, OffOper) != 1) continue; // loopback / not up
                    rx += (ulong)Marshal.ReadInt64(r, OffIn);
                    tx += (ulong)Marshal.ReadInt64(r, OffOut);
                }
                return true;
            }
            finally { FreeMibTable(t); }
        }
    }

    internal static class Kernel32
    {
        private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryExW(string fileName, IntPtr hFile, uint flags);

        // Pre-load a DLL from System32 only, so a same-named file next to the exe is never picked up.
        // Later [DllImport] calls bind to the module that is already loaded.
        public static bool LoadFromSystem32(string name)
        {
            return LoadLibraryExW(name, IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32) != IntPtr.Zero;
        }

        // CPU and RAM (Task Manager's numbers) - two cheap calls, no admin.
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    }

    // ------------------------------------------------------------------ DXGI

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    // Only the vtable slots we call are typed; the others are placeholders that keep the slot order.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    internal static class Dxgi
    {
        public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll")]
        public static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);
    }

    internal static class Direct3D
    {
        [DllImport("d3d12.dll")]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minFeatureLevel, ref Guid riid, IntPtr device);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            int[] featureLevels, uint numFeatureLevels, uint sdkVersion, IntPtr device, out int featureLevel, IntPtr context);

        private static readonly int[] Levels12 = { 0xc200, 0xc100, 0xc000, 0xb100, 0xb000 };
        private static readonly int[] Levels11 = { 0xb100, 0xb000, 0xa100, 0xa000 };

        private static string LevelName(int fl)
        {
            return string.Format("{0}_{1}", (fl >> 12) & 0xF, (fl >> 8) & 0xF);
        }

        // Returns e.g. "12 (feature level 12_2)". Passing a null device pointer only tests support.
        public static string DescribeSupport(object adapter)
        {
            IntPtr unk = IntPtr.Zero;
            try
            {
                unk = Marshal.GetIUnknownForObject(adapter);
                try
                {
                    Guid iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
                    foreach (int fl in Levels12)
                    {
                        if (D3D12CreateDevice(unk, fl, ref iid, IntPtr.Zero) >= 0)
                            return "12  ·  FL " + LevelName(fl);
                    }
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }

                try
                {
                    int got;
                    // D3D_DRIVER_TYPE_UNKNOWN (0) is required when an adapter is given; SDK version 7.
                    if (D3D11CreateDevice(unk, 0, IntPtr.Zero, 0, Levels11, (uint)Levels11.Length, 7, IntPtr.Zero, out got, IntPtr.Zero) >= 0)
                        return (got >= 0xb000 ? "11" : "10") + "  ·  FL " + LevelName(got);
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
            catch (Exception) { }
            finally
            {
                if (unk != IntPtr.Zero) Marshal.Release(unk);
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ Configuration Manager (PnP properties)

    internal static class CfgMgr
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
            public DEVPROPKEY(string guid, uint id) { fmtid = new Guid(guid); pid = id; }
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint propType,
            byte[] buffer, ref uint bufferSize, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint length, uint flags);

        private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100, CM_GETIDLIST_FILTER_CLASS = 0x200;
        private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        // PnP instance IDs of all present display adapters (e.g. "PCI\VEN_10DE&DEV_27B2&SUBSYS_...\4&...").
        public static List<string> DisplayAdapterIds()
        {
            var ids = new List<string>();
            try
            {
                uint flags = CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT;
                uint len;
                if (CM_Get_Device_ID_List_SizeW(out len, DisplayClass, flags) != 0 || len == 0) return ids;
                var buf = new char[len];
                if (CM_Get_Device_ID_ListW(DisplayClass, buf, len, flags) != 0) return ids;
                foreach (string s in new string(buf).Split('\0'))
                    if (s.Length > 0) ids.Add(s);
            }
            catch (Exception) { }
            return ids;
        }

        private const string PciDevice = "3ab22e31-8264-4b4e-9af5-a8d2d8e33e62";
        private const string DeviceGeneral = "a45c254e-df1c-4efd-8020-67d146a850e0";
        private const string DeviceDriver = "a8b865dd-2e3d-4094-ad97-e593a70c75d6";

        public static readonly DEVPROPKEY PciCurrentLinkSpeed = new DEVPROPKEY(PciDevice, 9);
        public static readonly DEVPROPKEY PciCurrentLinkWidth = new DEVPROPKEY(PciDevice, 10);
        public static readonly DEVPROPKEY PciMaxLinkSpeed = new DEVPROPKEY(PciDevice, 11);
        public static readonly DEVPROPKEY PciMaxLinkWidth = new DEVPROPKEY(PciDevice, 12);
        public static readonly DEVPROPKEY LocationInfo = new DEVPROPKEY(DeviceGeneral, 15);
        public static readonly DEVPROPKEY BusNumber = new DEVPROPKEY(DeviceGeneral, 23);
        public static readonly DEVPROPKEY DriverDate = new DEVPROPKEY(DeviceDriver, 2);
        public static readonly DEVPROPKEY DriverVersion = new DEVPROPKEY(DeviceDriver, 3);
        public static readonly DEVPROPKEY DriverProvider = new DEVPROPKEY(DeviceDriver, 9);

        private static byte[] Get(string instanceId, DEVPROPKEY key)
        {
            try
            {
                uint devInst;
                if (CM_Locate_DevNodeW(out devInst, instanceId, 0) != 0) return null;
                byte[] buf = new byte[2048];
                uint size = (uint)buf.Length;
                uint type;
                if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0) != 0) return null;
                byte[] result = new byte[size];
                Array.Copy(buf, result, (int)size);
                return result;
            }
            catch (Exception) { return null; }
        }

        public static uint GetUInt32(string instanceId, DEVPROPKEY key, uint fallback)
        {
            byte[] b = Get(instanceId, key);
            return (b != null && b.Length >= 4) ? BitConverter.ToUInt32(b, 0) : fallback;
        }

        public static string GetString(string instanceId, DEVPROPKEY key)
        {
            byte[] b = Get(instanceId, key);
            return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');
        }

        public static string GetDate(string instanceId, DEVPROPKEY key)
        {
            byte[] b = Get(instanceId, key);
            if (b == null || b.Length < 8) return null;
            try { return DateTime.FromFileTimeUtc(BitConverter.ToInt64(b, 0)).ToString("yyyy-MM-dd"); }
            catch (Exception) { return null; }
        }
    }

    // ------------------------------------------------------------------ PDH performance counters

    internal static class Pdh
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr Name;
            public PDH_FMT_COUNTERVALUE Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
            out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        public static extern int PdhCloseQuery(IntPtr query);

        // Reads all instances of a wildcard counter as (instance name, value) pairs.
        public static List<KeyValuePair<string, double>> ReadArray(IntPtr counter)
        {
            var list = new List<KeyValuePair<string, double>>();
            if (counter == IntPtr.Zero) return list;
            uint size = 0, count;
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero) != PDH_MORE_DATA || size == 0)
                return list;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0) return list;
                int stride = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM));
                for (int i = 0; i < count; i++)
                {
                    var item = (PDH_FMT_COUNTERVALUE_ITEM)Marshal.PtrToStructure(IntPtr.Add(buf, i * stride), typeof(PDH_FMT_COUNTERVALUE_ITEM));
                    if (item.Value.CStatus > 1) continue; // 0 = valid, 1 = new data
                    list.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(item.Name), item.Value.DoubleValue));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }
    }

    // ------------------------------------------------------------------ NVML (NVIDIA)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct NvmlPciInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlUtilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

    // nvmlMemory_v2_t: reports driver-reserved memory separately (what current nvidia-smi shows).
    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlMemoryV2 { public uint Version; public ulong Total; public ulong Reserved; public ulong Free; public ulong Used; }

    internal static class Nvml
    {
        private const string Dll = "nvml.dll";
        public const int CLOCK_GRAPHICS = 0, CLOCK_SM = 1, CLOCK_MEM = 2, CLOCK_VIDEO = 3;

        [DllImport(Dll)] private static extern int nvmlInit_v2();
        [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Dll)] private static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, out NvmlPciInfo pci);
        [DllImport(Dll)] private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
        [DllImport(Dll)] private static extern int nvmlSystemGetCudaDriverVersion(out int version);
        [DllImport(Dll)] private static extern int nvmlDeviceGetVbiosVersion(IntPtr device, StringBuilder version, uint length);
        [DllImport(Dll)] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport(Dll)] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport(Dll)] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport(Dll)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport(Dll)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);
        [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory mem);
        [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryInfo_v2(IntPtr device, ref NvmlMemoryV2 mem);
        private static bool memV2Missing;
        [DllImport(Dll)] private static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport(Dll)] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
        [DllImport(Dll)] private static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport(Dll)] private static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint width);
        [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryBusWidth(IntPtr device, out uint bits);
        [DllImport(Dll)] private static extern int nvmlDeviceGetNumGpuCores(IntPtr device, out uint cores);
        [DllImport(Dll)] private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out uint pstate);
        [DllImport(Dll)] private static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
        [DllImport(Dll)] private static extern int nvmlDeviceGetEncoderUtilization(IntPtr device, out uint util, out uint periodUs);
        [DllImport(Dll)] private static extern int nvmlDeviceGetDecoderUtilization(IntPtr device, out uint util, out uint periodUs);
        [DllImport(Dll)] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
        [DllImport(Dll)] private static extern int nvmlDeviceGetArchitecture(IntPtr device, out uint arch);

        private static bool initialized;

        public static bool Init()
        {
            if (initialized) return true;
            try
            {
                if (!Kernel32.LoadFromSystem32(Dll))
                {
                    // Older drivers installed NVML here instead of System32.
                    string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        @"NVIDIA Corporation\NVSMI\nvml.dll");
                    if (!File.Exists(alt) || Kernel32.LoadLibraryExW(alt, IntPtr.Zero, 0) == IntPtr.Zero) return false;
                }
                initialized = nvmlInit_v2() == 0;
            }
            catch (Exception) { initialized = false; }
            return initialized;
        }

        private delegate int UIntCall(IntPtr device, out uint value);

        // Wraps calls so an older driver that lacks an entry point simply reports "not available".
        private static bool Call(UIntCall f, IntPtr dev, out uint value)
        {
            try { return f(dev, out value) == 0; }
            catch (EntryPointNotFoundException) { value = 0; return false; }
        }

        public static bool Pci(IntPtr d, out NvmlPciInfo pci)
        {
            try { return nvmlDeviceGetPciInfo_v3(d, out pci) == 0; }
            catch (EntryPointNotFoundException) { pci = new NvmlPciInfo(); return false; }
        }

        public static string DriverVersion()
        {
            var sb = new StringBuilder(96);
            try { return nvmlSystemGetDriverVersion(sb, (uint)sb.Capacity) == 0 ? sb.ToString() : null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static string CudaVersion()
        {
            int v;
            try { return nvmlSystemGetCudaDriverVersion(out v) == 0 ? string.Format("{0}.{1}", v / 1000, (v % 1000) / 10) : null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static string BiosVersion(IntPtr d)
        {
            var sb = new StringBuilder(64);
            try { return nvmlDeviceGetVbiosVersion(d, sb, (uint)sb.Capacity) == 0 ? sb.ToString() : null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static string ComputeCapability(IntPtr d)
        {
            int major, minor;
            try { return nvmlDeviceGetCudaComputeCapability(d, out major, out minor) == 0 ? major + "." + minor : null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static string Architecture(IntPtr d)
        {
            uint a;
            if (!Call(nvmlDeviceGetArchitecture, d, out a)) return null;
            switch (a)
            {
                case 2: return "Kepler";
                case 3: return "Maxwell";
                case 4: return "Pascal";
                case 5: return "Volta";
                case 6: return "Turing";
                case 7: return "Ampere";
                case 8: return "Ada Lovelace";
                case 9: return "Hopper";
                case 10: return "Blackwell";
                default: return "Unknown (" + a + ")";
            }
        }

        public static bool Temperature(IntPtr d, out uint v)
        {
            try { return nvmlDeviceGetTemperature(d, 0, out v) == 0; }
            catch (EntryPointNotFoundException) { v = 0; return false; }
        }

        public static bool Clock(IntPtr d, int type, out uint v)
        {
            try { return nvmlDeviceGetClockInfo(d, type, out v) == 0; }
            catch (EntryPointNotFoundException) { v = 0; return false; }
        }

        public static bool MaxClock(IntPtr d, int type, out uint v)
        {
            try { return nvmlDeviceGetMaxClockInfo(d, type, out v) == 0; }
            catch (EntryPointNotFoundException) { v = 0; return false; }
        }

        public static bool Utilization(IntPtr d, out NvmlUtilization u)
        {
            try { return nvmlDeviceGetUtilizationRates(d, out u) == 0; }
            catch (EntryPointNotFoundException) { u = new NvmlUtilization(); return false; }
        }

        public static bool Memory(IntPtr d, out NvmlMemory m)
        {
            if (!memV2Missing)
            {
                try
                {
                    var v2 = new NvmlMemoryV2();
                    v2.Version = (uint)Marshal.SizeOf(typeof(NvmlMemoryV2)) | (2u << 24); // NVML_STRUCT_VERSION(Memory, 2)
                    if (nvmlDeviceGetMemoryInfo_v2(d, ref v2) == 0)
                    {
                        m = new NvmlMemory { Total = v2.Total, Free = v2.Free, Used = v2.Used };
                        return true;
                    }
                }
                catch (EntryPointNotFoundException) { memV2Missing = true; }
            }
            try { return nvmlDeviceGetMemoryInfo(d, out m) == 0; }
            catch (EntryPointNotFoundException) { m = new NvmlMemory(); return false; }
        }

        public static bool EncoderUtil(IntPtr d, out uint v)
        {
            uint period;
            try { return nvmlDeviceGetEncoderUtilization(d, out v, out period) == 0; }
            catch (EntryPointNotFoundException) { v = 0; return false; }
        }

        public static bool DecoderUtil(IntPtr d, out uint v)
        {
            uint period;
            try { return nvmlDeviceGetDecoderUtilization(d, out v, out period) == 0; }
            catch (EntryPointNotFoundException) { v = 0; return false; }
        }

        public static bool ThrottleReasons(IntPtr d, out ulong v)
        {
            try { return nvmlDeviceGetCurrentClocksThrottleReasons(d, out v) == 0; }
            catch (EntryPointNotFoundException) { v = 0; return false; }
        }

        public static bool PowerUsage(IntPtr d, out uint mw) { return Call(nvmlDeviceGetPowerUsage, d, out mw); }
        public static bool PowerLimit(IntPtr d, out uint mw) { return Call(nvmlDeviceGetEnforcedPowerLimit, d, out mw); }
        public static bool FanSpeed(IntPtr d, out uint pct) { return Call(nvmlDeviceGetFanSpeed, d, out pct); }
        public static bool PState(IntPtr d, out uint p) { return Call(nvmlDeviceGetPerformanceState, d, out p); }
        public static bool BusWidth(IntPtr d, out uint bits) { return Call(nvmlDeviceGetMemoryBusWidth, d, out bits); }
        public static bool Cores(IntPtr d, out uint n) { return Call(nvmlDeviceGetNumGpuCores, d, out n); }
        public static bool PcieCurGen(IntPtr d, out uint v) { return Call(nvmlDeviceGetCurrPcieLinkGeneration, d, out v); }
        public static bool PcieCurWidth(IntPtr d, out uint v) { return Call(nvmlDeviceGetCurrPcieLinkWidth, d, out v); }
        public static bool PcieMaxGen(IntPtr d, out uint v) { return Call(nvmlDeviceGetMaxPcieLinkGeneration, d, out v); }
        public static bool PcieMaxWidth(IntPtr d, out uint v) { return Call(nvmlDeviceGetMaxPcieLinkWidth, d, out v); }
    }

    // ------------------------------------------------------------------ Vulkan

    internal sealed class VkDeviceInfo
    {
        public uint VendorId, DeviceId, ApiVersion, DriverVersion;
        public string Name;
    }

    internal static class Vulkan
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct VkInstanceCreateInfo
        {
            public int sType;               // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1
            public IntPtr pNext;
            public uint flags;
            public IntPtr pApplicationInfo; // may be NULL
            public uint enabledLayerCount;
            public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount;
            public IntPtr ppEnabledExtensionNames;
        }

        [DllImport("vulkan-1.dll")] private static extern int vkCreateInstance(ref VkInstanceCreateInfo info, IntPtr allocator, out IntPtr instance);
        [DllImport("vulkan-1.dll")] private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
        [DllImport("vulkan-1.dll")] private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[] devices);
        [DllImport("vulkan-1.dll")] private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

        public static List<VkDeviceInfo> Enumerate()
        {
            var list = new List<VkDeviceInfo>();
            if (!Kernel32.LoadFromSystem32("vulkan-1.dll")) return list;
            IntPtr instance = IntPtr.Zero;
            try
            {
                var info = new VkInstanceCreateInfo();
                info.sType = 1;
                if (vkCreateInstance(ref info, IntPtr.Zero, out instance) != 0) { instance = IntPtr.Zero; return list; }
                uint count = 0;
                if (vkEnumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0) return list;
                var devices = new IntPtr[count];
                if (vkEnumeratePhysicalDevices(instance, ref count, devices) < 0) return list;

                // VkPhysicalDeviceProperties is ~830 bytes; we only read the leading fields.
                IntPtr props = Marshal.AllocHGlobal(4096);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        vkGetPhysicalDeviceProperties(devices[i], props);
                        var d = new VkDeviceInfo();
                        d.ApiVersion = (uint)Marshal.ReadInt32(props, 0);
                        d.DriverVersion = (uint)Marshal.ReadInt32(props, 4);
                        d.VendorId = (uint)Marshal.ReadInt32(props, 8);
                        d.DeviceId = (uint)Marshal.ReadInt32(props, 12);
                        d.Name = Marshal.PtrToStringAnsi(IntPtr.Add(props, 20));
                        list.Add(d);
                    }
                }
                finally { Marshal.FreeHGlobal(props); }
            }
            catch (Exception) { }
            finally
            {
                if (instance != IntPtr.Zero)
                {
                    try { vkDestroyInstance(instance, IntPtr.Zero); } catch (Exception) { }
                }
            }
            return list;
        }

        public static string FormatVersion(uint v)
        {
            return string.Format("{0}.{1}.{2}", (v >> 22) & 0x7F, (v >> 12) & 0x3FF, v & 0xFFF);
        }
    }

    // ------------------------------------------------------------------ OpenCL

    internal sealed class ClDeviceInfo
    {
        public uint VendorId;
        public string Name, Version;
    }

    internal static class OpenCL
    {
        private const ulong CL_DEVICE_TYPE_GPU = 1 << 2;
        private const uint CL_DEVICE_VENDOR_ID = 0x1001, CL_DEVICE_NAME = 0x102B, CL_DEVICE_VERSION = 0x102F;

        [DllImport("OpenCL.dll")] private static extern int clGetPlatformIDs(uint num, [Out] IntPtr[] platforms, out uint numPlatforms);
        [DllImport("OpenCL.dll")] private static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint num, [Out] IntPtr[] devices, out uint numDevices);
        [DllImport("OpenCL.dll")] private static extern int clGetDeviceInfo(IntPtr device, uint param, UIntPtr size, byte[] value, out UIntPtr sizeRet);

        private static byte[] Info(IntPtr dev, uint param)
        {
            UIntPtr size;
            if (clGetDeviceInfo(dev, param, UIntPtr.Zero, null, out size) != 0) return null;
            var buf = new byte[(int)size.ToUInt64()];
            if (clGetDeviceInfo(dev, param, size, buf, out size) != 0) return null;
            return buf;
        }

        private static string InfoString(IntPtr dev, uint param)
        {
            byte[] b = Info(dev, param);
            return b == null ? null : Encoding.ASCII.GetString(b).TrimEnd('\0').Trim();
        }

        public static List<ClDeviceInfo> Enumerate()
        {
            var list = new List<ClDeviceInfo>();
            if (!Kernel32.LoadFromSystem32("OpenCL.dll")) return list;
            try
            {
                uint np;
                if (clGetPlatformIDs(0, null, out np) != 0 || np == 0) return list;
                var platforms = new IntPtr[np];
                if (clGetPlatformIDs(np, platforms, out np) != 0) return list;
                foreach (IntPtr p in platforms)
                {
                    uint nd;
                    if (clGetDeviceIDs(p, CL_DEVICE_TYPE_GPU, 0, null, out nd) != 0 || nd == 0) continue;
                    var devices = new IntPtr[nd];
                    if (clGetDeviceIDs(p, CL_DEVICE_TYPE_GPU, nd, devices, out nd) != 0) continue;
                    foreach (IntPtr d in devices)
                    {
                        byte[] vid = Info(d, CL_DEVICE_VENDOR_ID);
                        var info = new ClDeviceInfo();
                        info.VendorId = (vid != null && vid.Length >= 4) ? BitConverter.ToUInt32(vid, 0) : 0;
                        info.Name = InfoString(d, CL_DEVICE_NAME);
                        info.Version = InfoString(d, CL_DEVICE_VERSION);
                        list.Add(info);
                    }
                }
            }
            catch (Exception) { }
            return list;
        }
    }
}
