using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AetherPC.App.Converters;

/// <summary>
/// Compara CurrentKey (values[0]) con Tag del botón de nav (values[1]).
/// </summary>
public sealed class NavKeyMatchConverter : IMultiValueConverter
{
    public static readonly NavKeyMatchConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2)
            return false;
        if (values[0] is null || values[1] is null)
            return false;
        if (values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return false;
        if (values[0] == Binding.DoNothing || values[1] == Binding.DoNothing)
            return false;

        return string.Equals(
            values[0].ToString(),
            values[1].ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
