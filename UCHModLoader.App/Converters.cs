using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace UCHModLoader.App;

/// <summary>
/// Given the browse grid's bounds, returns a left margin that places a control
/// at the left edge of the rightmost card column. Cards are 196px wide with
/// 6px margins on each side (208px per column); the surrounding header row
/// already carries the same 6px inset as the cards.
/// </summary>
public sealed class RightmostColumnMarginConverter : IValueConverter
{
    private const double ColumnWidth = 208;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value switch
        {
            Rect rect => rect.Width,
            double d => d,
            _ => 0.0,
        };
        var columns = Math.Max(1, (int)(width / ColumnWidth));
        return new Thickness((columns - 1) * ColumnWidth, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
