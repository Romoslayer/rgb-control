using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using RgbControl.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "RgbControl");
builder.Services.Configure<EventLogSettings>(settings => settings.SourceName = "RgbControl");

builder.Services.AddSingleton<LightingManager>();
builder.Services.AddHostedService<LightingWorker>();

if (WindowsServiceHelpers.IsWindowsService())
{
    // Replaces the default service lifetime so we get shutdown and sleep/wake notifications.
    builder.Services.AddSingleton<IHostLifetime, PowerAwareServiceLifetime>();
}

builder.Build().Run();
