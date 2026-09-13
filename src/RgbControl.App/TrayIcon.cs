using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace RgbControl.App;

/// <summary>Notification-area icon with quick on/off, built on WinForms' NotifyIcon.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _lightingItem;
    private readonly Icon _onIcon = LoadIcon("RgbControl.ico");
    private readonly Icon _offIcon = LoadIcon("RgbControl-off.ico");

    public TrayIcon(MainViewModel viewModel, Action showWindow, Action exit)
    {
        _viewModel = viewModel;

        _lightingItem = new ToolStripMenuItem("Lighting on") { CheckOnClick = true };
        _lightingItem.Click += (_, _) => _viewModel.LightingOn = _lightingItem.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open RGB Control", null, (_, _) => showWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_lightingItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _notifyIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                showWindow();
            }
        };

        _viewModel.PropertyChanged += OnViewModelChanged;
        Refresh();
    }

    public void ShowTip(string title, string text) => _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.None);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LightingOn))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var on = _viewModel.LightingOn;
        _lightingItem.Checked = on;
        _notifyIcon.Icon = on ? _onIcon : _offIcon;
        _notifyIcon.Text = on ? "RGB Control - lighting on" : "RGB Control - lighting off";
    }

    private static Icon LoadIcon(string name)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))!;
        using var stream = resource.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _onIcon.Dispose();
        _offIcon.Dispose();
    }
}
