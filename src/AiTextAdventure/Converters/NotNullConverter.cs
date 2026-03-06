using System.Globalization;

namespace AiTextAdventure.Converters;

/// <summary>Returns true if the value is not null. Used to show/hide elements based on optional data.</summary>
public class NotNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
