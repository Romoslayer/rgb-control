using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RgbControl.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\RgbControl.App";
    private const string ShowEventName = @"Local\RgbControl.App.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;

    public AppSettings Settings { get; } = AppSettings.Load();

    public bool IsExiting { get; private set; }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var snapshotIndex = Array.IndexOf(e.Args, "--snapshot");
        if (snapshotIndex >= 0 && snapshotIndex + 1 < e.Args.Length)
        {
            RunSnapshot(e.Args[snapshotIndex + 1]);
            return;
        }

        // Single instance: a second launch just brings the running app's window forward.
        _instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _viewModel = new MainViewModel();
        _tray = new TrayIcon(_viewModel, ShowMainWindow, ExitApp);

        new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "ShowWindowListener" }.Start();

        if (!e.Args.Contains("--tray"))
        {
            ShowMainWindow();
        }
    }

    public void ShowMainWindow()
    {
        _window ??= new MainWindow(_viewModel!);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    /// <summary>Called when the window is closed while "close to tray" is on.</summary>
    public void OnHiddenToTray()
    {
        if (!Settings.TrayTipShown)
        {
            _tray?.ShowTip("RGB Control is still running", "Use the tray icon to turn lighting on or off, or right-click it to exit.");
            Settings.TrayTipShown = true;
            Settings.Save();
        }
    }

    public void ExitApp()
    {
        IsExiting = true;
        _viewModel?.FlushPendingSave();
        _window?.Close();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>"--snapshot file.png" renders the window to an image and exits (used to check layout without a screen).</summary>
    private void RunSnapshot(string path)
    {
        var window = new MainWindow(new MainViewModel()) { Left = -10000, ShowActivated = false, Height = 1500 };
        window.ContentRendered += (_, _) =>
        {
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
            IsExiting = true;
            Shutdown();
        };
        window.Show();
    }
}
