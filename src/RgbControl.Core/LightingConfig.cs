using System.Text.Json;
using System.Text.Json.Serialization;
using RgbControl.Core.Aura;

namespace RgbControl.Core;

public sealed class LightingConfig
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RgbControl", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Master switch: turns every device off without losing its settings.</summary>
    public bool AllLightingOff { get; set; }

    public MotherboardLighting Motherboard { get; set; } = new();

    public RamLighting Ram { get; set; } = new();

    /// <summary>Turn lighting off when the PC goes to sleep, and back on when it wakes.</summary>
    public bool TurnOffOnSleep { get; set; } = true;

    /// <summary>Turn lighting off right before Windows shuts down.</summary>
    public bool TurnOffOnShutdown { get; set; } = true;

    public static LightingConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new LightingConfig();
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<LightingConfig>(stream, JsonOptions) ?? new LightingConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write-then-rename so the service never reads a half-written file.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class MotherboardLighting
{
    public bool Enabled { get; set; } = true;
    public AuraMode Mode { get; set; } = AuraMode.Static;
    public string Color { get; set; } = "#FFFFFF";

    /// <summary>0-100. Applied by dimming the color, so it has no effect on rainbow/spectrum effects.</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Also apply the effect to the board's ARGB headers.</summary>
    public bool IncludeAddressableHeaders { get; set; } = true;

    /// <summary>The color actually sent to the controller.</summary>
    [JsonIgnore]
    public Rgb EffectiveColor => Rgb.Parse(Color).Scale(Brightness);
}

/// <summary>ENE-based RGB DRAM (e.g. G.Skill Trident Z5 RGB). Uses the same effect numbering as Aura.</summary>
public sealed class RamLighting
{
    public bool Enabled { get; set; } = true;
    public AuraMode Mode { get; set; } = AuraMode.Static;
    public string Color { get; set; } = "#FFFFFF";

    /// <summary>0-100. Applied by dimming the color, so it has no effect on rainbow/spectrum effects.</summary>
    public int Brightness { get; set; } = 100;

    public EffectSpeed Speed { get; set; } = EffectSpeed.Normal;

    [JsonIgnore]
    public Rgb EffectiveColor => Rgb.Parse(Color).Scale(Brightness);
}

public enum EffectSpeed
{
    Slowest,
    Slow,
    Normal,
    Fast,
    Fastest,
}
