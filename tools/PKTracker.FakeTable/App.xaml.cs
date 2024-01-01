using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PKTracker.FakeTable;

/// <summary>
/// Симулятор столов для проверки HUD без реального клиента (Фаза 0, спайк ⑤).
/// Аргументы: "Имя стола:мест:место Hero[:x,y]". Места подписаны номерами HH, Hero — снизу, как в клиенте.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var specs = e.Args.Length > 0 ? e.Args : new[] { "Demorest:6:3" };
        int i = 0;
        foreach (var spec in specs)
        {
            var parts = spec.Split(':');
            int max = parts.Length > 1 ? int.Parse(parts[1]) : 6;
            int hero = parts.Length > 2 ? int.Parse(parts[2]) : 1;
            var w = new Window
            {
                Title = parts[0],
                Width = 820, Height = 600,
                Left = 40 + i * 60, Top = 40 + i * 50,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18)),
                Content = Table(max, hero),
            };
            if (parts.Length > 3)
            {
                var xy = parts[3].Split(',');
                w.Left = double.Parse(xy[0]);
                w.Top = double.Parse(xy[1]);
            }
            w.Show();
            i++;
        }
    }

    private static UIElement Table(int max, int hero)
    {
        var canvas = new Canvas { Width = 800, Height = 560 };
        var felt = new Ellipse { Width = 560, Height = 300, Fill = new SolidColorBrush(Color.FromRgb(0x1F, 0x5C, 0x3A)) };
        Canvas.SetLeft(felt, 120); Canvas.SetTop(felt, 110);
        canvas.Children.Add(felt);
        for (int seat = 1; seat <= max; seat++)
        {
            int v = ((seat - hero) % max + max) % max;
            double angle = Math.PI / 2 + v * 2 * Math.PI / max;
            double cx = 400 + 320 * Math.Cos(angle), cy = 263 + 207 * Math.Sin(angle);
            var label = new Border
            {
                Width = 90, Height = 26, CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(seat == hero ? Color.FromRgb(0x8A, 0x5E, 0x38) : Color.FromRgb(0x33, 0x33, 0x33)),
                Child = new TextBlock
                {
                    Text = seat == hero ? $"Seat {seat} (Hero)" : $"Seat {seat}", Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(label, cx - 45); Canvas.SetTop(label, cy - 13);
            canvas.Children.Add(label);
        }
        return new Viewbox { Child = canvas, Stretch = Stretch.Fill };
    }
}
