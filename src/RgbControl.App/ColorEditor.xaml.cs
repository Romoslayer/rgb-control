using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RgbControl.Core;

namespace RgbControl.App;

public partial class ColorEditor : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorEditor),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

    private static readonly string[] PresetColors =
        ["#FF0000", "#FF4000", "#FFA000", "#FFFF00", "#00FF00", "#00FFFF", "#0060FF", "#8000FF", "#FF00FF", "#FFFFFF"];

    private bool _updating;

    public ColorEditor()
    {
        InitializeComponent();
        Presets.ItemsSource = PresetColors;
        ShowColor(Rgb.Parse(Hex));
    }

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (ColorEditor)d;
        if (!editor._updating && TryParse(e.NewValue as string, out var color))
        {
            editor.ShowColor(color);
        }
    }

    private void ShowColor(Rgb color)
    {
        _updating = true;
        Red.Value = color.R;
        Green.Value = color.G;
        Blue.Value = color.B;
        HexBox.Text = color.ToString();
        Preview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        _updating = false;
    }

    private void Commit(Rgb color)
    {
        ShowColor(color);
        _updating = true;
        Hex = color.ToString();
        _updating = false;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating)
        {
            Commit(new Rgb((byte)Red.Value, (byte)Green.Value, (byte)Blue.Value));
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e) => Commit(Rgb.Parse((string)((Button)sender).Tag));

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnHexCommit(sender, e);
        }
    }

    private void OnHexCommit(object sender, RoutedEventArgs e)
    {
        if (TryParse(HexBox.Text, out var color))
        {
            Commit(color);
        }
        else
        {
            ShowColor(Rgb.Parse(Hex));
        }
    }

    private static bool TryParse(string? value, out Rgb color)
    {
        try
        {
            color = Rgb.Parse(value ?? "");
            return true;
        }
        catch (FormatException)
        {
            color = default;
            return false;
        }
    }
}
