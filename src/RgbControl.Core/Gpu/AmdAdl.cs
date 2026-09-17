using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RgbControl.Core.Gpu;

/// <summary>AMD driver's GPU-local I2C interface. This never uses the motherboard SMBus.</summary>
public sealed class AmdAdl : IDisposable, IGpuI2c
{
    private readonly nint _library;
    private nint _context;
    private readonly Allocate _allocate = Marshal.AllocHGlobal;
    private readonly Destroy _destroy;
    private readonly AdapterInfoGet _adapters;
    private readonly Transfer _transfer;

    public AmdAdl()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AMD ADL requires Windows.");
        _library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"));
        try
        {
            _destroy = Get<Destroy>("ADL2_Main_Control_Destroy");
            _adapters = Get<AdapterInfoGet>("ADL2_Adapter_AdapterInfoX4_Get");
            _transfer = Get<Transfer>("ADL2_Display_WriteAndReadI2C");
            Check(Get<Create>("ADL2_Main_Control_Create")(_allocate, 1, out _context), "initialize AMD ADL");
        }
        catch { NativeLibrary.Free(_library); throw; }
    }

    private T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    public IReadOnlyList<GpuAdapter> GetAdapters()
    {
        nint data = 0;
        try
        {
            Check(_adapters(_context, -1, out var count, out data), "enumerate AMD adapters");
            if (count is < 0 or > 128 || (count > 0 && data == 0)) throw new IOException("Invalid AMD adapter list.");
            var result = new List<GpuAdapter>();
            var seen = new HashSet<(int, int, int)>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<AdapterInfo>(data + i * Marshal.SizeOf<AdapterInfo>());
                if (info.Exist == 0 || !seen.Add((info.Bus, info.Device, info.Function))) continue;
                var match = Regex.Match(info.PnpString ?? "", @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})&SUBSYS_([0-9A-F]{4})([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                int Id(int group) => Convert.ToInt32(match.Groups[group].Value, 16);
                result.Add(new GpuAdapter(info.Index, info.Name, Id(1), Id(2), Id(4), Id(3)));
            }
            return result;
        }
        finally { if (data != 0) Marshal.FreeHGlobal(data); }
    }

    public byte[] Read(GpuAdapter adapter, byte address, byte register, int count)
    {
        var bytes = new byte[count];
        Exchange(adapter, address, register, bytes, read: true);
        return bytes;
    }

    public void Write(GpuAdapter adapter, byte address, byte register, params byte[] values)
    {
        byte[] bytes = [register, .. values];
        Exchange(adapter, address, 0, bytes, read: false);
    }

    private void Exchange(GpuAdapter adapter, byte address, byte offset, byte[] bytes, bool read)
    {
        // A closed allowlist ensures this helper cannot access unrelated GPU I2C peripherals.
        if (!GpuLightingController.IsSupported(adapter) || address != (adapter.SubVendor == 0x1DA2 ? 0x28 : 0x22))
            throw new InvalidOperationException("GPU is outside the supported lighting-controller allowlist.");
        if (bytes.Length is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(bytes));
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var packet = new I2cPacket
            {
                Size = Marshal.SizeOf<I2cPacket>(), Line = 1, Address = address << 1, Offset = offset,
                Action = read ? 1 : 2, Speed = 100, DataSize = bytes.Length, Data = pin.AddrOfPinnedObject(),
            };
            Check(_transfer(_context, adapter.Index, ref packet), $"{(read ? "read" : "write")} GPU lighting register 0x{(read ? offset : bytes[0]):X2}");
        }
        finally { pin.Free(); }
    }

    private static void Check(int status, string action)
    {
        if (status != 0) throw new IOException($"Could not {action} (AMD ADL status {status}).");
    }

    public void Dispose()
    {
        if (_context == 0) return;
        _destroy(_context);
        _context = 0;
        NativeLibrary.Free(_library);
        GC.KeepAlive(_allocate);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint Allocate(int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(Allocate allocate, int enumerateConnected, out nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Destroy(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AdapterInfoGet(nint context, int index, out int count, out nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Transfer(nint context, int index, ref I2cPacket packet);

    [StructLayout(LayoutKind.Sequential)]
    private struct I2cPacket
    {
        public int Size, Line, Address, Offset, Action, Speed, DataSize;
        public nint Data;
    }

    // AMD ADL SDK AdapterInfoX2, ANSI ADL_MAX_PATH = 256.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdapterInfo
    {
        public int Size, Index;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Udid;
        public int Bus, Device, Function, Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Display;
        public int Present, Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string PnpString;
        public int OsDisplayIndex, InfoMask, InfoValue;
    }
}

public sealed record GpuAdapter(int Index, string Name, int Vendor, int Device, int SubVendor, int SubDevice);

public interface IGpuI2c
{
    byte[] Read(GpuAdapter adapter, byte address, byte register, int count);
    void Write(GpuAdapter adapter, byte address, byte register, params byte[] values);
}
