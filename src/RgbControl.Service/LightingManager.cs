using RgbControl.Core;
using RgbControl.Core.Aura;
using RgbControl.Core.Smbus;
using RgbControl.Core.Gigabyte;
using RgbControl.Core.Gpu;

namespace RgbControl.Service;

/// <summary>Serializes all access to the lighting hardware and retries while devices are still coming up.</summary>
public sealed class LightingManager(ILogger<LightingManager> logger)
{
    private readonly Lock _gate = new();
    private LightingConfig? _lastGoodConfig;

    private volatile bool _isSuspended;
    public bool IsSuspended => _isSuspended;

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
        var gpusDone = new HashSet<GpuKind>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (IsSuspended) return;

                var config = LoadConfig();
                motherboardDone = motherboardDone || TryMotherboard(config, false);
                ramDone = ramDone || TryRam(bus => LightingActions.ApplyRamOn(bus, config, Log), "apply RAM lighting");
                foreach (var gpu in config.Gpus.Where(gpu => gpu.Managed && !gpusDone.Contains(gpu.Kind)))
                {
                    if (TryGpu(gpu, config.AllLightingOff)) gpusDone.Add(gpu.Kind);
                }

                if (motherboardDone && ramDone && config.Gpus.Where(g => g.Managed).All(g => gpusDone.Contains(g.Kind)))
                {
                    logger.LogInformation("Lighting applied");
                    return;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                logger.LogWarning("Gave up after {Timeout} (motherboard: {Motherboard}, RAM: {Ram}, GPU models applied: {GpuCount}); see preceding device errors",
                    timeout, motherboardDone ? "ok" : "failed", ramDone ? "ok" : "failed", gpusDone.Count);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>Turns lighting off immediately. Called from shutdown/suspend, which only allow a few seconds.</summary>
    public void TurnOff(string reason)
    {
        var config = LoadConfig();
        var motherboard = TryMotherboard(config, true);
        var ram = TryRam(bus => LightingActions.ApplyRamOff(bus, Log), "turn RAM lighting off");
        foreach (var gpu in config.Gpus.Where(g => g.Managed)) TryGpu(gpu, true);
        logger.LogInformation("Lighting off ({Reason}): motherboard {Motherboard}, RAM {Ram}",
            reason, motherboard ? "ok" : "failed", ram ? "ok" : "failed");
    }

    public void MarkSuspended(bool suspended)
    {
        lock (_gate) _isSuspended = suspended;
    }

    private void Log(string message) => logger.LogInformation("{Message}", message);

    private bool TryMotherboard(LightingConfig config, bool forceOff)
    {
        if (config.Motherboard.Controller == MotherboardKind.AsusAura)
            return TryAura(aura => { if (forceOff) LightingActions.ApplyOff(aura); else LightingActions.ApplyOn(aura, config); }, "apply motherboard lighting");
        lock (_gate)
        {
            try
            {
                using var board = GigabyteController.TryOpen();
                if (board is null) return false;
                board.Apply(config, forceOff);
                return true;
            }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to apply Gigabyte lighting"); return false; }
        }
    }

    private bool TryGpu(GpuLighting config, bool forceOff)
    {
        lock (_gate)
        {
            try { return GpuLightingController.Apply(config, forceOff, Log) > 0; }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to apply {Gpu} lighting", config.Kind); return false; }
        }
    }

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
