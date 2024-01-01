using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PKTracker.App.Infrastructure;

/// <summary>Цвет числа по знаку: выигрыш — зелёный, проигрыш — красный.</summary>
public sealed class SignToBrushConverter : IValueConverter
{
    public Brush Positive { get; set; } = Brushes.LightGreen;
    public Brush Negative { get; set; } = Brushes.IndianRed;
    public Brush Neutral { get; set; } = Brushes.Gainsboro;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string ?? value?.ToString() ?? "").Trim();
        if (s.Length == 0) return Neutral;
        if (s[0] == '-' || s[0] == '−') return Negative;
        if (s[0] == '+') return Positive;
        var digits = new string(s.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray()).Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? Positive : Neutral;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
