using System.Globalization;

namespace AiTextAdventure.Converters;

/// <summary>
/// Returns the active (gold) color when value is true, and the inactive (dim) color when false.
/// Used for tab button highlighting in the sidebar.
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Color.FromArgb("#D4A017") : Color.FromArgb("#505070");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
