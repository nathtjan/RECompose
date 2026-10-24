// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RECompose.Core.Sections;
using RECompose.Core.Timing;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Controls;

/// <summary>
/// The timeline (UI-15, as written): a ruler in song bars, the sections lane (SEC-50) and one
/// lane per track with its waveform. Waveforms and sections are drawn in their own layer and
/// only redrawn when the song, the zoom or the lanes change; while playing only the thin
/// playhead layer redraws (PB-36, PB-90). Click or drag to seek (PB-26); Ctrl+wheel zooms,
/// Shift+wheel scrolls. In the sections lane: drag a boundary (Alt snaps to beats), click a
/// block to select it, double-click to rename, right-click for its menu.
/// </summary>
public sealed class TimelineView : Control
{
    public const double RulerHeight = 28;
    public const double SectionsHeight = 32;
    public const double LaneHeight = 56;
    public const double TracksTop = RulerHeight + SectionsHeight;
    private const double GrabPixels = 5;

    public static readonly StyledProperty<SongViewModel?> SongProperty =
        AvaloniaProperty.Register<TimelineView, SongViewModel?>(nameof(Song));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<TimelineView, double>(nameof(Position));

    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<TimelineView, int>(nameof(Version));

    public static readonly StyledProperty<bool> FollowProperty =
        AvaloniaProperty.Register<TimelineView, bool>(nameof(Follow), true);

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<TimelineView, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<int> SectionsVersionProperty =
        AvaloniaProperty.Register<TimelineView, int>(nameof(SectionsVersion));

    public static readonly StyledProperty<string?> SelectedSectionProperty =
        AvaloniaProperty.Register<TimelineView, string?>(nameof(SelectedSection));

    private readonly WaveLayer _waves;
    private readonly PlayheadLayer _playhead;
    private double? _pixelsPerSecond; // null = fit the whole song
    private double _offsetSec;
    private bool _dragging;
    private DateTime _lastScrub;
    private string? _boundaryDrag;   // the section whose start is being dragged
    private double _boundaryX;

    public TimelineView()
    {
        _waves = new WaveLayer(this);
        _playhead = new PlayheadLayer(this);
        VisualChildren.Add(_waves);
        VisualChildren.Add(_playhead);
        ClipToBounds = true;
        Focusable = false;
        ActualThemeVariantChanged += (_, _) => Redraw();
    }

    /// <summary>Raised with a source time when the user clicks or drags.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>A section start was dragged: (section id, source time, snap to beats).</summary>
    public event Action<string, double, bool>? SectionMoved;

    public event Action<string>? SectionSelected;
    public event Action<string>? SectionRenameRequested;
    public event Action<string>? SectionMenuRequested;

    public SongViewModel? Song { get => GetValue(SongProperty); set => SetValue(SongProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public bool Follow { get => GetValue(FollowProperty); set => SetValue(FollowProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public int SectionsVersion { get => GetValue(SectionsVersionProperty); set => SetValue(SectionsVersionProperty, value); }
    public string? SelectedSection { get => GetValue(SelectedSectionProperty); set => SetValue(SelectedSectionProperty, value); }

    internal string? DraggedSection => _boundaryDrag;
    internal double DragX => _boundaryX;

    internal double Duration => Math.Max(1, Song?.DurationSec ?? 1);
    internal double PixelsPerSecond => _pixelsPerSecond ?? Math.Max(1, Bounds.Width) / Duration;
    internal double OffsetSec => _pixelsPerSecond is null ? 0 : _offsetSec;

    internal double XAt(double seconds) => (seconds - OffsetSec) * PixelsPerSecond;
    internal double TimeAt(double x) => Math.Clamp(OffsetSec + x / PixelsPerSecond, 0, Duration);

    // ---- Zoom (KEY-1: F fit, + in, − out) ---------------------------------------------

    public void Fit()
    {
        _pixelsPerSecond = null;
        _offsetSec = 0;
        Redraw();
    }

    public void ZoomIn() => ZoomAround(Bounds.Width / 2, 1.5);
    public void ZoomOut() => ZoomAround(Bounds.Width / 2, 1 / 1.5);

    private void ZoomAround(double x, double factor)
    {
        var anchor = TimeAt(x);
        var fit = Math.Max(1, Bounds.Width) / Duration;
        var pps = Math.Clamp(PixelsPerSecond * factor, fit, 400);
        if (pps <= fit * 1.0001)
        {
            Fit();
            return;
        }

        _pixelsPerSecond = pps;
        _offsetSec = ClampOffset(anchor - x / pps);
        Redraw();
    }

    private double ClampOffset(double offset) =>
        Math.Clamp(offset, 0, Math.Max(0, Duration - Bounds.Width / PixelsPerSecond));

    // ---- Layout and redraw -------------------------------------------------------------

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = Song?.Tracks.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var size = new Size(width, TracksTop + lanes * LaneHeight);
        _waves.Measure(size);
        _playhead.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        _waves.Arrange(rect);
        _playhead.Arrange(rect);
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SongProperty)
        {
            _pixelsPerSecond = null;
            _offsetSec = 0;
            InvalidateMeasure();
            Redraw();
        }
        else if (change.Property == VersionProperty || change.Property == BoundsProperty
                 || change.Property == SectionsVersionProperty || change.Property == SelectedSectionProperty)
        {
            Redraw();
        }
        else if (change.Property == PositionProperty)
        {
            FollowPlayhead();
            _playhead.InvalidateVisual();
        }
    }

    private void Redraw()
    {
        _waves.InvalidateVisual();
        _playhead.InvalidateVisual();
    }

    private void FollowPlayhead()
    {
        if (!Follow || !IsPlaying || _pixelsPerSecond is null || _dragging)
            return;
        var x = XAt(Position);
        if (x < 0 || x > Bounds.Width * 0.9)
        {
            _offsetSec = ClampOffset(Position - Bounds.Width * 0.1 / PixelsPerSecond);
            _waves.InvalidateVisual();
        }
    }

    // ---- Input --------------------------------------------------------------------------

    private static bool InSectionsLane(Point p) => p.Y >= RulerHeight && p.Y < TracksTop;

    /// <summary>The section whose start is within grab distance of x.</summary>
    private SectionInfo? BoundaryAt(double x) =>
        Song?.Sections.Where(s => Math.Abs(XAt(s.StartSec) - x) <= GrabPixels).OrderBy(s => Math.Abs(XAt(s.StartSec) - x)).FirstOrDefault();

    private SectionInfo? SectionAt(double x)
    {
        var t = TimeAt(x);
        return Song?.Sections.LastOrDefault(s => s.StartSec <= t + 1e-6 && t < s.EndSec);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Song is null)
            return;
        var point = e.GetCurrentPoint(this);
        var p = point.Position;

        if (InSectionsLane(p))
        {
            if (point.Properties.IsRightButtonPressed && SectionAt(p.X) is { } menu)
            {
                SectionSelected?.Invoke(menu.Id);
                SectionMenuRequested?.Invoke(menu.Id);
                e.Handled = true;
                return;
            }

            if (point.Properties.IsLeftButtonPressed)
            {
                if (BoundaryAt(p.X) is { } boundary)
                {
                    _boundaryDrag = boundary.Id;
                    _boundaryX = p.X;
                    SectionSelected?.Invoke(boundary.Id);
                    e.Pointer.Capture(this);
                    _playhead.InvalidateVisual();
                    e.Handled = true;
                    return;
                }

                if (SectionAt(p.X) is { } section)
                {
                    SectionSelected?.Invoke(section.Id);
                    if (e.ClickCount >= 2)
                    {
                        SectionRenameRequested?.Invoke(section.Id);
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        e.Pointer.Capture(this);
        SeekRequested?.Invoke(TimeAt(p.X));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_boundaryDrag is not null)
        {
            _boundaryX = Math.Clamp(p.X, 0, Bounds.Width);
            _playhead.InvalidateVisual();
            return;
        }

        Cursor = InSectionsLane(p) && BoundaryAt(p.X) is not null ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        if (!_dragging || DateTime.UtcNow - _lastScrub < TimeSpan.FromMilliseconds(60))
            return;
        _lastScrub = DateTime.UtcNow;
        SeekRequested?.Invoke(TimeAt(p.X)); // scrub
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_boundaryDrag is { } id)
        {
            _boundaryDrag = null;
            e.Pointer.Capture(null);
            _playhead.InvalidateVisual();
            var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            SectionMoved?.Invoke(id, TimeAt(e.GetPosition(this).X), alt);
            return;
        }

        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        SeekRequested?.Invoke(TimeAt(e.GetPosition(this).X));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            ZoomAround(e.GetPosition(this).X, e.Delta.Y > 0 ? 1.25 : 0.8);
            e.Handled = true;
        }
        else if ((e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > 0) && _pixelsPerSecond is not null)
        {
            var delta = Math.Abs(e.Delta.X) > 0 ? e.Delta.X : e.Delta.Y;
            _offsetSec = ClampOffset(_offsetSec - delta * 60 / PixelsPerSecond);
            Redraw();
            e.Handled = true;
        }
    }

    // ---- Drawing helpers -------------------------------------------------------------

    internal IBrush Brush(string token) =>
        this.TryFindResource(token, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    internal Typeface MonoFace =>
        new(this.TryFindResource("FontMono", out var f) && f is FontFamily family ? family : FontFamily.Default);

    internal Typeface SansFace =>
        new(this.TryFindResource("FontSans", out var f) && f is FontFamily family ? family : FontFamily.Default);

    private sealed class WaveLayer(TimelineView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var width = Bounds.Width;
            context.FillRectangle(owner.Brush("bg-surface"), new Rect(Bounds.Size));
            var song = owner.Song;
            if (song is null || width <= 0)
                return;

            DrawRuler(context, song, width);
            DrawSections(context, song, width);
            DrawLanes(context, song, width);
        }

        private void DrawRuler(DrawingContext context, SongViewModel song, double width)
        {
            context.FillRectangle(owner.Brush("bg-app"), new Rect(0, 0, width, RulerHeight));
            context.DrawLine(new Pen(owner.Brush("border")), new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));

            var grid = song.Plan.SourceGrid; // song bars on the written clock, the same numbers as the readout (PB-32)
            var pxPerBar = grid.BarLength * owner.PixelsPerSecond;
            var step = 1;
            while (step * pxPerBar < 55 && step < 1024) // labels at least 55 px apart (UI-15)
                step *= 2;

            var tick = new Pen(owner.Brush("divider"));
            var labelBrush = owner.Brush("text-secondary");
            var face = owner.MonoFace;
            var first = Readouts.SongBar(grid, owner.OffsetSec).Bar - 1;
            var last = Readouts.SongBar(grid, owner.TimeAt(width)).Bar + 1;

            for (var bar = first; bar <= last; bar++)
            {
                var x = owner.XAt(grid.BarToTime(bar));
                if (x < -1 || x > width + 1 || grid.BarToTime(bar) < -1e-6)
                    continue;
                var labelled = bar >= 1 && (bar - 1) % step == 0;
                if (labelled || pxPerBar >= 6)
                    context.DrawLine(tick, new Point(Math.Round(x) + 0.5, labelled ? 14 : 20), new Point(Math.Round(x) + 0.5, RulerHeight));
                if (labelled)
                {
                    var text = new FormattedText(bar.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, labelBrush);
                    context.DrawText(text, new Point(x + 3, 2));
                }
            }
        }

        private void DrawSections(DrawingContext context, SongViewModel song, double width)
        {
            var top = RulerHeight;
            context.FillRectangle(owner.Brush("bg-inset"), new Rect(0, top, width, SectionsHeight));
            context.DrawLine(new Pen(owner.Brush("border")), new Point(0, TracksTop - 0.5), new Point(width, TracksTop - 0.5));

            if (song.Sections.Count == 0)
            {
                var hint = new FormattedText("No sections yet", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, owner.SansFace, 12, owner.Brush("text-secondary"));
                context.DrawText(hint, new Point(12, top + (SectionsHeight - hint.Height) / 2));
                return;
            }

            var textBrush = owner.Brush("text-on-section");
            var warning = owner.Brush("state-warning");
            var focus = new Pen(owner.Brush("focus-ring"), 2);
            foreach (var s in song.Sections)
            {
                var x0 = Math.Round(owner.XAt(s.StartSec));
                var x1 = Math.Round(owner.XAt(s.EndSec));
                if (x1 < 0 || x0 > width)
                    continue;
                var rect = new Rect(x0 + 1, top + 3, Math.Max(1, x1 - x0 - 2), SectionsHeight - 6);
                context.FillRectangle(owner.Brush(s.ColorToken), rect, 3);

                using (context.PushClip(rect))
                {
                    // The letters carry the meaning; the fill only repeats it (design system "Sections").
                    var text = new FormattedText(s.Badge is { } badge ? $"{badge}  {s.Label}" : $"◆  {s.Label}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, owner.SansFace, 12, textBrush);
                    var textX = Math.Max(rect.X + 6, 6);
                    if (s.Section.NeedsReview)
                    {
                        var mark = new StreamGeometry();
                        using (var g = mark.Open())
                        {
                            var cy = rect.Center.Y;
                            g.BeginFigure(new Point(textX, cy + 6), true);
                            g.LineTo(new Point(textX + 7, cy - 6));
                            g.LineTo(new Point(textX + 14, cy + 6));
                            g.EndFigure(true);
                        }
                        context.DrawGeometry(warning, null, mark);
                        textX += 18;
                    }
                    context.DrawText(text, new Point(textX, rect.Y + (rect.Height - text.Height) / 2));
                }

                if (s.Id == owner.SelectedSection)
                    context.DrawRectangle(null, focus, rect.Inflate(1), 3, 3);
            }
        }

        private void DrawLanes(DrawingContext context, SongViewModel song, double width)
        {
            var separator = new Pen(owner.Brush("divider"));
            var wave = owner.Brush("waveform");
            var muted = owner.Brush("waveform-muted");
            var anySolo = song.Tracks.Any(t => t.Solo && !t.IsClickOrGuide);
            var pps = owner.PixelsPerSecond;
            var offset = owner.OffsetSec;

            for (var i = 0; i < song.Tracks.Count; i++)
            {
                var track = song.Tracks[i];
                var top = TracksTop + i * LaneHeight;
                if (i % 2 == 1)
                    context.FillRectangle(owner.Brush("bg-app"), new Rect(0, top, width, LaneHeight));
                context.DrawLine(separator, new Point(0, top + LaneHeight - 0.5), new Point(width, top + LaneHeight - 0.5));

                if (track.Missing)
                {
                    var text = new FormattedText(track.Problem, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, owner.SansFace, 13, owner.Brush("state-error"));
                    context.DrawText(text, new Point(12, top + (LaneHeight - text.Height) / 2));
                    continue;
                }

                if (track.Peaks is not { Count: > 0 } peaks)
                    continue;

                var silent = track.Mute || (anySolo && !track.Solo && !track.IsClickOrGuide);
                var mid = top + LaneHeight / 2;
                var half = LaneHeight / 2 - 4;
                var columns = (int)Math.Ceiling(width);
                var tops = new double[columns];
                var bottoms = new double[columns];
                for (var x = 0; x < columns; x++)
                {
                    var t0 = offset + x / pps;
                    var (lo, hi) = peaks.Range(t0, t0 + 1 / pps);
                    tops[x] = mid - Math.Clamp(hi, 0, 1) * half - 0.5;
                    bottoms[x] = mid - Math.Clamp(lo, -1, 0) * half + 0.5;
                }

                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(0, tops[0]), true);
                    for (var x = 1; x < columns; x++)
                        g.LineTo(new Point(x, tops[x]));
                    for (var x = columns - 1; x >= 0; x--)
                        g.LineTo(new Point(x, bottoms[x]));
                    g.EndFigure(true);
                }

                context.DrawGeometry(silent ? muted : wave, null, geometry);
            }
        }
    }

    private sealed class PlayheadLayer(TimelineView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (owner.Song is null)
                return;

            if (owner.DraggedSection is not null)
            {
                var dx = Math.Round(owner.DragX) + 0.5;
                var dashed = new Pen(owner.Brush("focus-ring"), 2, new DashStyle([3, 2], 0));
                context.DrawLine(dashed, new Point(dx, RulerHeight), new Point(dx, Bounds.Height));
            }

            var x = Math.Round(owner.XAt(owner.Position)) + 0.5;
            if (x < 0 || x > Bounds.Width)
                return;

            var brush = owner.Brush("playhead");
            context.DrawLine(new Pen(brush, 1.5), new Point(x, 0), new Point(x, Bounds.Height));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(x - 5, 0), true);
                g.LineTo(new Point(x + 5, 0));
                g.LineTo(new Point(x, 7));
                g.EndFigure(true);
            }
            context.DrawGeometry(brush, null, head);
        }
    }
}
