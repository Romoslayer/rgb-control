using System.Globalization;

namespace RgbControl.Core;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>Parses "#RRGGBB" or "RRGGBB".</summary>
    public static Rgb Parse(string value)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
        {
            throw new FormatException($"'{value}' is not a color. Use the form #RRGGBB, e.g. #FF0080.");
        }

        return new Rgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
    }

    /// <summary>Dims the color. These controllers have no brightness setting, so brightness is applied to the color itself.</summary>
    public Rgb Scale(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return new Rgb(Dim(R), Dim(G), Dim(B));

        byte Dim(byte channel) => (byte)Math.Round(channel * percent / 100.0);
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
