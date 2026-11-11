using System.Globalization;
using System.Windows.Data;

namespace PKTracker.App.Infrastructure;

/// <summary>
/// Целое число с разделителем разрядов: «12 345». Отдельный конвертер нужен потому, что StringFormat в привязке
/// берёт культуру из свойства Language элемента (по умолчанию en-US) и пишет «12,345» — не так, как тот же счётчик
/// в соседней карточке, отформатированный в коде.
/// </summary>
public sealed class GroupedNumberConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IFormattable f ? f.ToString("N0", CultureInfo.CurrentCulture) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
