using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PKTracker.Data;
using PKTracker.Data.Import;
using PKTracker.Parsers;

namespace PKTracker.App;

/// <summary>
/// Вкладка «Импорт»: выбор базы, аккаунты Hero и загрузка раздач.
///
/// Загрузка принимает папку, отдельные файлы и архивы — перетаскиванием или кнопками. Ник заранее
/// вводить не обязательно: если в файлах розданы карты игроку, которого нет в списке, трекер спрашивает,
/// ваш ли это аккаунт, и после ответа «да» повторяет импорт. Так сохраняется правило «только свои раздачи»,
/// но человеку не нужно угадывать, что и в каком порядке заполнить.
/// </summary>
public partial class MainWindow
{
    /// <summary>Больше стольких незнакомых ников в одной загрузке — это уже не «мой второй аккаунт», а общая выгрузка.</summary>
    private const int MaxHeroesToOffer = 4;

    private bool _dbListLoading;

    // ── Аккаунты Hero ──────────────────────────────────────────────────────

    private async Task LoadHeroesAsync()
    {
        var host = Host;
        if (HeroRoomCombo.Items.Count == 0)
            foreach (var room in ParserRegistry.Rooms)
                HeroRoomCombo.Items.Add(new ComboBoxItem { Content = room.Title, Tag = room.Code });
        if (HeroRoomCombo.SelectedItem == null)
            HeroRoomCombo.SelectedItem = HeroRoomCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, _svc.Settings.RoomCode, StringComparison.OrdinalIgnoreCase)) ?? HeroRoomCombo.Items[0];
        ImportObservedBox.IsChecked = _svc.Settings.ImportObservedHands;
        ImportGgBox.IsChecked = _svc.Settings.ImportGgHands;
        if (host == null) { HeroList.ItemsSource = null; return; }
        try { HeroList.ItemsSource = await Bg(() => host.Heroes.ListAllAsync()); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or ObjectDisposedException or InvalidOperationException)
        {
            _svc.Log("Import", "Список ников: " + ex.Message);
        }
    }

    private async void HeroAdd_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        var nick = HeroBox.Text.Trim();
        if (host == null || nick.Length == 0 || HeroRoomCombo.SelectedItem is not ComboBoxItem { Tag: string room }) return;
        await Task.Run(async () => { await host.Heroes.AddForRoomAsync(nick, room); await host.ReloadAsync(); });
        HeroBox.Clear();
        await LoadHeroesAsync();
        _svc.Log("Import", $"Добавлен Hero: {nick} ({room})");
    }

    private async void HeroRemove_Click(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host == null || HeroList.SelectedItem is not HeroAccount acc) return;
        await Task.Run(async () => { await host.Heroes.RemoveForRoomAsync(acc.Nickname, acc.RoomCode); await host.ReloadAsync(); });
        await LoadHeroesAsync();
    }

    // ── Загрузка раздач ────────────────────────────────────────────────────

    private static List<string> SplitPaths(string text) =>
        text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async void ImportBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Папка с историей раздач (вложенные папки и архивы читаются тоже)", Multiselect = true };
        if (dlg.ShowDialog(this) == true) await ImportChosenAsync(dlg.FolderNames);
    }

    private async void ImportBrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Файлы истории раздач или архив", Filter = ImportSources.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) await ImportChosenAsync(dlg.FileNames);
    }

    private void Import_DragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop) && ImportButton.IsEnabled;
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropFrame.Stroke = (Brush)FindResource(files ? "Accent" : "BorderBrush");
        e.Handled = true;
    }

    private void Import_DragLeave(object sender, DragEventArgs e) => DropFrame.Stroke = (Brush)FindResource("BorderBrush");

    private async void Import_Drop(object sender, DragEventArgs e)
    {
        DropFrame.Stroke = (Brush)FindResource("BorderBrush");
        if (!ImportButton.IsEnabled || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        await ImportChosenAsync(paths);
    }

    /// <summary>Выбранное диалогом или перетаскиванием импортируется сразу — без отдельного нажатия «Импортировать».</summary>
    private async Task ImportChosenAsync(IReadOnlyList<string> paths)
    {
        ImportFolderBox.Text = string.Join(" | ", paths);
        await RunImportAsync(paths.ToList());
    }

    /// <summary>Запуск с ключом «--import»: вкладка «Импорт» и загрузка указанных путей.</summary>
    public async Task ImportFromCommandLineAsync(IReadOnlyList<string> paths)
    {
        SelectTab("Импорт");
        if (paths.Count > 0) await ImportChosenAsync(paths);
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await RunImportAsync(SplitPaths(ImportFolderBox.Text));

    private void ImportCancel_Click(object sender, RoutedEventArgs e) => _importCts?.Cancel();

    /// <summary>Раздачи без Hero за столом: выключено по умолчанию, включает сам игрок — на свой риск (правила румов).</summary>
    private void ImportObserved_Click(object sender, RoutedEventArgs e)
    {
        _svc.Settings.ImportObservedHands = ImportObservedBox.IsChecked == true;
        SaveSettingsQuietly();
    }

    /// <summary>Раздачи сети GG: её политика запрещает сторонний импорт, поэтому включает сам игрок — на свой риск.</summary>
    private void ImportGg_Click(object sender, RoutedEventArgs e)
    {
        _svc.Settings.ImportGgHands = ImportGgBox.IsChecked == true;
        SaveSettingsQuietly();
    }

    /// <summary>
    /// Импорт целиком: загрузка, при необходимости вопрос про ники и повтор, затем проверка,
    /// не остались ли в базе раздачи, которым нужен пересчёт.
    /// </summary>
    private async Task RunImportAsync(List<string> paths)
    {
        var host = Host;
        if (host == null) { ImportStatusText.Text = "Нет подключения к базе — проверьте вкладку «Настройки»."; return; }
        if (paths.Count == 0) { ImportStatusText.Text = "Укажите, что загрузить: перетащите сюда папку, файлы или архив либо выберите их кнопкой."; return; }
        if (!ImportButton.IsEnabled) return; // импорт или пересчёт уже идёт

        var rep = await ImportOnceAsync(host, paths);
        if (rep == null) return;
        int imported = rep.Imported;
        // Оба вопроса задаются до повторного прохода, чтобы архив не читался трижды.
        bool again = rep.UnknownHeroes.Count > 0 && await OfferHeroesAsync(host, rep.UnknownHeroes);
        if (rep.UnknownWpn is { } wpn) again |= await OfferWpnRoomAsync(host, wpn);
        if (again) imported += (await ImportOnceAsync(host, paths))?.Imported ?? 0;
        if (imported > 0) await AfterImportAsync(host);
    }

    private async Task<ImportReport?> ImportOnceAsync(TrackerHost host, List<string> paths)
    {
        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;
        SetImportBusy(true);
        var progress = new Progress<ImportProgress>(p =>
        {
            ImportProgressBar.Maximum = Math.Max(1, p.FilesTotal);
            ImportProgressBar.Value = p.FilesDone;
            ImportStatusText.Text = $"Файлов {p.FilesDone:N0}/{p.FilesTotal:N0}  рук {p.Hands:N0}  импортировано {p.Imported:N0}  дубликатов {p.Duplicates:N0}  " +
                                    $"ошибок {p.Errors}  отклонено файлов {p.RejectedFiles:N0}  {p.HandsPerSecond:F0} рук/с" +
                                    (p.Eta is { } eta ? $"  осталось ~{eta:mm\\:ss}" : "");
        });
        try
        {
            bool observed = _svc.Settings.ImportObservedHands, gg = _svc.Settings.ImportGgHands;
            var rep = await Task.Run(() => host.CreateFileImporter(observed, gg).ImportPathsAsync(paths, progress, ct), ct);
            ImportProgressBar.Value = ImportProgressBar.Maximum;
            ImportStatusText.Text = $"Готово за {rep.Elapsed.TotalSeconds:F1} c: раздач {rep.Hands:N0}, импортировано {rep.Imported:N0}, " +
                                    $"дубликатов {rep.Duplicates:N0}, ошибок {rep.Errors:N0}, отклонено файлов {rep.RejectedFiles:N0}, турниров {rep.Tournaments}." +
                                    // Статы новых раздач пишутся той же транзакцией, что и сами раздачи: отдельный пересчёт не нужен.
                                    (rep.Imported > 0 ? " Статистика уже учитывает новые раздачи." : "") +
                                    (rep.Messages.Count > 0 ? "\n" + string.Join("\n", rep.Messages.Take(10)) : "") +
                                    (rep.Messages.Count > 10 ? $"\n… и ещё сообщений: {rep.Messages.Count - 10} (полный список — вкладка «Журнал»)." : "");
            _svc.Log("Import", $"Импорт: раздач {rep.Hands}, импортировано {rep.Imported}, дубликатов {rep.Duplicates}, ошибок {rep.Errors}, отклонено файлов {rep.RejectedFiles}");
            foreach (var m in rep.Messages) _svc.Log("Import", m);
            host.Hud.Invalidate(rep.AffectedPlayers);
            await _svc.Hud.RefreshTablesAsync();
            await LoadImportErrorsAsync();
            await LoadDatabasesAsync();
            return rep;
        }
        catch (OperationCanceledException)
        {
            ImportStatusText.Text = "Импорт отменён (уже записанные пачки сохранены, повторный импорт продолжит без дублей).";
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = "Ошибка: " + ex.Message;
            _svc.Log("Import", ex.ToString());
        }
        finally
        {
            SetImportBusy(false);
        }
        return null;
    }

    /// <summary>
    /// После догрузки в существующую базу. Новые раздачи импорт досчитывает сам и точно — сверка с пересчётом
    /// с нуля расхождений не даёт. Не трогает он старые: если они разобраны прежней версией программы,
    /// общая статистика смешивает два способа счёта. Это и проверяется, с предложением пересчитать.
    /// </summary>
    private async Task AfterImportAsync(TrackerHost host)
    {
        try
        {
            int caught = await Task.Run(() => host.CatchUpStaleStatsAsync());
            if (caught > 0) _svc.Log("Db", $"Досчитаны устаревшие статы: {caught}");
            long stale = await _svc.RefreshStaleContextAsync();
            if (stale == 0) return;
            var answer = MessageBox.Show(this,
                $"В базе {stale:N0} раздач, разобранных прежней версией программы. Новые раздачи посчитаны по-новому, " +
                "старые — по-старому, и часть статов сейчас смешивает оба варианта.\n\n" +
                "Пересчитать базу сейчас? Это займёт примерно столько же, сколько импорт этих раздач; " +
                "программой в это время можно пользоваться.",
                "База требует пересчёта", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes) await RecalcAsync(RecalcKind.Full, confirm: false);
            else DbActionText.Text = $"В базе {stale:N0} раздач прежней версии — «Пересчитать… → Полный пересчёт».";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or ObjectDisposedException)
        {
            _svc.Log("Db", "Проверка после импорта: " + ex.Message);
        }
    }

    // ── Пересчёт базы ──────────────────────────────────────────────────────

    private enum RecalcKind { Verify, Stats, Full }

    private void DbRecalc_Click(object sender, RoutedEventArgs e)
    {
        if (Host == null || !ImportButton.IsEnabled) return;
        var menu = new ContextMenu { PlacementTarget = DbRecalcButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string header, string hint, RecalcKind kind)
        {
            var item = new MenuItem { Header = header, ToolTip = hint };
            item.Click += async (_, _) => await RecalcAsync(kind, confirm: true);
            menu.Items.Add(item);
        }
        Add("Проверить статистику", "Сверяет сохранённые итоги статов с раздачами. Ничего не меняет", RecalcKind.Verify);
        Add("Пересчитать статистику", "Собирает статы заново из уже разобранных раздач. Обычно минуты", RecalcKind.Stats);
        Add("Полный пересчёт из текста раздач", "Разбирает каждую раздачу заново текущей версией программы. Долго — как повторный импорт", RecalcKind.Full);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Три уровня: сверка (только чтение), пересчёт итогов статов из сохранённых решений и полный разбор
    /// каждой раздачи из её текста. HUD всё это время читает прежние итоги и переключается на новые в конце.
    /// </summary>
    private async Task RecalcAsync(RecalcKind kind, bool confirm)
    {
        var host = Host;
        if (host == null || !ImportButton.IsEnabled) return;
        string db = DatabaseCatalog.Current(_svc.Settings.ConnectionString);
        if (confirm && kind != RecalcKind.Verify && MessageBox.Show(this,
                kind == RecalcKind.Stats
                    ? $"Пересчитать статистику базы «{db}»?\n\nИтоги всех статов будут собраны заново из уже разобранных раздач. " +
                      "Сами раздачи не меняются. Обычно это занимает минуты."
                    : $"Полностью пересчитать базу «{db}»?\n\nКаждая раздача будет заново разобрана из сохранённого текста текущей версией программы — " +
                      "это нужно после обновления программы. По времени — как повторный импорт всей базы.",
                "Пересчёт базы", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK)
            return;

        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;
        SetImportBusy(true);
        string title = kind switch { RecalcKind.Verify => "Проверка статистики", RecalcKind.Stats => "Пересчёт статистики", _ => "Полный пересчёт" };
        var progress = new Progress<RebuildProgress>(p =>
        {
            ImportProgressBar.IsIndeterminate = false;
            ImportProgressBar.Maximum = Math.Max(1, p.Total);
            ImportProgressBar.Value = p.Done;
            DbActionText.Text = $"{title}: {p.Done:N0} из {p.Total:N0}…";
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool offerRebuild = false;
        try
        {
            DbActionText.Text = title + "…";
            ImportProgressBar.IsIndeterminate = true;
            var rebuilder = new StatRebuilder(host.Db);
            switch (kind)
            {
                case RecalcKind.Verify:
                    var diffs = await Task.Run(() => rebuilder.VerifyAggregatesAsync(host.Stats, ct), ct);
                    foreach (var d in diffs.Take(50)) _svc.Log("Db", "Расхождение: " + d);
                    DbActionText.Text = diffs.Count == 0
                        ? $"Проверка пройдена за {sw.Elapsed.TotalSeconds:F0} c: статистика совпадает с раздачами."
                        : $"Найдено расхождений: {diffs.Count:N0} (первые — во вкладке «Журнал»).";
                    offerRebuild = diffs.Count > 0;
                    break;
                case RecalcKind.Stats:
                    await Task.Run(() => rebuilder.RebuildStatsAsync(host.Stats, progress, ct), ct);
                    host.Hud.InvalidateAll();
                    DbActionText.Text = $"Статистика пересчитана за {sw.Elapsed.TotalSeconds:F0} c.";
                    break;
                default:
                    await Task.Run(() => rebuilder.FullRebuildAsync(host.Stats, host.HeroNames, progress, ct), ct);
                    host.Hud.InvalidateAll();
                    long left = await _svc.RefreshStaleContextAsync();
                    DbActionText.Text = $"База пересчитана за {sw.Elapsed.TotalMinutes:F1} мин." + (left > 0 ? $" Осталось раздач прежней версии: {left:N0}." : "");
                    break;
            }
            _svc.Log("Db", DbActionText.Text);
            if (kind != RecalcKind.Verify) await _svc.Hud.RefreshTablesAsync();
        }
        catch (OperationCanceledException)
        {
            DbActionText.Text = kind == RecalcKind.Full
                ? "Полный пересчёт прерван. Уже пересчитанные раздачи сохранены, итоги статов остались прежними — запустите пересчёт снова, чтобы довести до конца."
                : title + ": отменено, в базе ничего не изменилось.";
        }
        catch (Exception ex)
        {
            DbActionText.Text = title + " — ошибка: " + ex.Message;
            _svc.Log("Db", ex.ToString());
        }
        finally
        {
            ImportProgressBar.IsIndeterminate = false;
            ImportProgressBar.Value = 0;
            SetImportBusy(false);
        }

        if (offerRebuild && MessageBox.Show(this,
                "Сохранённые итоги статов расходятся с раздачами. Пересчитать статистику сейчас?",
                "Проверка статистики", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            await RecalcAsync(RecalcKind.Stats, confirm: false);
    }

    private void SetImportBusy(bool busy)
    {
        ImportButton.IsEnabled = ImportFilesButton.IsEnabled = ImportFolderButton.IsEnabled = DbRecalcButton.IsEnabled = !busy;
        ImportCancelButton.IsEnabled = busy;
        // Смена базы посреди импорта оборвала бы запись пачки.
        DbCombo.IsEnabled = !busy;
    }

    /// <summary>
    /// В файлах нашлись раздачи с картами игроков, не заведённых как Hero. Спрашиваем, чьи это аккаунты:
    /// молчаливый отказ («отклонено файлов: 5975») не объясняет, что нужно сделать.
    /// </summary>
    /// <returns>true — ники добавлены, импорт нужно повторить.</returns>
    private async Task<bool> OfferHeroesAsync(TrackerHost host, IReadOnlyList<UnknownHero> unknown)
    {
        string List(IEnumerable<UnknownHero> items) =>
            string.Join("\n", items.Select(u => $"   •  {u.Nickname} — {u.RoomTitle}: {u.Hands:N0} раздач в {u.Files:N0} файл(ах)"));

        if (unknown.Count > MaxHeroesToOffer)
        {
            MessageBox.Show(this,
                $"В выбранных файлах — раздачи {unknown.Count} разных игроков, которых нет в списке «Мои ники»:\n\n{List(unknown.Take(8))}" +
                (unknown.Count > 8 ? "\n   …" : "") +
                "\n\nПохоже на общую выгрузку нескольких человек. Трекер принимает только собственные раздачи: " +
                "добавьте свои ники в список «Мои ники» вручную и повторите импорт.",
                "Импорт: чужие раздачи", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var answer = MessageBox.Show(this,
            $"В выбранных файлах есть раздачи, записанные клиентом игрока, которого нет в списке «Мои ники»:\n\n{List(unknown)}\n\n" +
            "Это ваши аккаунты?\n\n" +
            "«Да» — добавить их в «Мои ники» и загрузить эти раздачи.\n" +
            "«Нет» — оставить как есть: чужие раздачи трекер не принимает (правила румов).",
            "Импорт: чей это аккаунт?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return false;

        await Task.Run(async () =>
        {
            foreach (var u in unknown) await host.Heroes.AddForRoomAsync(u.Nickname, u.RoomCode);
            await host.ReloadAsync();
        });
        foreach (var u in unknown) _svc.Log("Import", $"Добавлен Hero: {u.Nickname} ({u.RoomCode})");
        await LoadHeroesAsync();
        return true;
    }

    /// <summary>Раздачи сети WPN без известного рума: спрашиваем, чей аккаунт и на каком руме сети.</summary>
    /// <returns>true — ник добавлен, импорт нужно повторить.</returns>
    private async Task<bool> OfferWpnRoomAsync(TrackerHost host, UnknownHero wpn)
    {
        var dlg = new Views.WpnRoomDialog(wpn, _svc.Settings.RoomCode) { Owner = this };
        if (dlg.ShowDialog() != true) return false;
        string room = dlg.RoomCode;
        await Task.Run(async () => { await host.Heroes.AddForRoomAsync(wpn.Nickname, room); await host.ReloadAsync(); });
        _svc.Log("Import", $"Добавлен Hero: {wpn.Nickname} ({room})");
        await LoadHeroesAsync();
        return true;
    }

    // ── Базы данных ────────────────────────────────────────────────────────

    private async Task LoadDatabasesAsync()
    {
        var cs = _svc.Settings.ConnectionString;
        try
        {
            var list = await Task.Run(() => DatabaseCatalog.ListAsync(cs));
            _dbListLoading = true;
            DbCombo.ItemsSource = list;
            DbCombo.SelectedItem = list.FirstOrDefault(d => string.Equals(d.Name, DatabaseCatalog.Current(cs), StringComparison.OrdinalIgnoreCase));
            if (DbActionText.Text.StartsWith("Сервер", StringComparison.Ordinal)) DbActionText.Text = "";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException or ArgumentException)
        {
            DbActionText.Text = "Сервер PostgreSQL недоступен: " + ex.Message;
        }
        finally { _dbListLoading = false; }
    }

    private async void DbCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_dbListLoading || DbCombo.SelectedItem is not DatabaseInfo db) return;
        if (string.Equals(db.Name, DatabaseCatalog.Current(_svc.Settings.ConnectionString), StringComparison.OrdinalIgnoreCase)) return;
        if (!db.IsTracker && MessageBox.Show(this,
                $"В базе «{db.Name}» нет таблиц PKTracker — похоже, это база другой программы.\n\nПодключиться и создать в ней таблицы трекера?",
                "База данных", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            await LoadDatabasesAsync(); // вернуть выбор на активную базу
            return;
        }
        await SwitchDatabaseAsync(db.Name);
    }

    /// <summary>Переключение — это смена имени базы в строке подключения и перезапуск подсистем, как после «Сохранить» в настройках.</summary>
    private async Task SwitchDatabaseAsync(string name)
    {
        _svc.Settings.ConnectionString = DatabaseCatalog.WithDatabase(_svc.Settings.ConnectionString, name);
        _svc.SaveSettings(_svc.Settings);
        SetConn.Text = _svc.Settings.ConnectionString;
        DbCombo.IsEnabled = false;
        DbActionText.Text = $"Подключение к базе «{name}»…";
        try { await _svc.StartAsync(); }
        finally { DbCombo.IsEnabled = true; }
        DbActionText.Text = _svc.Host == null ? "Не удалось подключиться: " + _svc.DbStatus
            : _svc.StaleContextHands > 0
                ? $"Активная база: «{name}». В ней {_svc.StaleContextHands:N0} раздач прежней версии программы — «Пересчитать… → Полный пересчёт»."
                : $"Активная база: «{name}».";
        ImportStatusText.Text = "";
        ImportProgressBar.Value = 0;
        await LoadDatabasesAsync();
        await LoadHeroesAsync();
        await LoadImportErrorsAsync();
    }

    private async void DbCreate_Click(object sender, RoutedEventArgs e)
    {
        if (!ImportButton.IsEnabled) return;
        var input = Views.PlayerMarkerMenu.Ask("Новая база данных",
            "Имя новой базы — латинские буквы, цифры и «_» (например, test или winamax_2026).\n" +
            "База создаётся пустой, трекер сразу переключится на неё. Прежняя база остаётся нетронутой — вернуться к ней можно в этом же списке.",
            "", multiline: false);
        if (input == null) return;
        var name = DatabaseCatalog.Normalize(input, out var error);
        if (error != null) { MessageBox.Show(this, error, "Новая база данных", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var cs = _svc.Settings.ConnectionString;
        try
        {
            DbActionText.Text = $"Создаю базу «{name}»…";
            await Task.Run(() => DatabaseCatalog.CreateAsync(cs, name));
            _svc.Log("Db", $"Создана база: {name}");
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or ArgumentException)
        {
            DbActionText.Text = "";
            MessageBox.Show(this, "Базу создать не удалось: " + ex.Message, "Новая база данных", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await SwitchDatabaseAsync(name);
    }

    private async void DbDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!ImportButton.IsEnabled || DbCombo.SelectedItem is not DatabaseInfo db) return;
        var others = DbCombo.Items.OfType<DatabaseInfo>().Where(d => d.Name != db.Name && d.IsTracker).ToList();
        if (!db.IsTracker)
        {
            MessageBox.Show(this, $"«{db.Name}» — не база PKTracker, удалять её отсюда нельзя.", "Удаление базы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (others.Count == 0)
        {
            MessageBox.Show(this, "Это единственная база трекера. Сначала создайте другую — приложению нужна база для работы.",
                "Удаление базы", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Куда переключиться после удаления: на базу по умолчанию, если она есть, иначе на первую по списку.
        var fallback = others.FirstOrDefault(d => d.Name == "pktracker") ?? others[0];
        var confirm = new Views.DeleteDatabaseDialog(db, fallback.Name, _svc.BackupFolder) { Owner = this };
        if (confirm.ShowDialog() != true) return;

        try
        {
            if (confirm.BackupFirst)
            {
                DbActionText.Text = $"Резервная копия базы «{db.Name}»…";
                var file = await _svc.BackupAsync();
                _svc.Log("Db", $"Копия перед удалением: {file}");
            }
            await SwitchDatabaseAsync(fallback.Name);
            if (_svc.Host == null) throw new InvalidOperationException($"не удалось переключиться на «{fallback.Name}», база «{db.Name}» не тронута");
            var cs = _svc.Settings.ConnectionString;
            DbActionText.Text = $"Удаляю базу «{db.Name}»…";
            await Task.Run(() => DatabaseCatalog.DropAsync(cs, db.Name));
            _svc.Log("Db", $"Удалена база: {db.Name}");
            await LoadDatabasesAsync();
            DbActionText.Text = $"База «{db.Name}» удалена. Активная база: «{fallback.Name}».";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            await LoadDatabasesAsync();
            DbActionText.Text = "";
            MessageBox.Show(this, "Базу удалить не удалось: " + ex.Message, "Удаление базы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
