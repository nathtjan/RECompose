// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RECompose.Core.Planning;

namespace RECompose.App.Controls;

/// <summary>
/// The plan on one line for Perform (UI-40): a block per plan item on the effective clock (an open repeat is one block),
/// the playhead, a dashed marker where a pending action takes effect, and a tick at each hold. Bar numbers and the
/// playhead agree with the readouts because all of them come from the same plan (PB-32, PB-33). Clicking seeks, unless the
/// service lock is on.
/// </summary>
public sealed class MiniTimeline : Control
{
    public const double Height = 64;
    private const double LabelBand = 18;

    public static readonly StyledProperty<SongPlan?> PlanProperty = AvaloniaProperty.Register<MiniTimeline, SongPlan?>(nameof(Plan));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<MiniTimeline, double>(nameof(Position));
    public static readonly StyledProperty<double?> PendingAtProperty = AvaloniaProperty.Register<MiniTimeline, double?>(nameof(PendingAt));
    public static readonly StyledProperty<bool> LockedProperty = AvaloniaProperty.Register<MiniTimeline, bool>(nameof(Locked));

    static MiniTimeline()
    {
        AffectsRender<MiniTimeline>(PlanProperty, PositionProperty, PendingAtProperty);
    }

    public MiniTimeline()
    {
        MinHeight = Height;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public SongPlan? Plan { get => GetValue(PlanProperty); set => SetValue(PlanProperty, value); }

    /// <summary>The playhead, in plan seconds.</summary>
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    /// <summary>Where the pending action takes effect, in plan seconds.</summary>
    public double? PendingAt { get => GetValue(PendingAtProperty); set => SetValue(PendingAtProperty, value); }

    /// <summary>Service lock: the timeline does not seek (WF-11).</summary>
    public bool Locked { get => GetValue(LockedProperty); set => SetValue(LockedProperty, value); }

    /// <summary>The MD clicked a place: the plan second to seek to.</summary>
    public event Action<double>? SeekRequested;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, Height);

    private IBrush Brush(string token) =>
        this.TryFindResource(token, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Locked || Plan is not { IsEmpty: false } plan || Bounds.Width <= 0)
            return;
        var fraction = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);
        SeekRequested?.Invoke(plan.FromEffective(fraction * plan.ExpectedDurationSec));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Brush("bg-inset"), new Rect(0, 0, width, height), 4);
        if (Plan is not { IsEmpty: false } plan || width <= 0)
            return;

        var total = Math.Max(0.001, plan.ExpectedDurationSec);
        double X(double effectiveSec) => Math.Clamp(effectiveSec / total, 0, 1) * width;

        var typeface = new Typeface(this.TryFindResource("FontSans", out var f) && f is FontFamily family ? family : FontFamily.Default);
        var onSection = Brush("text-on-section");
        var neutral = Brush("text-secondary");
        var top = LabelBand;
        var blockHeight = height - LabelBand - 6;

        foreach (var item in plan.DisplayItems)
        {
            if (item.Length <= 0)
                continue;
            var x0 = X(item.PlanStart);
            var x1 = X(item.PlanStart + item.Length);
            var rect = new Rect(x0 + 1, top, Math.Max(1, x1 - x0 - 2), blockHeight);
            var isSection = item.Kind is PlanItemKind.Section or PlanItemKind.Song && item.Section is not null;
            context.FillRectangle(isSection ? Brush(item.Section!.ColorToken) : Brush("bg-raised"), rect, 3);

            if (rect.Width > 44)
            {
                var text = new FormattedText(item.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, isSection ? onSection : neutral);
                text.MaxTextWidth = rect.Width - 8;
                text.MaxTextHeight = blockHeight;
                text.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(text, new Point(rect.X + 5, rect.Y + (blockHeight - text.Height) / 2));
            }
        }

        // A tick at each hold, where the clock waits for GO.
        foreach (var hold in plan.Holds())
        {
            var x = X(plan.EffectiveSec(hold.AtSec));
            context.DrawLine(new Pen(Brush("state-hold"), 3), new Point(x, 2), new Point(x, top - 2));
        }

        // Song bar numbers every few bars along the top.
        var barLen = plan.SongGrid.BarLength;
        var bars = Math.Max(1, plan.TotalBars);
        var step = bars > 64 ? 16 : bars > 32 ? 8 : 4;
        for (var bar = 1; bar <= bars; bar += step)
        {
            var x = X(plan.P0 + (bar - 1) * barLen);
            var label = new FormattedText(bar.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, neutral);
            context.DrawText(label, new Point(x + 2, 2));
        }

        // The pending action (PB-45): a dashed blue marker where it takes effect.
        if (PendingAt is { } pending)
        {
            var x = X(plan.EffectiveSec(pending));
            var pen = new Pen(Brush("pending-marker"), 2, new DashStyle([3, 2], 0));
            context.DrawLine(pen, new Point(x, top - 4), new Point(x, height - 2));
        }

        // The playhead.
        var playhead = X(plan.EffectiveSec(Position));
        context.DrawLine(new Pen(Brush("accent"), 2), new Point(playhead, 0), new Point(playhead, height));
    }
}
