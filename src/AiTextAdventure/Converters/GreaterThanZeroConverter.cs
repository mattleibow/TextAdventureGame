using System.Globalization;

namespace AiTextAdventure.Converters;

/// <summary>Returns true if a numeric value is greater than zero. Used to show list-empty states.</summary>
public class GreaterThanZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int i => i > 0,
            long l => l > 0,
            double d => d > 0,
            float f => f > 0,
            _ => false
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
