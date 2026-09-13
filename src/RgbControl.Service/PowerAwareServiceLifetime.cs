using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace RgbControl.Service;

/// <summary>Windows service lifetime that also reacts to shutdown, sleep and wake.</summary>
public sealed class PowerAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly LightingManager _manager;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;

    public PowerAwareServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions,
        IOptions<WindowsServiceLifetimeOptions> serviceOptions,
        LightingManager manager)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions)
    {
        _manager = manager;
        _applicationLifetime = applicationLifetime;
        _logger = loggerFactory.CreateLogger<PowerAwareServiceLifetime>();
        CanShutdown = true;
        CanHandlePowerEvent = true;
    }

    protected override void OnShutdown()
    {
        if (_manager.LoadConfig().TurnOffOnShutdown)
        {
            _manager.TurnOff("shutdown");
        }

        base.OnShutdown();
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _manager.MarkSuspended(true);
                if (_manager.LoadConfig().TurnOffOnSleep)
                {
                    _manager.TurnOff("sleep");
                }
                break;

            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeSuspend:
                // Windows sends both resume events; only react to the first.
                // Re-apply even if we didn't turn off, since the firmware may reset the controller on wake.
                if (!_manager.IsSuspended)
                {
                    break;
                }

                _manager.MarkSuspended(false);
                _logger.LogInformation("Resumed from sleep; re-applying lighting");
                _ = Task.Run(() => _manager.ApplyOnAsync(TimeSpan.FromMinutes(1), _applicationLifetime.ApplicationStopping));
                break;
        }

        return base.OnPowerEvent(powerStatus);
    }
}
