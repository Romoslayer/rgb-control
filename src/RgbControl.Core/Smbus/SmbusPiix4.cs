using static RgbControl.Core.Smbus.PawnIONative;

namespace RgbControl.Core.Smbus;

/// <summary>
/// AMD FCH (PIIX4-compatible) SMBus access through the PawnIO SmbusPIIX4 module.
/// Transfer semantics follow Linux i2c-smbus: read/write flag, command byte, protocol.
/// </summary>
public sealed class SmbusPiix4 : IDisposable
{
    public const ushort AmdVendorId = 0x1022;
    public const ushort AmdFchSmbusDeviceId = 0x790B;

    /// <summary>System-wide lock shared with OpenRGB, HWiNFO, LibreHardwareMonitor, etc.</summary>
    private const string GlobalSmbusMutexName = @"Global\Access_SMBUS.HTP.Method";

    private const ulong Read = 1;
    private const ulong Write = 0;

    private const ulong ProtocolByte = 1;
    private const ulong ProtocolByteData = 2;
    private const ulong ProtocolWordData = 3;
    private const ulong ProtocolBlockData = 5;

    private readonly IntPtr _handle;
    private readonly Mutex _mutex;
    private readonly long _previousPort;

    public uint PawnIOVersion { get; }
    public ushort PciVendorId { get; }
    public ushort PciDeviceId { get; }
    public ulong IoBase { get; }

    public bool IsAmdFch => PciVendorId == AmdVendorId && PciDeviceId == AmdFchSmbusDeviceId;

    private SmbusPiix4(IntPtr handle, uint version)
    {
        _handle = handle;
        PawnIOVersion = version;
        _mutex = new Mutex(false, GlobalSmbusMutexName);

        var identity = Execute("ioctl_identity", [0], 3);
        IoBase = identity[1];
        PciVendorId = (ushort)(identity[2] & 0xFFFF);
        PciDeviceId = (ushort)((identity[2] >> 16) & 0xFFFF);

        // Port 0 is the primary bus where the DIMMs live.
        using (Lock())
        {
            _previousPort = (long)Execute("ioctl_piix4_port_sel", [0], 1)[0];
        }
    }

    /// <summary>Opens PawnIO and loads the SmbusPIIX4 module. Requires administrator/SYSTEM.</summary>
    public static SmbusPiix4 Open(string? modulePath = null)
    {
        modulePath ??= Path.Combine(AppContext.BaseDirectory, "modules", "SmbusPIIX4.bin");
        if (!File.Exists(modulePath))
        {
            throw new FileNotFoundException($"PawnIO module not found: {modulePath}");
        }

        EnsureResolver();

        uint version;
        try
        {
            Check(pawnio_version(out version), "PawnIO is not installed or not running");
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException("PawnIOLib.dll not found. Is PawnIO installed?");
        }

        var status = pawnio_open(out var handle);
        if (status == E_ACCESSDENIED)
        {
            throw new UnauthorizedAccessException("PawnIO requires administrator rights. Run from an admin terminal.");
        }
        Check(status, "Could not open PawnIO");

        var blob = File.ReadAllBytes(modulePath);
        status = pawnio_load(handle, blob, (nuint)blob.Length);
        if (status != 0)
        {
            pawnio_close(handle);
            Check(status, "PawnIO rejected the SmbusPIIX4 module (unsupported chipset?)");
        }

        try
        {
            return new SmbusPiix4(handle, version);
        }
        catch
        {
            pawnio_close(handle);
            throw;
        }
    }

    /// <summary>Holds the global SMBus mutex. Wrap a whole register read/write sequence in one lock.</summary>
    public IDisposable Lock()
    {
        try
        {
            _mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed; we now own it.
        }

        return new Releaser(_mutex);
    }

    /// <summary>SMBus "receive byte": addresses the device and reads, sending no command. Returns -1 if nothing answers.</summary>
    public int ReadByte(byte address) => TryXfer(address, Read, 0, ProtocolByte, null, out var value) ? (int)(value & 0xFF) : -1;

    public int ReadByteData(byte address, byte command) =>
        TryXfer(address, Read, command, ProtocolByteData, null, out var value) ? (int)(value & 0xFF) : -1;

    public bool WriteByteData(byte address, byte command, byte value) =>
        TryXfer(address, Write, command, ProtocolByteData, [value], out _);

    public bool WriteWordData(byte address, byte command, ushort value) =>
        TryXfer(address, Write, command, ProtocolWordData, [value], out _);

    public bool WriteBlockData(byte address, byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "SMBus blocks are 1-32 bytes.");
        }

        // Byte-packed little-endian: [length, data...] across 5 cells.
        var packed = new byte[40];
        packed[0] = (byte)data.Length;
        data.CopyTo(packed.AsSpan(1));

        var cells = new ulong[5];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = BitConverter.ToUInt64(packed, i * 8);
        }

        return TryXfer(address, Write, command, ProtocolBlockData, cells, out _);
    }

    private bool TryXfer(byte address, ulong readWrite, byte command, ulong protocol, ulong[]? data, out ulong result)
    {
        if (address > 0x7F)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        var input = new ulong[4 + (data?.Length ?? 0)];
        input[0] = address;
        input[1] = readWrite;
        input[2] = command;
        input[3] = protocol;
        data?.CopyTo(input, 4);

        var output = new ulong[5];
        var status = pawnio_execute(_handle, "ioctl_smbus_xfer", input, (nuint)input.Length, output, (nuint)output.Length, out _);
        result = output[0];
        return status == 0;
    }

    private ulong[] Execute(string ioctl, ulong[] input, int outputCells)
    {
        var output = new ulong[outputCells];
        Check(pawnio_execute(_handle, ioctl, input, (nuint)input.Length, output, (nuint)outputCells, out _), $"{ioctl} failed");
        return output;
    }

    private static void Check(int hresult, string message)
    {
        if (hresult != 0)
        {
            throw new InvalidOperationException($"{message} (0x{hresult:X8})");
        }
    }

    public void Dispose()
    {
        if (_previousPort is > 0 and <= 4)
        {
            using (Lock())
            {
                pawnio_execute(_handle, "ioctl_piix4_port_sel", [(ulong)_previousPort], 1, new ulong[1], 1, out _);
            }
        }

        pawnio_close(_handle);
        _mutex.Dispose();
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose() => mutex.ReleaseMutex();
    }
}
