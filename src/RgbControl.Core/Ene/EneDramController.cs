using RgbControl.Core.Aura;
using RgbControl.Core.Smbus;

namespace RgbControl.Core.Ene;

/// <summary>
/// Writes lighting to one verified ENE DRAM controller. Construction re-verifies the address, signature and firmware
/// every time, so a controller is never written unless it identified itself in the same session.
/// Deliberately has no "save to device": OpenRGB disables that for ENE RAM by default because of flash-write risk.
/// </summary>
public sealed class EneDramController
{
    private const ushort RegColorsEffectV1 = 0x8010; // 15 bytes (5 LEDs)
    private const ushort RegColorsEffectV2 = 0x8160; // 30 bytes (10 LEDs)
    private const ushort RegDirect = 0x8020;
    private const ushort RegMode = 0x8021;
    private const ushort RegSpeed = 0x8022;
    private const ushort RegDirection = 0x8023;
    private const ushort RegApply = 0x80A0;
    private const byte ApplyValue = 0x01;

    /// <summary>Firmware versions we know the register layout for (from OpenRGB). Anything else is refused.</summary>
    private static readonly Dictionary<string, ushort> EffectRegisterByVersion = new()
    {
        ["LED-0116"] = RegColorsEffectV1,
        ["DIMM_LED-0102"] = RegColorsEffectV1,
        ["AUMA0-E8K4-0101"] = RegColorsEffectV1,
        ["AUDA0-E6K5-0101"] = RegColorsEffectV2,
        ["AUMA0-E6K5-0104"] = RegColorsEffectV2,
        ["AUMA0-E6K5-0105"] = RegColorsEffectV2,
        ["AUMA0-E6K5-0106"] = RegColorsEffectV2,
        ["AUMA0-E6K5-0107"] = RegColorsEffectV2,
        ["AUMA0-E6K5-0008"] = RegColorsEffectV2,
        ["AUMA0-E6K5-1107"] = RegColorsEffectV2,
        ["AUMA0-E6K5-1110"] = RegColorsEffectV2,
        ["AUMA0-E6K5-1111"] = RegColorsEffectV2,
        ["AUMA0-E6K5-1113"] = RegColorsEffectV2,
        ["AUMA0-E6K5-1114"] = RegColorsEffectV2,
    };

    private readonly SmbusPiix4 _bus;
    private readonly ushort _effectRegister;

    public EneDramInfo Info { get; }
    public int LedCount { get; }

    private EneDramController(SmbusPiix4 bus, EneDramInfo info, ushort effectRegister)
    {
        _bus = bus;
        Info = info;
        _effectRegister = effectRegister;
        var capacity = effectRegister == RegColorsEffectV2 ? 10 : 5;
        LedCount = Math.Clamp(info.LedCount, 1, capacity);
    }

    /// <summary>Finds every verified ENE DRAM controller in 0x70-0x76. Never touches 0x77 or anything outside that range.</summary>
    public static List<EneDramController> Discover(SmbusPiix4 bus, Action<string>? log = null)
    {
        var controllers = new List<EneDramController>();
        if (!bus.IsAmdFch)
        {
            log?.Invoke($"Unexpected SMBus controller {bus.PciVendorId:X4}:{bus.PciDeviceId:X4}; not scanning.");
            return controllers;
        }

        foreach (var address in EneDram.FindResponding(bus))
        {
            if (address == EneDram.DefaultAddress)
            {
                log?.Invoke("Device at 0x77 (un-remapped DRAM?) ignored.");
                continue;
            }

            if (!EneDram.HasEneSignature(bus, address))
            {
                log?.Invoke($"0x{address:X2} is not an ENE controller; ignored.");
                continue;
            }

            var info = EneDram.ReadInfo(bus, address, configBytes: 4);
            if (info.IsMicron)
            {
                log?.Invoke($"0x{address:X2} is a Micron module; ignored.");
                continue;
            }

            if (!EffectRegisterByVersion.TryGetValue(info.Version, out var register))
            {
                log?.Invoke($"0x{address:X2} has unknown firmware '{info.Version}'; ignored.");
                continue;
            }

            controllers.Add(new EneDramController(bus, info, register));
        }

        return controllers;
    }

    /// <summary>Sets a hardware effect with one color on every LED (not saved; lost at power-off).</summary>
    public void SetEffect(AuraMode mode, Rgb color, EffectSpeed speed = EffectSpeed.Normal)
    {
        if (mode is AuraMode.Direct or > AuraMode.RandomFlicker)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), $"{mode} is not supported on RAM.");
        }

        using (_bus.Lock())
        {
            Write(RegMode, (byte)mode);
            Write(RegSpeed, ToEneSpeed(speed));
            Write(RegDirection, 0x00);
            Write(RegApply, ApplyValue);

            Write(RegDirect, 0x00);
            Write(RegApply, ApplyValue);

            // ENE color order is R, B, G.
            for (var led = 0; led < LedCount; led++)
            {
                WriteBlock((ushort)(_effectRegister + 3 * led), [color.R, color.B, color.G]);
            }
            Write(RegApply, ApplyValue);
        }
    }

    /// <summary>ENE speed register: 0x00 is fastest, 0x04 is slowest.</summary>
    private static byte ToEneSpeed(EffectSpeed speed) => speed switch
    {
        EffectSpeed.Slowest => 0x04,
        EffectSpeed.Slow => 0x03,
        EffectSpeed.Fast => 0x01,
        EffectSpeed.Fastest => 0x00,
        _ => 0x02,
    };

    private void Write(ushort register, byte value)
    {
        SelectRegister(register);
        if (!_bus.WriteByteData(Info.Address, 0x01, value))
        {
            throw new IOException($"ENE 0x{Info.Address:X2}: write to 0x{register:X4} failed");
        }
    }

    private void WriteBlock(ushort register, byte[] data)
    {
        SelectRegister(register);
        if (_bus.WriteBlockData(Info.Address, 0x03, data))
        {
            return;
        }

        // Fall back to single bytes; the ENE register pointer auto-increments.
        SelectRegister(register);
        foreach (var b in data)
        {
            if (!_bus.WriteByteData(Info.Address, 0x01, b))
            {
                throw new IOException($"ENE 0x{Info.Address:X2}: write to 0x{register:X4} failed");
            }
        }
    }

    private void SelectRegister(ushort register)
    {
        if (!_bus.WriteWordData(Info.Address, 0x00, EneDram.SwapRegister(register)))
        {
            throw new IOException($"ENE 0x{Info.Address:X2}: failed to select register 0x{register:X4}");
        }
    }
}
