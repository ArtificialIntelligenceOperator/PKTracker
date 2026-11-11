using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.App.Hud;

/// <summary>
/// Редактор раскладок: слева вкладки, по центру сетка, справа свойства ячейки.
///
/// Сетка рисуется тем же <see cref="HudGridControl"/>, что панель и попап, — иначе в редакторе
/// раскладка выглядела бы не так, как на столе, и вся правка шла бы вслепую.
/// </summary>
public sealed class HudEditorWindow : Window
{
    private readonly HudProfileStore _store = new();
    private readonly IReadOnlyDictionary<string, CompiledStat> _stats;
    private readonly Action _onSaved;

    private HudProfile _profile;
    private readonly ListBox _pageList = new();
    private readonly HudGridControl _grid = new();
    private readonly StackPanel _props = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), Opacity = 0.75 };
    private readonly ComboBox _profileBox = new();

    /// <summary>Сколько строк списка занимают страницы панели: одна или вкладки P/B/C/V.</summary>
    private int PanelCount => Math.Max(1, _profile.PanelTabs.Count);
    private bool PanelSelected => _pageList.SelectedIndex >= 0 && _pageList.SelectedIndex < PanelCount;

    private HudPage CurrentPage => PanelSelected
        ? (_profile.PanelTabs.Count > 0 ? _profile.PanelTabs[_pageList.SelectedIndex] : _profile.Panel)
        : _profile.Pages[Math.Max(0, _pageList.SelectedIndex - PanelCount)];

    /// <summary>Подменить текущую страницу (страницы неизменяемы по свойствам сетки — заменяются целиком).</summary>
    private void ReplaceCurrentPage(HudPage fresh)
    {
        if (PanelSelected)
        {
            if (_profile.PanelTabs.Count > 0)
            {
                _profile.PanelTabs[_pageList.SelectedIndex] = fresh;
                if (_pageList.SelectedIndex == 0) _profile = WithPanel(_profile, fresh);
            }
            else _profile = WithPanel(_profile, fresh);
        }
        else _profile.Pages[_pageList.SelectedIndex - PanelCount] = fresh;
    }

    private static HudProfile WithPanel(HudProfile p, HudPage panel) => new()
    {
        Name = p.Name, GameKind = p.GameKind, Panel = panel, PanelTabs = p.PanelTabs, Pages = p.Pages, Version = p.Version,
    };

    /// <summary>Текущая ячейка; null — выбрано пустое место (координаты в <see cref="_newCell"/>).</summary>
    private HudCell? _selected;
    private (int Row, int Col)? _newCell;

    public HudEditorWindow(IReadOnlyDictionary<string, CompiledStat> stats, HudProfile profile, Action onSaved)
    {
        _stats = stats;
        _onSaved = onSaved;
        // Правим копию: встроенный профиль должен оставаться нетронутым, пока его не сохранили под своим именем.
        _profile = HudProfile.FromJson(profile.ToJson());

        Title = "Редактор HUD и попапов";
        Width = 1280;
        Height = 820;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x14, 0x24));
        Infrastructure.Win32.ApplyDarkFrame(this);
        Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8));
        FontFamily = new FontFamily("Segoe UI");

        _grid.EditMode = true;
        _grid.ShowGridLines2 = true;
        _grid.ShowTooltips = true;
        _grid.Stats = stats;
        _grid.CellClicked += OnCellClicked;

        Content = BuildLayout();
        ReloadPages();
        _pageList.SelectedIndex = 0;
    }

    // ── Каркас окна ──────────────────────────────────────────────────────────

    private UIElement BuildLayout()
    {
        var root = new DockPanel();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        toolbar.Children.Add(Label("Профиль:"));
        _profileBox.Width = 180;
        _profileBox.Margin = new Thickness(4, 0, 12, 0);
        ReloadProfileList();
        _profileBox.SelectionChanged += (_, _) => LoadSelectedProfile();
        toolbar.Children.Add(_profileBox);
        toolbar.Children.Add(Button("Сохранить", SaveProfile));
        toolbar.Children.Add(Button("Сохранить как…", SaveProfileAs));
        toolbar.Children.Add(Button("+ вкладка", AddPage));
        toolbar.Children.Add(Button("− вкладка", RemovePage));
        toolbar.Children.Add(Button("Вернуть встроенный", ResetProfile));
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _pageList.Width = 160;
        _pageList.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x1C, 0x30));
        _pageList.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8));
        _pageList.BorderThickness = new Thickness(0);
        _pageList.SelectionChanged += (_, _) => RenderPage();
        DockPanel.SetDock(_pageList, Dock.Left);
        root.Children.Add(_pageList);

        var propScroll = new ScrollViewer
        {
            Width = 300,
            Content = _props,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x1C, 0x30)),
            Padding = new Thickness(8),
        };
        DockPanel.SetDock(propScroll, Dock.Right);
        root.Children.Add(propScroll);

        root.Children.Add(new ScrollViewer
        {
            Content = _grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8),
        });
        return root;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8)),
    };

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3) };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ── Профили и вкладки ────────────────────────────────────────────────────

    private void ReloadProfileList()
    {
        _profileBox.Items.Clear();
        foreach (var p in DefaultProfiles.All) _profileBox.Items.Add("[встроенный] " + p.Name);
        foreach (var name in _store.List()) _profileBox.Items.Add(name);
        _profileBox.SelectedIndex = 0;
    }

    private void LoadSelectedProfile()
    {
        if (_profileBox.SelectedItem is not string item) return;
        HudProfile? loaded = item.StartsWith("[встроенный] ")
            ? DefaultProfiles.All.FirstOrDefault(p => p.Name == item["[встроенный] ".Length..])
            : _store.Load(item);
        if (loaded == null) return;
        _profile = HudProfile.FromJson(loaded.ToJson());
        ReloadPages();
        _pageList.SelectedIndex = 0;
        _status.Text = "Загружен профиль: " + _profile.Name;
    }

    private void ReloadPages()
    {
        int keep = _pageList.SelectedIndex;
        _pageList.Items.Clear();
        if (_profile.PanelTabs.Count > 0)
            foreach (var t in _profile.PanelTabs) _pageList.Items.Add("▣ Панель: " + t.Name);
        else _pageList.Items.Add("▣ Панель");
        foreach (var p in _profile.Pages) _pageList.Items.Add(HudProfile.PageKey(p));
        _pageList.SelectedIndex = Math.Clamp(keep, 0, _pageList.Items.Count - 1);
    }

    private void AddPage()
    {
        string name = Prompt("Имя новой вкладки", "NEW");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_profile.Page(name) != null)
        {
            _status.Text = "Вкладка с таким именем уже есть";
            return;
        }
        // «Группа/Вкладка» — вкладка попадёт в группу слева в попапе (как группы в Hand2Note).
        int slash = name.IndexOf('/');
        if (slash >= 0 && name[(slash + 1)..].Trim().Length == 0)
        {
            _status.Text = "После «Группа/» нужно имя вкладки";
            return;
        }
        _profile.Pages.Add(slash > 0
            ? new HudPage { Name = name[(slash + 1)..].Trim(), Group = name[..slash].Trim() }
            : new HudPage { Name = name });
        ReloadPages();
        _pageList.SelectedIndex = PanelCount + _profile.Pages.Count - 1;
    }

    private void RemovePage()
    {
        if (PanelSelected)
        {
            _status.Text = "Панель удалить нельзя — она есть всегда";
            return;
        }
        int i = _pageList.SelectedIndex - PanelCount;
        if (MessageBox.Show($"Удалить вкладку «{_profile.Pages[i].Name}»?", "Редактор HUD",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profile.Pages.RemoveAt(i);
        ReloadPages();
    }

    private void SaveProfile()
    {
        if (DefaultProfiles.All.Any(p => p.Name == _profile.Name))
        {
            // Встроенный профиль не перезаписываем: иначе обновление программы молча потеряет правки.
            SaveProfileAs();
            return;
        }
        _store.Save(_profile);
        _status.Text = $"Сохранено: {_profile.Name}";
        ReloadProfileList();
        _onSaved();
    }

    private void SaveProfileAs()
    {
        string name = Prompt("Имя профиля", _profile.Name.StartsWith("MTT") ? "Мой MTT" : _profile.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _profile = new HudProfile
        {
            Name = name, GameKind = _profile.GameKind, Panel = _profile.Panel, PanelTabs = _profile.PanelTabs,
            Pages = _profile.Pages, Version = _profile.Version,
        };
        _store.Save(_profile);
        _status.Text = $"Сохранено: {name}. Выберите профиль в настройках HUD, чтобы он применился.";
        ReloadProfileList();
        _onSaved();
    }

    private void ResetProfile()
    {
        var builtin = _profile.GameKind == "cash" ? DefaultProfiles.Cash : DefaultProfiles.Mtt;
        if (MessageBox.Show("Заменить текущую раскладку встроенной? Несохранённые правки пропадут.", "Редактор HUD",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _profile = HudProfile.FromJson(builtin.ToJson());
        ReloadPages();
        RenderPage();
    }

    // ── Сетка ────────────────────────────────────────────────────────────────

    private void RenderPage()
    {
        if (_pageList.SelectedIndex < 0) return;
        _selected = null;
        _newCell = null;
        _grid.Render(CurrentPage, null);
        _grid.Select(-1);
        BuildProperties();
    }

    private void OnCellClicked(int index, int row, int col)
    {
        if (index >= 0)
        {
            _selected = CurrentPage.Cells[index];
            _newCell = null;
            _grid.Select(index);
        }
        else
        {
            _selected = null;
            _newCell = (row, col);
            _grid.Select(-1);
        }
        BuildProperties();
    }

    private void Replace(HudCell old, HudCell fresh)
    {
        var cells = CurrentPage.Cells;
        int i = cells.IndexOf(old);
        if (i < 0) return;
        cells[i] = fresh;
        _selected = fresh;
        _grid.Render(CurrentPage, null);
        _grid.Select(i);
    }

    // ── Свойства ячейки ──────────────────────────────────────────────────────

    private void BuildProperties()
    {
        _props.Children.Clear();
        if (_newCell is { } np)
        {
            _props.Children.Add(Head($"Пустая клетка ({np.Row}, {np.Col})"));
            _props.Children.Add(Button("Добавить подпись", () => AddCell(text: "Текст")));
            _props.Children.Add(Button("Добавить стат", () => AddCell(stat: _stats.Keys.FirstOrDefault())));
            return;
        }
        if (_selected is not { } cell)
        {
            _props.Children.Add(Head("Ячейка не выбрана"));
            _props.Children.Add(new TextBlock
            {
                Text = "Щёлкните по ячейке, чтобы её править, или по пустой клетке, чтобы добавить новую.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8)),
            });
            _props.Children.Add(Head("Страница"));
            _props.Children.Add(Field("Ширина колонки", CurrentPage.ColWidth.ToString("0.#"), v => SetPage(colWidth: Num(v))));
            _props.Children.Add(Field("Высота строки", CurrentPage.RowHeight.ToString("0.#"), v => SetPage(rowHeight: Num(v))));
            _props.Children.Add(Field("Размер шрифта", CurrentPage.FontSize.ToString("0.#"), v => SetPage(fontSize: Num(v))));
            return;
        }

        // Каждая правка заменяет объект ячейки новым (Replace), а поля панели свойств созданы один раз:
        // они обращаются к текущей ячейке через Cur(), иначе вторая правка подряд молча терялась бы.
        HudCell Cur() => _selected ?? cell;
        _props.Children.Add(Head($"Ячейка ({cell.Row}, {cell.Col})"));
        _props.Children.Add(Field("Текст", cell.Text ?? "", v => Replace(Cur(), Clone(Cur(), text: Empty(v)))));
        _props.Children.Add(StatPicker(cell));
        _props.Children.Add(Field("Строка", cell.Row.ToString(), v => Replace(Cur(), Clone(Cur(), row: (int)(Num(v) ?? Cur().Row)))));
        _props.Children.Add(Field("Колонка", cell.Col.ToString(), v => Replace(Cur(), Clone(Cur(), col: (int)(Num(v) ?? Cur().Col)))));
        _props.Children.Add(Field("Объединить строк", cell.RowSpan.ToString(), v => Replace(Cur(), Clone(Cur(), rowSpan: (int)(Num(v) ?? 1)))));
        _props.Children.Add(Field("Объединить колонок", cell.ColSpan.ToString(), v => Replace(Cur(), Clone(Cur(), colSpan: (int)(Num(v) ?? 1)))));
        _props.Children.Add(Field("Фон #RRGGBB", cell.Style?.Bg ?? "", v => Replace(Cur(), Clone(Cur(), style: Style(Cur(), bg: Empty(v))))));
        _props.Children.Add(Field("Цвет текста", cell.Style?.Fg ?? "", v => Replace(Cur(), Clone(Cur(), style: Style(Cur(), fg: Empty(v))))));
        _props.Children.Add(Check("Жирный", cell.Style?.Bold ?? false, v => Replace(Cur(), Clone(Cur(), style: Style(Cur(), bold: v)))));
        _props.Children.Add(Field("Рамка #RRGGBB", cell.Style?.Border ?? "", v => Replace(Cur(), Clone(Cur(), style: StyleBorder(Cur(), Empty(v))))));
        _props.Children.Add(Check("Показывать выборку (n)", cell.ShowSample, v => Replace(Cur(), Clone(Cur(), showSample: v))));
        _props.Children.Add(Field("Знаков после запятой", cell.Decimals?.ToString() ?? "", v => Replace(Cur(), Clone(Cur(), decimals: (int?)Num(v)))));
        _props.Children.Add(Field("Мин. выборка", cell.MinSample?.ToString() ?? "", v => Replace(Cur(), Clone(Cur(), minSample: (int?)Num(v)))));
        if (PanelSelected)
        {
            // Привязка — выбором из списка вкладок попапа: опечатка в имени тихо отвязала бы ячейку.
            _props.Children.Add(Label("Открывать вкладку"));
            var box = new ComboBox { Margin = new Thickness(0, 0, 0, 6), IsEditable = false };
            box.Items.Add("(нет)");
            foreach (var pg in _profile.Pages) box.Items.Add(HudProfile.PageKey(pg));
            box.SelectedItem = cell.Popup != null && _profile.Page(cell.Popup) is { } bound ? HudProfile.PageKey(bound) : "(нет)";
            box.SelectionChanged += (_, _) =>
            {
                var v = box.SelectedItem as string;
                var fresh = Clone(Cur(), popup: v == "(нет)" ? "" : v);
                Replace(Cur(), v == "(нет)" ? ClearPopup(fresh) : fresh);
            };
            _props.Children.Add(box);
            // Правило панели: без привязанной вкладки ячейка за столом не реагирует ни на клик,
            // ни на наведение. Пустое поле здесь выглядит как «ещё не заполнил», поэтому говорим прямо.
            _props.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(cell.Popup)
                    ? "Вкладка не привязана: за столом эта ячейка ничего не открывает."
                    : $"За столом ячейка открывает вкладку «{cell.Popup}».",
                Foreground = string.IsNullOrEmpty(cell.Popup) ? Brushes.Goldenrod : Brushes.Gray,
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
            });
        }

        _props.Children.Add(Head("Фильтр"));
        var f = cell.Filter ?? new CellFilter();
        _props.Children.Add(Field("Позиции (через запятую)", Join(f.Pos), v => SetFilter(Cur(), pos: Split(v))));
        _props.Children.Add(Field("Против позиций", Join(f.VsPos), v => SetFilter(Cur(), vs: Split(v))));
        _props.Children.Add(Field("Позиция опенрейзера", Join(f.OpenPos), v => SetFilter(Cur(), open: Split(v))));
        _props.Children.Add(Field("Игроков за столом от", f.PlayersMin?.ToString() ?? "", v => SetFilter(Cur(), pMin: (int?)Num(v))));
        _props.Children.Add(Field("Игроков за столом до", f.PlayersMax?.ToString() ?? "", v => SetFilter(Cur(), pMax: (int?)Num(v))));
        _props.Children.Add(Field("Игроков в раздаче от", f.InHandMin?.ToString() ?? "", v => SetFilter(Cur(), hMin: (int?)Num(v))));
        _props.Children.Add(Field("Игроков в раздаче до", f.InHandMax?.ToString() ?? "", v => SetFilter(Cur(), hMax: (int?)Num(v))));
        _props.Children.Add(Field("Стек от, bb", f.StackMin?.ToString() ?? "", v => SetFilter(Cur(), sMin: Num(v))));
        _props.Children.Add(Field("Стек до, bb", f.StackMax?.ToString() ?? "", v => SetFilter(Cur(), sMax: Num(v))));
        _props.Children.Add(Tri("Олл-ин", f.AllIn, v => SetFilter(Cur(), allIn: v)));
        _props.Children.Add(Tri("Анте", f.Ante, v => SetFilter(Cur(), ante: v)));
        _props.Children.Add(Field("Условие (язык статов)", f.Where ?? "", v => SetFilter(Cur(), where: Empty(v))));

        if (PanelSelected && cell.Filter is { } cf && !cf.AggCompatible)
            _props.Children.Add(Warn("Панель читает агрегаты: позиция оппонента, олл-ин, анте и произвольные "
                + "условия там не работают. Для панели заведите отдельный стат или уберите эти поля."));

        _props.Children.Add(Button("Удалить ячейку", () =>
        {
            CurrentPage.Cells.Remove(Cur());
            RenderPage();
        }));
    }

    private void AddCell(string? text = null, string? stat = null)
    {
        if (_newCell is not { } p) return;
        var cell = new HudCell { Row = p.Row, Col = p.Col, Text = text, Stat = stat };
        CurrentPage.Cells.Add(cell);
        _selected = cell;
        _newCell = null;
        _grid.Render(CurrentPage, null);
        _grid.Select(CurrentPage.Cells.Count - 1);
        BuildProperties();
    }

    private void SetPage(double? colWidth = null, double? rowHeight = null, double? fontSize = null)
    {
        var p = CurrentPage;
        var fresh = new HudPage
        {
            Name = p.Name, Cells = p.Cells, ColWidth = colWidth ?? p.ColWidth, RowHeight = rowHeight ?? p.RowHeight,
            ColWidths = p.ColWidths, FontSize = fontSize ?? p.FontSize, Bg = p.Bg, Group = p.Group, Note = p.Note,
        };
        ReplaceCurrentPage(fresh);
        _grid.Render(CurrentPage, null);
    }

    private void SetFilter(HudCell cell, string[]? pos = null, string[]? vs = null, string[]? open = null,
        int? pMin = null, int? pMax = null, int? hMin = null, int? hMax = null, double? sMin = null, double? sMax = null,
        bool? allIn = null, bool? ante = null, string? where = null, bool clearWhere = false)
    {
        var f = cell.Filter ?? new CellFilter();
        var fresh = new CellFilter
        {
            Pos = pos ?? f.Pos, VsPos = vs ?? f.VsPos, OpenPos = open ?? f.OpenPos,
            PlayersMin = pMin ?? f.PlayersMin, PlayersMax = pMax ?? f.PlayersMax,
            InHandMin = hMin ?? f.InHandMin, InHandMax = hMax ?? f.InHandMax,
            StackMin = sMin ?? f.StackMin, StackMax = sMax ?? f.StackMax,
            ToBbMin = f.ToBbMin, ToBbMax = f.ToBbMax,
            AllIn = allIn ?? f.AllIn, Ante = ante ?? f.Ante, Street = f.Street,
            Where = clearWhere ? null : where ?? f.Where,
        };
        Replace(cell, Clone(cell, filter: fresh.IsEmpty ? null : fresh));
    }

    private static HudCell Clone(HudCell c, int? row = null, int? col = null, int? rowSpan = null, int? colSpan = null,
        string? text = null, string? stat = null, CellFilter? filter = null, CellStyle? style = null,
        int? decimals = null, int? minSample = null, bool? showSample = null, string? popup = null) => new()
    {
        Row = row ?? c.Row, Col = col ?? c.Col, RowSpan = rowSpan ?? c.RowSpan, ColSpan = colSpan ?? c.ColSpan,
        Text = text ?? c.Text, Stat = stat ?? c.Stat, Filter = filter ?? c.Filter, Colors = c.Colors,
        Style = style ?? c.Style, Decimals = decimals ?? c.Decimals, MinSample = minSample ?? c.MinSample,
        ShowSample = showSample ?? c.ShowSample, Popup = popup ?? c.Popup,
    };

    private static HudCell ClearPopup(HudCell c) => new()
    {
        Row = c.Row, Col = c.Col, RowSpan = c.RowSpan, ColSpan = c.ColSpan, Text = c.Text, Stat = c.Stat, Filter = c.Filter,
        Colors = c.Colors, Style = c.Style, Decimals = c.Decimals, MinSample = c.MinSample, ShowSample = c.ShowSample, Popup = null,
    };

    private static CellStyle Style(HudCell c, string? bg = null, string? fg = null, bool? bold = null) => new()
    {
        Bg = bg ?? c.Style?.Bg, Fg = fg ?? c.Style?.Fg, Bold = bold ?? c.Style?.Bold ?? false,
        Align = c.Style?.Align, FontSize = c.Style?.FontSize, Border = c.Style?.Border,
    };

    private static CellStyle StyleBorder(HudCell c, string? border) => new()
    {
        Bg = c.Style?.Bg, Fg = c.Style?.Fg, Bold = c.Style?.Bold ?? false, Align = c.Style?.Align, FontSize = c.Style?.FontSize,
        Border = border,
    };

    // ── Мелкие элементы ──────────────────────────────────────────────────────

    private static TextBlock Head(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4),
        Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
    };

    private static TextBlock Warn(string text) => new()
    {
        Text = "⚠ " + text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
    };

    private static UIElement Field(string label, string value, Action<string> onChange)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Opacity = 0.75,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8)),
        });
        var box = new TextBox { Text = value };
        // Правка применяется по потере фокуса и по Enter: на каждое нажатие клавиши
        // перерисовывать сетку — значит терять фокус на первом же символе.
        box.LostFocus += (_, _) => onChange(box.Text);
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) onChange(box.Text); };
        panel.Children.Add(box);
        return panel;
    }

    private static UIElement Check(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8)),
        };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    /// <summary>Тройной переключатель: «не важно» / «да» / «нет».</summary>
    private static UIElement Tri(string label, bool? value, Action<bool?> onChange)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Opacity = 0.75,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8)),
        });
        var box = new ComboBox();
        box.Items.Add("не важно");
        box.Items.Add("да");
        box.Items.Add("нет");
        box.SelectedIndex = value == null ? 0 : value.Value ? 1 : 2;
        box.SelectionChanged += (_, _) => onChange(box.SelectedIndex switch { 1 => true, 2 => false, _ => null });
        panel.Children.Add(box);
        return panel;
    }

    private UIElement StatPicker(HudCell cell)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "Стат (пусто — подпись)", FontSize = 11, Opacity = 0.75,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8)),
        });
        var box = new ComboBox { IsEditable = true, Text = cell.Stat ?? "" };
        box.Items.Add("");
        foreach (var code in _stats.Keys.OrderBy(c => c)) box.Items.Add(code);
        box.LostFocus += (_, _) => Apply(box.Text);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) Apply(s); };
        panel.Children.Add(box);
        if (cell.Stat != null && _stats.TryGetValue(cell.Stat, out var st))
            panel.Children.Add(new TextBlock
            {
                Text = st.Definition.Title + " — " + (st.Definition.Opportunity ?? st.Definition.Hit ?? ""),
                TextWrapping = TextWrapping.Wrap, FontSize = 10, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF0, 0xF8)),
            });
        return panel;

        void Apply(string code)
        {
            string? fresh = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
            if (fresh == cell.Stat) return;
            var cells = CurrentPage.Cells;
            int i = cells.IndexOf(cell);
            if (i < 0) return;
            // Стат снимается только через явную замену объекта: Clone не умеет писать null поверх значения.
            cells[i] = new HudCell
            {
                Row = cell.Row, Col = cell.Col, RowSpan = cell.RowSpan, ColSpan = cell.ColSpan, Text = cell.Text,
                Stat = fresh, Filter = cell.Filter, Colors = cell.Colors, Style = cell.Style, Decimals = cell.Decimals,
                MinSample = cell.MinSample, ShowSample = cell.ShowSample, Popup = cell.Popup,
            };
            _selected = cells[i];
            _grid.Render(CurrentPage, null);
            _grid.Select(i);
            BuildProperties();
        }
    }

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static double? Num(string s) => double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Any,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string Join(string[]? a) => a == null ? "" : string.Join(", ", a);
    private static string[]? Split(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Prompt(string title, string initial)
    {
        var dlg = new Window
        {
            Title = title, Width = 340, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(10) };
        var ok = new Button { Content = "ОК", Width = 80, Margin = new Thickness(10), IsDefault = true };
        string result = "";
        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(ok);
        dlg.Content = panel;
        dlg.ShowDialog();
        return result;
    }
}
