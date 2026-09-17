using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RgbControl.Core;
using RgbControl.Core.Aura;

namespace RgbControl.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record ModeOption(AuraMode Mode, string Label, bool UsesColor)
{
    /// <summary>Effects supported by both the Aura USB controller and ENE RAM. "Off" is the card's on/off switch.</summary>
    public static readonly IReadOnlyList<ModeOption> All =
    [
        new(AuraMode.Static, "Static", true),
        new(AuraMode.Breathing, "Breathing", true),
        new(AuraMode.Flashing, "Flashing", true),
        new(AuraMode.ChaseFade, "Chase fade", true),
        new(AuraMode.Chase, "Chase", true),
        new(AuraMode.SpectrumCycle, "Spectrum cycle", false),
        new(AuraMode.Rainbow, "Rainbow", false),
    ];

    public static ModeOption For(AuraMode mode) => All.FirstOrDefault(o => o.Mode == mode) ?? All[0];

    public bool IsAnimated => Mode != AuraMode.Static;
}

public sealed record SpeedOption(EffectSpeed Speed, string Label)
{
    public static readonly IReadOnlyList<SpeedOption> All =
    [
        new(EffectSpeed.Slowest, "Slowest"),
        new(EffectSpeed.Slow, "Slow"),
        new(EffectSpeed.Normal, "Normal"),
        new(EffectSpeed.Fast, "Fast"),
        new(EffectSpeed.Fastest, "Fastest"),
    ];

    public static SpeedOption For(EffectSpeed speed) => All.FirstOrDefault(o => o.Speed == speed) ?? All[2];
}

public sealed class DeviceLightingViewModel : ObservableObject
{
    private bool _enabled;
    private ModeOption _mode;
    private string _color;
    private int _brightness;
    private SpeedOption _speed;

    public DeviceLightingViewModel(bool enabled, AuraMode mode, string color, int brightness, EffectSpeed? speed)
    {
        _enabled = enabled && mode != AuraMode.Off;
        _mode = ModeOption.For(mode);
        _color = color;
        _brightness = Math.Clamp(brightness, 0, 100);
        SupportsSpeed = speed is not null;
        _speed = SpeedOption.For(speed ?? EffectSpeed.Normal);
    }

    public IReadOnlyList<ModeOption> Modes { get; private set; } = ModeOption.All;
    public void SetSupportedModes(IEnumerable<AuraMode> modes)
    {
        Modes = ModeOption.All.Where(m => modes.Contains(m.Mode)).ToList();
        if (!Modes.Any(m => m.Mode == Mode.Mode)) Mode = Modes[0];
        OnPropertyChanged(nameof(Modes));
    }
    public IReadOnlyList<SpeedOption> Speeds => SpeedOption.All;

    /// <summary>The Aura USB motherboard protocol has no speed setting; ENE RAM does.</summary>
    public bool SupportsSpeed { get; }

    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) RaiseVisibility(); }
    }

    public ModeOption Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) RaiseVisibility(); }
    }

    public string Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

    public int Brightness
    {
        get => _brightness;
        set => Set(ref _brightness, Math.Clamp(value, 0, 100));
    }

    public SpeedOption Speed
    {
        get => _speed;
        set => Set(ref _speed, value);
    }

    public bool ShowColor => Enabled && Mode.UsesColor;

    /// <summary>Speed stays visible on devices that support it, so it's discoverable; it's only adjustable for animated effects.</summary>
    public bool ShowSpeed => Enabled && SupportsSpeed;
    public bool CanChangeSpeed => Mode.IsAnimated;

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(CanChangeSpeed));
    }
}

public sealed class MotherboardViewModel(MotherboardLighting config) : ObservableObject
{
    private MotherboardKind _controller = config.Controller;
    public IReadOnlyList<MotherboardOption> Controllers { get; } =
        [new(MotherboardKind.AsusAura, "ASUS Aura"), new(MotherboardKind.GigabyteB650AorusEliteAx, "Gigabyte B650 AORUS Elite AX (experimental)")];
    public MotherboardKind Controller { get => _controller; set => Set(ref _controller, value); }
    private bool _includeAddressableHeaders = config.IncludeAddressableHeaders;

    public DeviceLightingViewModel Lighting { get; } = new(config.Enabled, config.Mode, config.Color, config.Brightness, speed: null);

    public bool IncludeAddressableHeaders
    {
        get => _includeAddressableHeaders;
        set => Set(ref _includeAddressableHeaders, value);
    }
}

public sealed record MotherboardOption(MotherboardKind Kind, string Label);

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _serviceTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private bool _lightingOn;
    private bool _turnOffOnSleep;
    private bool _turnOffOnShutdown;
    private string _status = "";
    private string _serviceStatus = "";
    private bool _serviceRunning;
    private bool _isBusy;
    private bool _initializing = true;

    public MainViewModel()
    {
        LightingConfig config;
        try
        {
            config = LightingConfig.Load();
        }
        catch (Exception ex)
        {
            config = new LightingConfig();
            _status = $"Couldn't read config ({ex.Message}); showing defaults.";
        }

        _lightingOn = !config.AllLightingOff;
        Motherboard = new MotherboardViewModel(config.Motherboard);
        Ram = new DeviceLightingViewModel(config.Ram.Enabled, config.Ram.Mode, config.Ram.Color, config.Ram.Brightness, config.Ram.Speed);
        _turnOffOnSleep = config.TurnOffOnSleep;
        _turnOffOnShutdown = config.TurnOffOnShutdown;
        Gpus = Enum.GetValues<GpuKind>().Select(kind => new GpuViewModel(config.Gpus.FirstOrDefault(g => g.Kind == kind)
            ?? new GpuLighting { Kind = kind })).ToList();
        foreach (var gpu in Gpus)
        {
            gpu.PropertyChanged += (_, _) => ScheduleSave();
            gpu.Lighting.PropertyChanged += (_, _) => ScheduleSave();
        }

        ArgbHeaders = Enumerable.Range(1, Math.Max(3, config.ArgbHeaders.Select(h => h.Header).DefaultIfEmpty(0).Max()))
            .Select(index => new ArgbHeaderViewModel(config.ArgbHeaders.FirstOrDefault(h => h.Header == index)
                ?? new ArgbHeaderLighting { Header = index, Name = $"ARGB header {index}" }))
            .ToList();
        foreach (var header in ArgbHeaders)
        {
            header.PropertyChanged += (_, _) => ScheduleSave();
            header.Lighting.PropertyChanged += (_, _) => ScheduleSave();
        }

        Motherboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MotherboardViewModel.Controller)) UpdateMotherboardModes();
            ScheduleSave();
        };
        UpdateMotherboardModes();
        Motherboard.Lighting.PropertyChanged += (_, _) => ScheduleSave();
        Ram.PropertyChanged += (_, _) => ScheduleSave();

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _serviceTimer.Tick += (_, _) => RefreshServiceStatus();
        _serviceTimer.Start();
        RefreshServiceStatus();

        CopyMotherboardToRamCommand = new RelayCommand(() =>
        {
            Ram.Enabled = Motherboard.Lighting.Enabled;
            Ram.Mode = Motherboard.Lighting.Mode;
            Ram.Color = Motherboard.Lighting.Color;
            Ram.Brightness = Motherboard.Lighting.Brightness;
        });
        SaveStartupColorCommand = new RelayCommand(SaveStartupColor, () => !_isBusy);
        _initializing = false;
    }

    public MotherboardViewModel Motherboard { get; }
    public DeviceLightingViewModel Ram { get; }
    public IReadOnlyList<ArgbHeaderViewModel> ArgbHeaders { get; }
    public IEnumerable<ArgbHeaderViewModel> VisibleArgbHeaders => Motherboard.Controller == MotherboardKind.GigabyteB650AorusEliteAx
        ? ArgbHeaders.Where(h => h.Header <= 2) : ArgbHeaders;
    public IReadOnlyList<GpuViewModel> Gpus { get; }

    private void UpdateMotherboardModes()
    {
        var modes = Motherboard.Controller == MotherboardKind.GigabyteB650AorusEliteAx
            ? new[] { AuraMode.Static, AuraMode.Breathing, AuraMode.Flashing, AuraMode.SpectrumCycle }
            : ModeOption.All.Select(m => m.Mode).ToArray();
        Motherboard.Lighting.SetSupportedModes(modes);
        foreach (var header in ArgbHeaders) header.Lighting.SetSupportedModes(modes);
        OnPropertyChanged(nameof(VisibleArgbHeaders));
    }

    /// <summary>Master switch (tray and header). Off keeps every device's settings.</summary>
    public bool LightingOn
    {
        get => _lightingOn;
        set
        {
            if (Set(ref _lightingOn, value))
            {
                // Quick toggle: apply right away rather than waiting for the debounce.
                _saveTimer.Stop();
                Save();
            }
        }
    }

    public bool TurnOffOnSleep
    {
        get => _turnOffOnSleep;
        set { if (Set(ref _turnOffOnSleep, value)) ScheduleSave(); }
    }

    public bool TurnOffOnShutdown
    {
        get => _turnOffOnShutdown;
        set { if (Set(ref _turnOffOnShutdown, value)) ScheduleSave(); }
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            try
            {
                StartupRegistration.Set(value);
            }
            catch (Exception ex)
            {
                Status = $"Couldn't change startup setting: {ex.Message}";
            }
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => AppSettingsOrDefault.CloseToTray;
        set
        {
            AppSettingsOrDefault.CloseToTray = value;
            AppSettingsOrDefault.Save();
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string ServiceStatus
    {
        get => _serviceStatus;
        private set => Set(ref _serviceStatus, value);
    }

    public bool ServiceRunning
    {
        get => _serviceRunning;
        private set => Set(ref _serviceRunning, value);
    }

    public ICommand CopyMotherboardToRamCommand { get; }
    public RelayCommand SaveStartupColorCommand { get; }

    private static AppSettings AppSettingsOrDefault => (Application.Current as App)?.Settings ?? FallbackSettings;
    private static readonly AppSettings FallbackSettings = new();

    private void ScheduleSave()
    {
        if (_initializing) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Flushes any pending change immediately (e.g. when the window closes).</summary>
    public void FlushPendingSave()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
    }

    private LightingConfig ToConfig() => new()
    {
        AllLightingOff = !LightingOn,
        Motherboard = new MotherboardLighting
        {
            Controller = Motherboard.Controller,
            Enabled = Motherboard.Lighting.Enabled,
            Mode = Motherboard.Lighting.Mode.Mode,
            Color = Motherboard.Lighting.Color,
            Brightness = Motherboard.Lighting.Brightness,
            IncludeAddressableHeaders = Motherboard.IncludeAddressableHeaders,
        },
        Ram = new RamLighting
        {
            Enabled = Ram.Enabled,
            Mode = Ram.Mode.Mode,
            Color = Ram.Color,
            Brightness = Ram.Brightness,
            Speed = Ram.Speed.Speed,
        },
        TurnOffOnSleep = TurnOffOnSleep,
        TurnOffOnShutdown = TurnOffOnShutdown,
        ArgbHeaders = ArgbHeaders.Select(h => h.ToConfig()).ToList(),
        Gpus = Gpus.Select(g => g.ToConfig()).ToList(),
    };

    private void Save()
    {
        try
        {
            ToConfig().Save();
            Status = ServiceRunning
                ? $"Saved {DateTime.Now:t}. The service is applying your changes."
                : $"Saved {DateTime.Now:t}. The service isn't running, so changes apply when it starts.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't save: {ex.Message}";
        }
    }

    private async void SaveStartupColor()
    {
        if (Motherboard.Controller != MotherboardKind.AsusAura)
        {
            Status = "Saving a startup color to hardware is currently supported only on ASUS. Gigabyte settings apply when the Windows service starts.";
            return;
        }
        FlushPendingSave();
        var config = ToConfig();
        _isBusy = true;
        SaveStartupColorCommand.RaiseCanExecuteChanged();
        Status = "Saving to the motherboard controller...";

        try
        {
            var saved = await Task.Run(() =>
            {
                using var aura = AuraUsbController.TryOpen();
                if (aura is null)
                {
                    return false;
                }

                LightingActions.SaveToHardware(aura, config);
                return true;
            });

            Status = saved
                ? "Saved to the motherboard: this color shows during startup, and the board stays dark while the PC is off."
                : "Motherboard lighting controller not found.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't save to the motherboard: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            SaveStartupColorCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshServiceStatus()
    {
        try
        {
            using var service = new ServiceController("RgbControl");
            ServiceRunning = service.Status == ServiceControllerStatus.Running;
            ServiceStatus = ServiceRunning ? "Service running" : $"Service {service.Status.ToString().ToLowerInvariant()}";
        }
        catch (InvalidOperationException)
        {
            ServiceRunning = false;
            ServiceStatus = "Service not installed";
        }
    }
}

public sealed class GpuViewModel : ObservableObject
{
    private bool _managed;
    private GpuConnection _connection;
    public GpuViewModel(GpuLighting config)
    {
        Kind = config.Kind;
        _managed = config.Managed;
        _connection = config.Connection;
        Lighting = new(config.Enabled, config.Mode, config.Color, config.Brightness, null);
        Lighting.SetSupportedModes([AuraMode.Static, AuraMode.Rainbow, AuraMode.SpectrumCycle]);
    }
    public GpuKind Kind { get; }
    public string Name => Kind == GpuKind.SapphireNitro9070Xt ? "Sapphire NITRO+ RX 9070 XT" : "PowerColor Red Devil RX 9070 XT";
    public bool Managed { get => _managed; set => Set(ref _managed, value); }
    public bool UseArgbCable
    {
        get => _connection == GpuConnection.ArgbCable;
        set { _connection = value ? GpuConnection.ArgbCable : GpuConnection.Direct; OnPropertyChanged(); OnPropertyChanged(nameof(UseDirect)); }
    }
    public bool UseDirect => !UseArgbCable;
    public DeviceLightingViewModel Lighting { get; }
    public GpuLighting ToConfig() => new()
    {
        Kind = Kind, Managed = Managed, Connection = _connection, Enabled = Lighting.Enabled,
        Mode = Lighting.Mode.Mode, Color = Lighting.Color, Brightness = Lighting.Brightness,
    };
}

public sealed class ArgbHeaderViewModel(ArgbHeaderLighting config) : ObservableObject
{
    private string _name = config.Name;
    private bool _useCustomSettings = config.UseCustomSettings;
    public int Header { get; } = config.Header;
    public string HeaderLabel => $"ARGB header {Header}";
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool UseCustomSettings { get => _useCustomSettings; set => Set(ref _useCustomSettings, value); }
    public DeviceLightingViewModel Lighting { get; } = new(config.Enabled, config.Mode, config.Color, config.Brightness, null);

    public ArgbHeaderLighting ToConfig() => new()
    {
        Header = Header, Name = Name, UseCustomSettings = UseCustomSettings,
        Enabled = Lighting.Enabled, Mode = Lighting.Mode.Mode,
        Color = Lighting.Color, Brightness = Lighting.Brightness,
    };
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
