// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace RECompose.App.Controls;

/// <summary>Turns a token name from a view model ("section-chorus") into its brush in the current theme.</summary>
public sealed class TokenBrushConverter : IValueConverter
{
    public static TokenBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string token || Application.Current is not { } app)
            return Brushes.Transparent;
        return app.TryFindResource(token, app.ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
