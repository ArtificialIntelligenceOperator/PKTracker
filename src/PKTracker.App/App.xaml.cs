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

    public App() => UseUniformNumbers();

    /// <summary>
    /// Числа во всём окне пишутся одинаково: дробная часть через точку, разряды — как принято в системе («12 345»).
    /// Иначе карточка, отформатированная в коде, показывала «12,34», а таблица рядом, через привязку, — «56.7».
    /// Точка выбрана потому, что так пишут bb/100 и проценты все покерные программы и сам HUD. На разбор файлов
    /// это не влияет: парсеры читают числа только с инвариантной культурой.
    ///
    /// Вызывается из конструктора, а не из OnStartup: тот — async, а культура, выставленная внутри async-метода,
    /// живёт только в его контексте выполнения и до остального интерфейса не доходит.
    /// </summary>
    private static void UseUniformNumbers()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
        var nf = culture.NumberFormat;
        // Где разряды разделяют точкой («1.234,5»), точка в роли десятичного знака дала бы «1.234.5».
        if (nf.NumberGroupSeparator == ".") nf.NumberGroupSeparator = "\u00A0";
        nf.NumberDecimalSeparator = nf.CurrencyDecimalSeparator = nf.PercentDecimalSeparator = ".";
        nf.CurrencyGroupSeparator = nf.PercentGroupSeparator = nf.NumberGroupSeparator;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
    }

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

        // «--tab <название>» — открыть приложение сразу на нужной вкладке (ярлык на «Аналитика», проверка интерфейса).
        int ti = Array.IndexOf(e.Args, "--tab");
        if (ti >= 0 && ti + 1 < e.Args.Length) _main.SelectTab(e.Args[ti + 1]);

        // «--import <путь> [путь…]» — открыть вкладку «Импорт» и сразу загрузить папку, файлы или архив:
        // тот же путь, что у перетаскивания (ярлык «загрузить выгрузку», проверка интерфейса).
        int ii = Array.IndexOf(e.Args, "--import");
        if (ii >= 0 && ii + 1 < e.Args.Length)
            _ = _main.ImportFromCommandLineAsync(e.Args.Skip(ii + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList());

        // «--replay <номер раздачи>» — сразу открыть раздачу в реплеере (номер рума или внутренний hand_pk).
        int ri = Array.IndexOf(e.Args, "--replay");
        if (ri >= 0 && ri + 1 < e.Args.Length && long.TryParse(e.Args[ri + 1], out long handId) && Services.Host is { } replayHost)
        {
            try
            {
                var pk = await Task.Run(() => new PKTracker.Data.ReportRepository(replayHost.Db).ResolveHandPkAsync(handId));
                if (pk is { } found) new Replayer.ReplayerWindow(replayHost, found, Services.Settings).Show();
                else Services.Log("App", $"Раздача {handId} не найдена");
            }
            catch (Exception ex) { Services.Log("App", "Реплеер: " + ex.Message); }
        }

        // Предпросмотр раскладки без стола: «PKTracker.exe --preview <ник> [вкладка]» — панель со всеми
        // вкладками и попап. Нужен, чтобы проверить раскладку глазами, не открывая клиент рума.
        int pi = Array.IndexOf(e.Args, "--preview");
        if (pi >= 0 && pi + 1 < e.Args.Length)
        {
            // «--preview <ник> [группа/вкладка] [код стата]»: третий аргумент сразу открывает разбор стата.
            try
            {
                await Hud.LayoutPreview.ShowAsync(Services, e.Args[pi + 1], pi + 2 < e.Args.Length ? e.Args[pi + 2] : null,
                    pi + 3 < e.Args.Length ? e.Args[pi + 3] : null);
            }
            catch (Exception ex)
            {
                Services.Log("App", "Предпросмотр раскладки: " + ex);
                MessageBox.Show(ex.Message, "PKTracker — предпросмотр", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
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
