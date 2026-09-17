using RgbControl.Core;

namespace RgbControl.Service;

/// <summary>Applies lighting at startup and re-applies whenever the config file changes.</summary>
public sealed class LightingWorker(LightingManager manager, ILogger<LightingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting; config at {Path}", LightingConfig.DefaultPath);

        var directory = Path.GetDirectoryName(LightingConfig.DefaultPath)!;
        Directory.CreateDirectory(directory);

        using var changed = new SemaphoreSlim(0);
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(LightingConfig.DefaultPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => changed.Release();
        watcher.Created += (_, _) => changed.Release();
        watcher.Renamed += (_, _) => changed.Release();

        // Watch before startup retries so edits made while a device is unavailable aren't missed.
        await manager.ApplyOnAsync(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await changed.WaitAsync(stoppingToken);

            // Editors fire several events per save; wait for them to settle.
            await Task.Delay(500, stoppingToken);
            while (changed.CurrentCount > 0)
            {
                await changed.WaitAsync(stoppingToken);
            }

            logger.LogInformation("Config changed; re-applying");
            await manager.ApplyOnAsync(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
