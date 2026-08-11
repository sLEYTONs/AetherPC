using System.Globalization;
using System.Windows.Data;

namespace AetherPC.App.Converters;

public sealed class ByteSizeConverter : IValueConverter
{
    public static readonly ByteSizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        var bytes = value switch
        {
            ulong u => u,
            long l when l >= 0 => (ulong)l,
            int i when i >= 0 => (ulong)i,
            double d when d >= 0 => (ulong)d,
            _ => 0UL
        };
        if (bytes == 0) return "—";
        if (bytes >= 1024UL * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        if (bytes >= 1024UL * 1024)
            return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / 1024.0:0.#} KB";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
