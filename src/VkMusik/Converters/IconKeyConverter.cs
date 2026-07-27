using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VkMusik.Converters;

/// <summary>
/// Достаёт геометрию иконки из ресурсов по её имени: модели знают только строковый ключ
/// («IconVolumeHigh»), а рисовать надо готовой Geometry.
/// </summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static readonly IconKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        if (Application.Current is not { } app) return null;

        return app.TryGetResource(key, app.ActualThemeVariant, out var resource)
            ? resource as Geometry
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
