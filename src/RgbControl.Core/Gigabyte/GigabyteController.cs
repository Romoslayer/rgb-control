using System.Buffers.Binary;
using HidSharp;
using Microsoft.Win32;
using RgbControl.Core.Aura;

namespace RgbControl.Core.Gigabyte;

/// <summary>
/// B650 AORUS ELITE AX IT5702 layout 31. Protocol adapted from OpenRGB's
/// GigabyteRGBFusion2USBController (jackun, megadjc), GPL-2.0-or-later.
/// No calibration or flash writes. Unknown boards/controllers are refused.
/// </summary>
public sealed class GigabyteController : IDisposable
{
    private readonly HidStream _stream;
    public string Firmware { get; }

    public static string BoardProduct
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "";
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return key?.GetValue("BaseBoardProduct") as string ?? "";
        }
    }

    private GigabyteController(HidStream stream)
    {
        _stream = stream;
        _stream.ReadTimeout = _stream.WriteTimeout = 1000;
        Send(0x60, 0);
        var report = new byte[64];
        report[0] = 0xCC;
        _stream.GetFeature(report);
        if (report[0] != 0xCC || report[2] != 0 || report.AsSpan(4, 4).SequenceEqual(new byte[4]))
            throw new IOException("Unexpected Gigabyte lighting controller identity.");
        Firmware = $"{report[4]}.{report[5]}.{report[6]}.{report[7]}";
    }

    public static GigabyteController? TryOpen()
    {
        if (!BoardProduct.Trim().Equals("B650 AORUS ELITE AX", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var device in DeviceList.Local.GetHidDevices(0x048D, 0x5702))
        {
            if (!device.GetReportDescriptor().DeviceItems.Any(item => item.Usages.GetAllValues().Contains(0xFF8900CCu))) continue;
            if (!device.TryOpen(out HidStream stream)) continue;
            try { return new GigabyteController(stream); }
            catch { stream.Dispose(); throw; }
        }
        return null;
    }

    public void Apply(LightingConfig config, bool forceOff = false)
    {
        var commands = new List<(int Zone, byte[] Packet)>();
        // Validate all effects before changing any lighting.
        for (var zone = 1; zone <= 6; zone++)
        {
            var effect = forceOff ? (AuraMode.Off, Rgb.Black) : LightingActions.ResolveChannel(config, zone >= 5 ? zone - 4 : 0);
            if (effect is { } value) commands.Add((zone, BuildEffectPacket(zone, value.Item1, value.Item2)));
        }
        Send(0x31, 0); // disable hardware audio effects
        // Select built-in effects for the managed ARGB headers only. IT5702 has two.
        // Other RGB software should not drive this controller concurrently.
        if (commands.Any(c => c.Zone >= 5)) Send(0x32, 0);
        uint mask = 0;
        foreach (var command in commands)
        {
            _stream.SetFeature(command.Packet);
            mask |= 1u << command.Zone;
        }
        var apply = Packet(0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(apply.AsSpan(2), mask);
        _stream.SetFeature(apply);
    }

    public static byte[] BuildEffectPacket(int zone, AuraMode mode, Rgb color)
    {
        if (zone is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(zone));
        byte effect = mode switch
        {
            AuraMode.Off => 1, AuraMode.Static => 1, AuraMode.Breathing => 2,
            AuraMode.Flashing => 3, AuraMode.SpectrumCycle => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), "This Gigabyte board supports Static, Breathing, Flashing and Spectrum cycle."),
        };
        var packet = Packet((byte)(0x20 + zone));
        if (mode == AuraMode.Off) color = Rgb.Black;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), 1u << zone);
        packet[11] = effect;
        packet[12] = mode == AuraMode.Off ? (byte)0 : (byte)255;
        packet[14] = color.B; packet[15] = color.G; packet[16] = color.R;
        void Period(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset), value);
        switch (mode)
        {
            case AuraMode.Breathing: Period(22, 900); Period(24, 900); Period(26, 200); break;
            case AuraMode.Flashing: Period(22, 100); Period(24, 100); Period(26, 1700); break;
            case AuraMode.SpectrumCycle: Period(22, 800); Period(24, 600); packet[30] = 7; break;
        }
        return packet;
    }

    private static byte[] Packet(byte command)
    {
        var packet = new byte[64]; packet[0] = 0xCC; packet[1] = command; return packet;
    }
    private void Send(byte command, byte value)
    {
        var packet = Packet(command); packet[2] = value; _stream.SetFeature(packet);
    }
    public void Dispose() => _stream.Dispose();
}
