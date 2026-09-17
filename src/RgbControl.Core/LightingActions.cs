using RgbControl.Core.Aura;
using RgbControl.Core.Ene;
using RgbControl.Core.Smbus;

namespace RgbControl.Core;

/// <summary>High-level lighting operations shared by the service, app and CLI.</summary>
public static class LightingActions
{
    /// <summary>Shows the configured effect. Not saved to the controller, so a power loss falls back to its saved state.</summary>
    public static void ApplyOn(AuraUsbController aura, LightingConfig config)
    {
        if (config.AllLightingOff)
        {
            ApplyOff(aura);
            return;
        }

        ApplyConfiguredMotherboard(aura, config);
    }

    private static void ApplyConfiguredMotherboard(AuraUsbController aura, LightingConfig config)
    {
        aura.Initialize();
        var header = 0;
        foreach (var channel in aura.Channels)
        {
            var setting = ResolveChannel(config, channel.Type == AuraChannelType.Addressable ? ++header : 0);
            if (setting is { } effect)
                aura.SetEffect(channel, effect.Mode, effect.Color);
        }
    }

    /// <summary>Header 0 is onboard; a null result leaves an unmanaged header alone.</summary>
    public static (AuraMode Mode, Rgb Color)? ResolveChannel(LightingConfig config, int header)
    {
        if (config.AllLightingOff)
            return (AuraMode.Off, Rgb.Black);

        var custom = config.ArgbHeaders.FirstOrDefault(h => h.Header == header && h.UseCustomSettings);
        if (header > 0 && custom is not null)
            return custom.Enabled ? (custom.Mode, custom.EffectiveColor) : (AuraMode.Off, Rgb.Black);

        var board = config.Motherboard;
        if (!board.Enabled)
            return (AuraMode.Off, Rgb.Black);
        if (header > 0 && !board.IncludeAddressableHeaders)
            return null;
        return (board.Mode, board.EffectiveColor);
    }

    public static void ApplyOff(AuraUsbController aura) => Apply(aura, AuraMode.Off, Rgb.Black, includeAddressable: true);

    public static void Apply(AuraUsbController aura, AuraMode mode, Rgb color, bool includeAddressable)
    {
        aura.Initialize();
        foreach (var channel in aura.Channels)
        {
            if (channel.Type == AuraChannelType.Addressable && !includeAddressable)
            {
                continue;
            }

            aura.SetEffect(channel, mode, color);
        }
    }

    /// <summary>Applies the RAM config to every verified ENE DRAM controller. Returns how many sticks were set.</summary>
    public static int ApplyRamOn(SmbusPiix4 bus, LightingConfig config, Action<string>? log = null)
    {
        var ram = config.Ram;
        return config.AllLightingOff || !ram.Enabled
            ? ApplyRamOff(bus, log)
            : ApplyRam(bus, ram.Mode, ram.EffectiveColor, ram.Speed, log);
    }

    public static int ApplyRamOff(SmbusPiix4 bus, Action<string>? log = null) =>
        ApplyRam(bus, AuraMode.Off, Rgb.Black, EffectSpeed.Normal, log);

    public static int ApplyRam(SmbusPiix4 bus, AuraMode mode, Rgb color, EffectSpeed speed = EffectSpeed.Normal, Action<string>? log = null)
    {
        var sticks = EneDramController.Discover(bus, log);
        foreach (var stick in sticks)
        {
            stick.SetEffect(mode, color, speed);
        }
        return sticks.Count;
    }

    /// <summary>
    /// Writes to the controller's flash: the configured motherboard effect while the PC is on, and "off" while it is off.
    /// Only needed once (or after changing colors); the hardware then keeps the board dark when powered down
    /// even if the service never gets a chance to run. Ignores the master off switch, since this is the startup look.
    /// </summary>
    public static void SaveToHardware(AuraUsbController aura, LightingConfig config)
    {
        // Store the configured startup look even while the runtime master switch is off.
        var startup = new LightingConfig { Motherboard = config.Motherboard, ArgbHeaders = config.ArgbHeaders };
        ApplyConfiguredMotherboard(aura, startup);

        foreach (var channel in aura.Channels.Where(c => c.Type == AuraChannelType.Onboard))
        {
            aura.SetEffect(channel, AuraMode.Off, Rgb.Black, shutdownEffect: true);
        }

        aura.Commit();

        if (config.AllLightingOff)
        {
            ApplyOff(aura);
        }
    }
}
