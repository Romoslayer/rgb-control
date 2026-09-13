using RgbControl.Core;
using RgbControl.Core.Aura;
using RgbControl.Core.Smbus;

namespace RgbControl.Service;

/// <summary>Serializes all access to the lighting hardware and retries while devices are still coming up.</summary>
public sealed class LightingManager(ILogger<LightingManager> logger)
{
    private readonly Lock _gate = new();
    private LightingConfig? _lastGoodConfig;

    public bool IsSuspended { get; private set; }

    public LightingConfig LoadConfig()
    {
        try
        {
            return _lastGoodConfig = LightingConfig.Load();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read {Path}; using {Fallback}", LightingConfig.DefaultPath,
                _lastGoodConfig is null ? "defaults" : "last good config");
            return _lastGoodConfig ?? new LightingConfig();
        }
    }

    /// <summary>Applies the configured lighting, retrying each device until it succeeds or the timeout passes.</summary>
    public async Task ApplyOnAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var motherboardDone = false;
        var ramDone = false;

        while (true)
        {
            if (IsSuspended)
            {
                return;
            }

            var config = LoadConfig();
            motherboardDone = motherboardDone || TryAura(aura => LightingActions.ApplyOn(aura, config), "apply motherboard lighting");
            ramDone = ramDone || TryRam(bus => LightingActions.ApplyRamOn(bus, config, Log), "apply RAM lighting");

            if (motherboardDone && ramDone)
            {
                logger.LogInformation("Lighting applied");
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                logger.LogWarning("Gave up after {Timeout} (motherboard: {Motherboard}, RAM: {Ram})",
                    timeout, motherboardDone ? "ok" : "failed", ramDone ? "ok" : "failed");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>Turns lighting off immediately. Called from shutdown/suspend, which only allow a few seconds.</summary>
    public void TurnOff(string reason)
    {
        var motherboard = TryAura(LightingActions.ApplyOff, "turn motherboard lighting off");
        var ram = TryRam(bus => LightingActions.ApplyRamOff(bus, Log), "turn RAM lighting off");
        logger.LogInformation("Lighting off ({Reason}): motherboard {Motherboard}, RAM {Ram}",
            reason, motherboard ? "ok" : "failed", ram ? "ok" : "failed");
    }

    public void MarkSuspended(bool suspended) => IsSuspended = suspended;

    private void Log(string message) => logger.LogInformation("{Message}", message);

    private bool TryAura(Action<AuraUsbController> action, string what)
    {
        lock (_gate)
        {
            try
            {
                using var aura = AuraUsbController.TryOpen();
                if (aura is null)
                {
                    logger.LogDebug("Aura controller not found while trying to {What}", what);
                    return false;
                }

                action(aura);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to {What}", what);
                return false;
            }
        }
    }

    private bool TryRam(Func<SmbusPiix4, int> action, string what)
    {
        lock (_gate)
        {
            try
            {
                using var bus = SmbusPiix4.Open();
                var sticks = action(bus);
                if (sticks == 0)
                {
                    logger.LogDebug("No RAM lighting controllers found while trying to {What}", what);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to {What}", what);
                return false;
            }
        }
    }
}
