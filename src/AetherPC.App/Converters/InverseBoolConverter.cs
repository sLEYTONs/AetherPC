using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AetherPC.App.Converters;

/// <summary>Invierte bool. Si el target es Visibility, mapea a Visible/Collapsed.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var inverted = value is not bool b || !b;
        // Solo Visibility cuando el binding lo pide explícitamente (NO typeof(object):
        // WPF pasa object en IsEnabled y rompía los botones Aplicar/Analizar).
        if (targetType == typeof(Visibility))
            return inverted ? Visibility.Visible : Visibility.Collapsed;
        return inverted;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility v)
            return v != Visibility.Visible;
        return value is bool b && !b;
    }
}
