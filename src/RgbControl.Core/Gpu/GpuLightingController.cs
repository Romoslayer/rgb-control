using RgbControl.Core.Aura;

namespace RgbControl.Core.Gpu;

/// <summary>
/// Protocols adapted from OpenRGB SapphireNitroGlowV3 (K900) and PowerColorRedDevilV2 (Nexrem),
/// GPL-2.0-or-later. See docs/hardware-support.md for reference links and validation status.
/// </summary>
public static class GpuLightingController
{
    public static bool IsSupported(GpuAdapter gpu) => gpu.Vendor == 0x1002 && gpu.Device == 0x7550 &&
        ((gpu.SubVendor == 0x1DA2 && gpu.SubDevice == 0xE489) ||
         (gpu.SubVendor == 0x148C && gpu.SubDevice == 0x2435));

    public static bool Matches(GpuAdapter gpu, GpuKind kind) => IsSupported(gpu) &&
        (kind == GpuKind.SapphireNitro9070Xt ? gpu.SubVendor == 0x1DA2 : gpu.SubVendor == 0x148C);

    public static int Apply(GpuLighting settings, bool forceOff, Action<string>? log = null)
    {
        if (!settings.Managed) return 0;
        using var adl = new AmdAdl();
        var matched = adl.GetAdapters().Where(gpu => Matches(gpu, settings.Kind)).ToList();
        foreach (var gpu in matched)
        {
            ApplyToAdapter(adl, gpu, settings, forceOff);
            log?.Invoke($"Applied GPU lighting: {gpu.Name} ({settings.Connection})");
        }
        return matched.Count;
    }

    public static void ApplyToAdapter(IGpuI2c adl, GpuAdapter gpu, GpuLighting settings, bool forceOff)
    {
        if (!settings.Managed) return;
        if (!Matches(gpu, settings.Kind)) throw new InvalidOperationException("GPU identity does not match the configured model.");
        if (settings.Kind == GpuKind.SapphireNitro9070Xt) ApplySapphire(adl, gpu, settings, forceOff);
        else ApplyPowerColor(adl, gpu, settings, forceOff);
    }

    private static void ApplySapphire(IGpuI2c adl, GpuAdapter gpu, GpuLighting config, bool forceOff)
    {
        // Verify a valid mode can be read before the first write. PCI IDs select the protocol.
        var currentMode = adl.Read(gpu, 0x28, 0x10, 1)[0];
        if (currentMode > 7) throw new IOException($"Unexpected Sapphire lighting mode {currentMode}.");
        void Write(byte register, byte value)
        {
            adl.Write(gpu, 0x28, register, value);
            Thread.Sleep(50);
        }
        void Verify(byte register, byte expected)
        {
            var actual = adl.Read(gpu, 0x28, register, 1)[0];
            if (actual != expected)
                throw new IOException($"Sapphire register 0x{register:X2}: requested {expected}, read back {actual}.");
        }
        var mode = GetMode(config, forceOff);
        if (config.Connection == GpuConnection.ArgbCable && mode != AuraMode.Off)
        {
            Write(0x0F, 1);
            Verify(0x0F, 1);
            return;
        }
        var color = mode == AuraMode.Off ? Rgb.Black : Rgb.Parse(config.Color).Scale(config.Brightness);
        byte hardwareMode = mode switch
        {
            AuraMode.Off => 7, AuraMode.Static => 6,
            AuraMode.Rainbow => 0, AuraMode.SpectrumCycle => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(config.Mode)),
        };
        // Match OpenRGB's mode update followed by LED update. Color writes must follow
        // the mode selection so the controller applies them to the selected effect.
        Write(0x0F, 0);
        Write(0x10, hardwareMode);
        Write(0x1A, color.R);
        Write(0x1B, color.G);
        Write(0x1C, color.B);
        Verify(0x0F, 0);
        Verify(0x10, hardwareMode);
        Verify(0x1A, color.R);
        Verify(0x1B, color.G);
        Verify(0x1C, color.B);
    }

    private static void ApplyPowerColor(IGpuI2c adl, GpuAdapter gpu, GpuLighting config, bool forceOff)
    {
        var signature = adl.Read(gpu, 0x22, 0x82, 3);
        if (signature[0] != 1 || (signature[1] != 5 && signature[1] != 0x32) || signature[2] != 0)
            throw new IOException("PowerColor lighting controller signature did not match.");
        void Write(byte register, params byte[] bytes)
        {
            adl.Write(gpu, 0x22, register, bytes);
            Thread.Sleep(50);
        }
        var mode = GetMode(config, forceOff);
        if (config.Connection == GpuConnection.ArgbCable && mode != AuraMode.Off)
        {
            Write(0x04, 1, 1, 1);
            return;
        }
        var color = mode == AuraMode.Off ? Rgb.Black : Rgb.Parse(config.Color);
        Write(0x04, 0, 0, 0);
        Write(0x30, color.R, color.G, color.B);
        Write(0x31, color.R, color.G, color.B);
        Write(0x01, mode switch
        {
            AuraMode.Off => 0, AuraMode.Static => 1,
            AuraMode.Rainbow => 9, AuraMode.SpectrumCycle => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(config.Mode)),
        }, (byte)(Math.Clamp(config.Brightness, 0, 100) * 255 / 100), 0x32);
    }

    public static AuraMode GetMode(GpuLighting config, bool forceOff)
    {
        if (forceOff) return AuraMode.Off;
        if (config.Connection == GpuConnection.ArgbCable) return AuraMode.Static;
        if (!config.Enabled) return AuraMode.Off;
        if (config.Mode is not (AuraMode.Static or AuraMode.Rainbow or AuraMode.SpectrumCycle))
            throw new ArgumentOutOfRangeException(nameof(config.Mode), "GPU supports Static, Rainbow and Spectrum cycle.");
        return config.Mode;
    }
}
