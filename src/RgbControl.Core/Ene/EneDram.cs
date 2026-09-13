using System.Text;
using RgbControl.Core.Smbus;

namespace RgbControl.Core.Ene;

/// <summary>
/// ENE SMBus RGB controllers on DRAM (G.Skill Trident Z5 RGB and others).
/// Protocol reference: OpenRGB's ENESMBusController.
/// </summary>
public static class EneDram
{
    /// <summary>
    /// The only addresses this app will ever talk to. OpenRGB's list also includes 0x4F, 0x66, 0x67 and 0x39-0x3D;
    /// on DDR5, 0x48-0x4F are PMICs (DIMM power) and 0x50-0x57 are SPD, so we deliberately stay in 0x70-0x77.
    /// </summary>
    public static readonly IReadOnlyList<byte> AllowedAddresses = [0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77];

    /// <summary>Power-on address shared by all un-remapped ENE DRAM controllers.</summary>
    public const byte DefaultAddress = 0x77;

    private const ushort RegDeviceName = 0x1000;
    private const ushort RegMicronCheck = 0x1030;
    private const ushort RegConfigTable = 0x1C00;

    /// <summary>Stage 1: which allowed addresses acknowledge a plain receive-byte (no command sent).</summary>
    public static List<byte> FindResponding(SmbusPiix4 bus)
    {
        var found = new List<byte>();
        foreach (var address in AllowedAddresses)
        {
            using (bus.Lock())
            {
                if (bus.ReadByte(address) >= 0)
                {
                    found.Add(address);
                }
            }
            Thread.Sleep(1);
        }
        return found;
    }

    /// <summary>Stage 2a: ENE controllers echo 0x00-0x0F from registers 0xA0-0xAF.</summary>
    public static bool HasEneSignature(SmbusPiix4 bus, byte address)
    {
        CheckAllowed(address);
        using (bus.Lock())
        {
            for (var i = 0; i < 16; i++)
            {
                if (bus.ReadByteData(address, (byte)(0xA0 + i)) != i)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Stage 2b: read identification (sets the ENE register pointer, then reads). Only call on a verified ENE address.</summary>
    /// <param name="configBytes">How much of the 64-byte config table to read; the LED count only needs the first 4.</param>
    public static EneDramInfo ReadInfo(SmbusPiix4 bus, byte address, int configBytes = 64)
    {
        CheckAllowed(address);
        if (address == DefaultAddress)
        {
            throw new InvalidOperationException("0x77 may be shared by several un-remapped DIMMs; not reading registers there.");
        }

        using (bus.Lock())
        {
            var name = ReadRegisters(bus, address, RegDeviceName, 16);
            var micron = ReadRegisters(bus, address, RegMicronCheck, 16);
            var config = new byte[64];
            ReadRegisters(bus, address, RegConfigTable, Math.Clamp(configBytes, 4, 64)).CopyTo(config, 0);
            return new EneDramInfo(address, AsciiZ(name), AsciiZ(micron) == "Micron", config);
        }
    }

    private static byte[] ReadRegisters(SmbusPiix4 bus, byte address, ushort start, int count)
    {
        var values = new byte[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = RegisterRead(bus, address, (ushort)(start + i));
        }
        return values;
    }

    /// <summary>ENE registers are 16-bit: write the byte-swapped register to command 0x00, then read command 0x81.</summary>
    internal static byte RegisterRead(SmbusPiix4 bus, byte address, ushort register)
    {
        if (!bus.WriteWordData(address, 0x00, SwapRegister(register)))
        {
            throw new IOException($"ENE 0x{address:X2}: failed to select register 0x{register:X4}");
        }

        var value = bus.ReadByteData(address, 0x81);
        if (value < 0)
        {
            throw new IOException($"ENE 0x{address:X2}: failed to read register 0x{register:X4}");
        }
        return (byte)value;
    }

    internal static ushort SwapRegister(ushort register) => (ushort)((register << 8) & 0xFF00 | (register >> 8) & 0x00FF);

    private static void CheckAllowed(byte address)
    {
        if (!AllowedAddresses.Contains(address))
        {
            throw new ArgumentOutOfRangeException(nameof(address), $"0x{address:X2} is outside the allowed ENE DRAM range 0x70-0x77.");
        }
    }

    private static string AsciiZ(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }
}

public sealed record EneDramInfo(byte Address, string Version, bool IsMicron, byte[] ConfigTable)
{
    /// <summary>Firmware versions that store the LED count at config offset 3 instead of 2 (per OpenRGB).</summary>
    private static readonly HashSet<string> LedCountAtOffset3 =
        ["AUMA0-E6K5-0107", "AUMA0-E6K5-1110", "AUMA0-E6K5-1111", "AUMA0-E6K5-1107", "AUMA0-E6K5-0008", "AUMA0-E6K5-1113", "AUMA0-E6K5-1114"];

    public int LedCount => ConfigTable[LedCountAtOffset3.Contains(Version) ? 3 : 2];
}
