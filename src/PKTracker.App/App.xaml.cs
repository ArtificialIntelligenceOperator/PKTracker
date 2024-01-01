using System.Runtime;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PKTracker.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _main;

    public AppServices Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Один экземпляр: два трекера = два HUD и двойной импорт (§15.11)
        _mutex = new Mutex(true, @"Local\PKTracker.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("PKTracker уже запущен (см. значок в трее).", "PKTracker", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Services = new AppServices(Dispatcher);

        // После падения на экране не должны оставаться осиротевшие topmost-панели (§15.11)
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Services.Log("App", "Необработанное исключение: " + args.ExceptionObject);
            Dispatcher.Invoke(() => Services.Hud.CloseAllPanels());
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.Log("App", "Unobserved: " + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };

        _main = new MainWindow(Services);
        CreateTray();
        _main.Show();

        await Services.StartAsync();
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Log("App", "Ошибка UI: " + e.Exception);
        Services.Hud.CloseAllPanels();
        e.Handled = true;
        MessageBox.Show(e.Exception.Message, "PKTracker — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void CreateTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "PKTracker",
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowMain));
        _tray.ContextMenuStrip.Items.Add("HUD вкл/выкл", null, (_, _) => Dispatcher.Invoke(() =>
        {
            Services.Hud.Enabled = !Services.Hud.Enabled;
            Services.Log("Hud", Services.Hud.Enabled ? "HUD включён" : "HUD выключен");
        }));
        _tray.ContextMenuStrip.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
    }

    /// <summary>Иконка трея — та же, что у exe и ярлыка; если достать не вышло, системная.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } ico) return ico;
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void ShowMain()
    {
        _main ??= new MainWindow(Services);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public async void ExitApp()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        await Services.DisposeAsync();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    /// <summary>Пока открыт хоть один стол — SustainedLowLatency, чтобы GC не давал микрофризов оверлея (§10.2).</summary>
    public static void SetLowLatency(bool on) =>
        GCSettings.LatencyMode = on ? GCLatencyMode.SustainedLowLatency : GCLatencyMode.Interactive;
}
