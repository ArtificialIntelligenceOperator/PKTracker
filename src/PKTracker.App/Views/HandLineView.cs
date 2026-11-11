using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PKTracker.Core.Model;
using PKTracker.Data;
using PKTracker.Engine;

namespace PKTracker.App.Views;

/// <summary>
/// Раздача одной строкой и «постфлоп-диаграмма» силы руки — в привычном по Hand2Note виде.
///
/// Строка раздачи: карты игрока цветными плашками (четырёхцветная колода), затем по улицам — борд, банк в bb
/// и действия буквами с размером: «R3 C2 · [A 9 2] 6 · X B3.4 C3.4». Действия игрока, по которому открыт
/// список, подчёркнуты; цвет букв различает игроков раздачи (один игрок — один цвет на всю строку).
/// </summary>
public static class HandLineView
{
    // Четырёхцветная колода: трефы зелёные, бубны синие, червы красные, пики серые.
    private static readonly Color[] Suits =
    {
        Color.FromRgb(0x1B, 0x8A, 0x3C), Color.FromRgb(0x1E, 0x63, 0xC8), Color.FromRgb(0xB0, 0x2A, 0x2A), Color.FromRgb(0x55, 0x55, 0x5C),
    };

    // Цвета игроков в строке: светлые тона, читаемые на тёмном фоне. Игрок в фокусе отмечен ещё и подчёркиванием.
    private static readonly Brush[] Actors =
    {
        Frozen(0xF2, 0xD3, 0x4B), Frozen(0x6A, 0xB8, 0xF0), Frozen(0xF0, 0x7A, 0x7A), Frozen(0x6B, 0xD6, 0x7A),
        Frozen(0xC3, 0xA6, 0xF0), Frozen(0xF0, 0xA6, 0x3C), Frozen(0x5F, 0xD6, 0xD0), Frozen(0xE0, 0xE0, 0xE0),
    };

    /// <summary>Цвет категории диаграммы силы: воздух — зелёные, дро — синие, слабые пары — фиолетовые, топ-пары — жёлтые, сильные — тёплые.</summary>
    private static readonly Color[] Category =
    {
        Color.FromRgb(0x2E, 0x9E, 0x4F), Color.FromRgb(0x6B, 0x7A, 0x70), Color.FromRgb(0x3F, 0xA7, 0xA0), Color.FromRgb(0x5B, 0x8C, 0x85),
        Color.FromRgb(0x3F, 0xA9, 0xE0), Color.FromRgb(0x2F, 0x7D, 0xD1), Color.FromRgb(0x4A, 0x5F, 0xD0),
        Color.FromRgb(0x7A, 0x5F, 0xC8), Color.FromRgb(0x8B, 0x4F, 0xC0), Color.FromRgb(0xA0, 0x4F, 0xB8),
        Color.FromRgb(0xD9, 0xB5, 0x26), Color.FromRgb(0xE0, 0x8A, 0x1E), Color.FromRgb(0xC0, 0x60, 0x3A),
        Color.FromRgb(0xC0, 0x45, 0x7A), Color.FromRgb(0xE0, 0x6A, 0x6A),
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static FrameworkElement CardChip(Card c) => new Border
    {
        Width = 18, Height = 20, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 2, 0),
        Background = new SolidColorBrush(Suits[c.Suit]),
        ToolTip = c.ToString(),
        Child = new TextBlock
        {
            Text = Card.Ranks[c.Rank].ToString(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static FrameworkElement Cards(IEnumerable<Card> cards, double width)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Width = width, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in cards) p.Children.Add(CardChip(c));
        return p;
    }

    private static FrameworkElement ActionsOf(LineStreet s, double width)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Width = width, VerticalAlignment = VerticalAlignment.Center };
        // Длинная улица (несколько рейзов в мультипоте) не должна ломать колонки: показываем начало и «+N».
        const int max = 5;
        foreach (var a in s.Actions.Take(max))
        {
            var t = new TextBlock
            {
                Text = a.Text, Margin = new Thickness(0, 0, 6, 0), FontSize = 12,
                Foreground = a.Text == "F" ? Res("TextDim") : Actors[a.Actor % Actors.Length],
                FontWeight = a.Focus ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (a.Focus)
                t.TextDecorations = new TextDecorationCollection
                {
                    new TextDecoration
                    {
                        Location = TextDecorationLocation.Underline, PenOffset = 2,
                        Pen = new Pen(t.Foreground, 1.5) { DashStyle = new DashStyle(new double[] { 2, 1.5 }, 0) },
                    },
                };
            p.Children.Add(t);
        }
        if (s.Actions.Count > max)
            p.Children.Add(new TextBlock { Text = $"+{s.Actions.Count - max}", Foreground = Res("TextDim"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    private static TextBlock Pot(double bb) => new()
    {
        Text = bb <= 0 ? "" : bb.ToString(bb < 10 ? "0.#" : "0", CultureInfo.InvariantCulture), Width = 30, Foreground = Res("Text"),
        FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Банк к началу улицы, bb",
    };

    /// <summary>Одна раздача строкой. <paramref name="showHole"/> = false — без карт игрока (стол рума со статичным HUD).</summary>
    public static FrameworkElement Row(HandLine line, bool showHole = true, bool withDate = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
        var hole = new List<Card>();
        if (showHole && line.Hole1 is { } a && line.Hole2 is { } b) { hole.Add(a); hole.Add(b); }
        row.Children.Add(Cards(hole, 46));
        for (int i = 0; i < 4; i++)
        {
            var st = i < line.Streets.Count ? line.Streets[i] : null;
            if (i > 0)
            {
                row.Children.Add(Cards(st?.Cards ?? Array.Empty<Card>(), i == 1 ? 64 : 24));
                row.Children.Add(Pot(st?.PotBb ?? 0));
            }
            row.Children.Add(st != null ? ActionsOf(st, i == 0 ? 104 : 118) : new Border { Width = i == 0 ? 104 : 118 });
        }
        row.Children.Add(new TextBlock
        {
            Text = (line.NetBb >= 0 ? "+" : "−") + Math.Abs(line.NetBb).ToString("0.#", CultureInfo.InvariantCulture), Width = 54,
            TextAlignment = TextAlignment.Right, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res(line.NetBb >= 0 ? "Positive" : "Negative"), ToolTip = "Итог раздачи для игрока, bb",
        });
        if (withDate)
            row.Children.Add(new TextBlock
            {
                Text = line.PlayedAt.ToLocalTime().ToString("dd.MM.yy HH:mm", CultureInfo.InvariantCulture), Width = 96, Margin = new Thickness(10, 0, 0, 0),
                Foreground = Res("TextDim"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Блайнды {line.Stakes}",
            });
        return row;
    }

    private static FrameworkElement HeaderRow(bool withDate)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        void H(string text, double w) => row.Children.Add(new TextBlock { Text = text, Width = w, Foreground = Res("TextDim"), FontSize = 11 });
        H("Карты", 46); H("Префлоп", 104); H("Флоп", 64); H("Банк", 30); H("Действия", 118); H("Т", 24); H("Банк", 30); H("Действия", 118);
        H("Р", 24); H("Банк", 30); H("Действия", 118); H("  Итог, bb", 54);
        if (withDate) H("   Дата", 106);
        return row;
    }

    /// <summary>Список раздач со строкой заголовков; двойной клик по раздаче — <paramref name="open"/> (реплеер).</summary>
    public static FrameworkElement List(IEnumerable<HandLine> lines, bool showHole, Action<long>? open, double maxHeight = 320)
    {
        var panel = new StackPanel();
        foreach (var line in lines)
        {
            var row = Row(line, showHole);
            if (open != null)
            {
                long pk = line.HandPk;
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { open(pk); e.Handled = true; } };
            }
            panel.Children.Add(row);
        }
        var root = new StackPanel();
        root.Children.Add(HeaderRow(true));
        root.Children.Add(new ScrollViewer { Content = panel, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    /// <summary>
    /// Диаграмма силы руки: категории в постоянном порядке, доля, полоса и «сколько из скольких».
    /// Наведение на категорию показывает сами раздачи этой категории. Название и число есть в каждой строке —
    /// цвет полосы только помогает глазу, смысл на нём не держится.
    /// </summary>
    public static FrameworkElement StrengthDiagram(IReadOnlyList<StrengthCat> cats, IReadOnlyDictionary<long, HandLine> lines, bool showHole)
    {
        var panel = new StackPanel { Width = 300 };
        long total = cats.Sum(c => c.Count);
        panel.Children.Add(new TextBlock
        {
            Text = $"Сила руки на улице · вскрытых рук: {total}", Foreground = Res("TextDim"), Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
        });

        // Сводная полоса: всё распределение одним взглядом; между сегментами зазор цвета фона.
        var stacked = new Grid { Height = 10, Margin = new Thickness(0, 0, 0, 8) };
        int col = 0;
        foreach (var c in cats.Where(c => c.Count > 0))
        {
            stacked.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Count, GridUnitType.Star) });
            var seg = new Border { Background = new SolidColorBrush(Category[c.Index % Category.Length]), Margin = new Thickness(0, 0, 1.5, 0), ToolTip = $"{c.Label}: {c.Pct:F0}%" };
            Grid.SetColumn(seg, col++);
            stacked.Children.Add(seg);
        }
        panel.Children.Add(stacked);

        double max = Math.Max(1, cats.Max(c => c.Pct));
        foreach (var c in cats)
        {
            var g = new Grid { Margin = new Thickness(0, 1.5, 0, 1.5), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            var ink = c.Count > 0 ? Res("Text") : Res("TextDim");
            g.Children.Add(new TextBlock { Text = c.Label, Foreground = ink, FontSize = 12 });
            var pct = new TextBlock { Text = $"{c.Pct:F0}%", Foreground = ink, FontSize = 12, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(pct, 1);
            g.Children.Add(pct);
            var track = new Grid { Height = 11, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { Background = Res("Bg2"), CornerRadius = new CornerRadius(2) });
            track.Children.Add(new Border
            {
                Background = new SolidColorBrush(Category[c.Index % Category.Length]), CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left, Width = c.Count == 0 ? 0 : Math.Max(3, 120 * c.Pct / max),
            });
            Grid.SetColumn(track, 2);
            g.Children.Add(track);
            var n = new TextBlock { Text = $"{c.Count}/{total}", Foreground = Res("TextDim"), FontSize = 11, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(n, 3);
            g.Children.Add(n);

            var examples = c.HandPks.Select(pk => lines.GetValueOrDefault(pk)).Where(l => l != null).Select(l => l!).ToList();
            if (examples.Count > 0)
            {
                var tip = new StackPanel();
                tip.Children.Add(new TextBlock { Text = $"{c.Label}: раздачи ({examples.Count} из {c.Count})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                foreach (var l in examples) tip.Children.Add(Row(l, showHole, withDate: false));
                g.ToolTip = new ToolTip { Content = tip, MaxWidth = 900 };
                ToolTipService.SetShowDuration(g, 60_000);
                ToolTipService.SetInitialShowDelay(g, 250);
                g.Cursor = Cursors.Help;
            }
            panel.Children.Add(g);
        }
        return panel;
    }
}
