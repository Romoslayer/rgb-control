using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace RgbControl.App;

/// <summary>Per-user app preferences (not lighting; that lives in the machine-wide config the service reads).</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RgbControl", "app-settings.json");

    /// <summary>Closing the window keeps the app in the notification area instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Whether the "still running in the tray" tip has been shown.</summary>
    public bool TrayTipShown { get; set; }

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Preferences are best-effort.
        }
    }
}

/// <summary>Launch at sign-in via the per-user Run key, starting minimized to the tray.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RGB Control";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
