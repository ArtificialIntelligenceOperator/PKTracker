using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PKTracker.Engine.Hud;
using PKTracker.Engine.Stats;

namespace PKTracker.App.Hud;

/// <summary>
/// Рисует страницу раскладки: сетка ячеек со своими цветами и значениями.
///
/// Один контрол на три места — панель над столом, вкладка попапа и превью редактора. Иначе
/// раскладка выглядела бы в редакторе не так, как на столе, и редактор терял бы смысл.
/// </summary>
public sealed class HudGridControl : Grid
{
    private static readonly Brush DefaultFg = new SolidColorBrush(Color.FromRgb(0xEF, 0xE6, 0xDA));
    private static readonly Brush GridLine = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly Brush EmptyCellBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    private HudPage? _page;
    private CellValue[]? _values;
    private readonly Dictionary<int, Border> _byIndex = new();

    /// <summary>Режим редактора: пустые клетки кликабельны, выделение видно, значения — заглушки.</summary>
    public bool EditMode { get; set; }
    /// <summary>Показывать сетку и координаты — только в редакторе.</summary>
    public bool ShowGridLines2 { get; set; }
    public IReadOnlyDictionary<string, CompiledStat> Stats { get; set; } = new Dictionary<string, CompiledStat>();
    public bool ShowTooltips { get; set; } = true;

    /// <summary>Индекс ячейки в <see cref="HudPage.Cells"/>; -1 — кликнули по пустому месту.</summary>
    public event Action<int, int, int>? CellClicked;
    public event Action<int>? CellRightClicked;

    /// <summary>
    /// Курсор зашёл на ячейку со статом и ушёл с неё. Вместе с индексом отдаётся сам элемент:
    /// попап внутри окна прицепляется к ячейке, и без неё его неоткуда разместить.
    /// </summary>
    public event Action<int, FrameworkElement>? CellEnter;
    public event Action<int>? CellLeave;

    public int SelectedIndex { get; private set; } = -1;

    public void Select(int index)
    {
        if (SelectedIndex == index) return;
        if (SelectedIndex >= 0 && _byIndex.TryGetValue(SelectedIndex, out var old)) ApplyBorder(old, false);
        SelectedIndex = index;
        if (index >= 0 && _byIndex.TryGetValue(index, out var now)) ApplyBorder(now, true);
    }

    private void ApplyBorder(Border b, bool selected)
    {
        b.BorderBrush = selected ? SelectionBrush : (ShowGridLines2 ? GridLine : Brushes.Transparent);
        b.BorderThickness = new Thickness(selected ? 2 : (ShowGridLines2 ? 0.5 : 0));
    }

    /// <summary>Перерисовать страницу. Значения могут быть null — тогда во всех ячейках прочерк.</summary>
    public void Render(HudPage page, CellValue[]? values)
    {
        _page = page;
        _values = values;
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        _byIndex.Clear();

        // В редакторе оставляем запас строк и колонок, чтобы было куда класть новые ячейки.
        int cols = Math.Max(page.Cols, 1) + (EditMode ? 3 : 0);
        int rows = Math.Max(page.Rows, 1) + (EditMode ? 3 : 0);
        for (int c = 0; c < cols; c++)
            ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(page.ColWidths?.GetValueOrDefault(c, page.ColWidth) ?? page.ColWidth, GridUnitType.Pixel),
            });
        for (int r = 0; r < rows; r++)
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(page.RowHeight, GridUnitType.Pixel) });

        if (EditMode)
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    AddPlaceholder(r, c);

        for (int i = 0; i < page.Cells.Count; i++)
            AddCell(i, page.Cells[i], values != null && i < values.Length ? values[i] : CellValue.Empty, page);

        if (SelectedIndex >= 0 && _byIndex.TryGetValue(SelectedIndex, out var sel)) ApplyBorder(sel, true);
    }

    private void AddPlaceholder(int row, int col)
    {
        var border = new Border
        {
            Background = EmptyCellBrush,
            BorderBrush = GridLine,
            BorderThickness = new Thickness(0.5),
            Margin = new Thickness(0.5),
        };
        SetRow(border, row);
        SetColumn(border, col);
        int r = row, c = col;
        border.MouseLeftButtonUp += (_, e) => { CellClicked?.Invoke(-1, r, c); e.Handled = true; };
        Children.Add(border);
    }

    private void AddCell(int index, HudCell cell, CellValue value, HudPage page)
    {
        var stat = cell.Stat != null && Stats.TryGetValue(cell.Stat, out var s) ? s : null;
        // В редакторе настоящих значений нет — рисуем «99», чтобы видеть ширину и цвет ячейки.
        var shown = EditMode && !cell.IsLabel ? new CellValue(99, 99, 99) : value;
        var r = CellRender.Render(cell, shown, stat);

        var text = new TextBlock
        {
            Text = r.Text,
            Foreground = Brush(r.Fg) ?? DefaultFg,
            FontWeight = r.Bold ? FontWeights.Bold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = r.Align switch { "right" => TextAlignment.Right, "left" => TextAlignment.Left, _ => TextAlignment.Center },
            HorizontalAlignment = r.Align switch
            {
                "right" => HorizontalAlignment.Right, "left" => HorizontalAlignment.Left, _ => HorizontalAlignment.Center,
            },
            Margin = new Thickness(3, 0, 3, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            // Малая выборка — шум: приглушаем, а не прячем (§5.3).
            Opacity = r.LowSample ? 0.45 : 1,
        };
        if (r.FontSize is { } fs) text.FontSize = fs;
        else text.FontSize = page.FontSize;

        var border = new Border
        {
            Background = Brush(r.Bg) ?? Brushes.Transparent,
            Margin = new Thickness(0.5),
            Child = text,
            Cursor = cell.IsLabel ? Cursors.Arrow : Cursors.Hand,
            ToolTip = ShowTooltips && r.Tooltip.Length > 0 ? r.Tooltip : null,
        };
        ApplyBorder(border, false);
        SetRow(border, cell.Row);
        SetColumn(border, cell.Col);
        SetRowSpan(border, Math.Max(1, cell.RowSpan));
        SetColumnSpan(border, Math.Max(1, cell.ColSpan));
        int i = index, row = cell.Row, col = cell.Col;
        border.MouseLeftButtonUp += (_, e) => { CellClicked?.Invoke(i, row, col); e.Handled = true; };
        border.MouseRightButtonUp += (_, e) => { CellRightClicked?.Invoke(i); e.Handled = true; };
        // Подписи наведение не отслеживают: у них нет стата, показывать по ним нечего.
        if (!cell.IsLabel)
        {
            border.MouseEnter += (_, _) => CellEnter?.Invoke(i, border);
            border.MouseLeave += (_, _) => CellLeave?.Invoke(i);
        }
        _byIndex[index] = border;
        Children.Add(border);
    }

    private static Brush? Brush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch (FormatException) { return null; }
    }

    /// <summary>Размер страницы в DIP — панели нужен, чтобы позиционировать себя над местом.</summary>
    public (double W, double H) Size()
    {
        if (_page == null) return (0, 0);
        double w = 0;
        for (int c = 0; c < ColumnDefinitions.Count; c++) w += ColumnDefinitions[c].Width.Value;
        return (w, RowDefinitions.Count * _page.RowHeight);
    }
}
