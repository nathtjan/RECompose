// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using RECompose.App.Theme;

namespace RECompose.App.Controls;

/// <summary>
/// An icon from the RECompose set (design system: 24 px grid, 2 px stroke, round caps,
/// solid transport glyphs), drawn in the current foreground colour so text tokens tint it.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Glyph));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(IconSize), 16);

    private static readonly Dictionary<string, Geometry> Cache = [];

    static Icon()
    {
        AffectsRender<Icon>(GlyphProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(IconSizeProperty);
    }

    /// <summary>The icon's file name in design/icons without ".svg": "play", "stop".</summary>
    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>16 px in Prepare, 18 px on Perform keys, 12–14 px in pills and chips.</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    public override void Render(DrawingContext context)
    {
        if (Glyph is null || !IconData.All.TryGetValue(Glyph, out var shapes))
            return;

        var brush = GetValue(TextElement.ForegroundProperty) ?? Brushes.Gray;
        var scale = IconSize / 24.0;
        var pen = new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            foreach (var (data, filled) in shapes)
            {
                if (!Cache.TryGetValue(data, out var geometry))
                    Cache[data] = geometry = Geometry.Parse(data);
                context.DrawGeometry(filled ? brush : null, pen, geometry);
            }
        }
    }
}
