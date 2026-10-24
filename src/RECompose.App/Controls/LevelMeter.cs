// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RECompose.App.Controls;

/// <summary>
/// A vertical post-fader meter (PB-84). <see cref="Value"/> is 0…1 on the strip's
/// −60…+6 dB scale; the top 6 dB use meter-high, −18…0 dB meter-mid, the rest meter-low.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Value));

    // −18 dB and 0 dB on the −60…+6 dB scale.
    private const double MidFrom = 42.0 / 66.0;
    private const double HighFrom = 60.0 / 66.0;

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(ValueProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brush("bg-inset"), bounds, 2);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0)
            return;

        var h = bounds.Height;
        void Segment(double from, double to, string token)
        {
            var a = Math.Min(from, value);
            var b = Math.Min(to, value);
            if (b <= a)
                return;
            context.FillRectangle(Brush(token), new Rect(1, h - b * h, Math.Max(0, bounds.Width - 2), (b - a) * h));
        }

        Segment(0, MidFrom, "meter-low");
        Segment(MidFrom, HighFrom, "meter-mid");
        Segment(HighFrom, 1, "meter-high");
    }

    private IBrush Brush(string token) =>
        this.TryFindResource(token, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
