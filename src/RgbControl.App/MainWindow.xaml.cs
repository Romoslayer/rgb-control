using System.ComponentModel;
using System.Windows;

namespace RgbControl.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.FlushPendingSave();

        var app = (App)Application.Current;
        if (!app.IsExiting && app.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            app.OnHiddenToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        var app = (App)Application.Current;
        if (!app.IsExiting && !app.Settings.CloseToTray)
        {
            app.ExitApp();
        }
    }
}
