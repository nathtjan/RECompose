// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Timing;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Controls;

/// <summary>
/// The timeline (UI-15). As written it shows the song: sections lane (SEC-50). As heard it shows the arrangement
/// on its own clock: one block per plan item (section, pass, lift, range), with the waveforms re-sequenced to match.
/// Entry gestures (ARR-24, AT-30): click selects, double-click opens ENTRY, right-click for the menu, drag to
/// reorder (Alt copies), drag an edge to trim the bars. Historical notes for the written view: a ruler in song bars, the sections lane (SEC-50) and one
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

    public static readonly StyledProperty<bool> AsHeardProperty =
        AvaloniaProperty.Register<TimelineView, bool>(nameof(AsHeard));

    public static readonly StyledProperty<string?> SelectedEntryProperty =
        AvaloniaProperty.Register<TimelineView, string?>(nameof(SelectedEntry));

    internal enum EntryDragMode { None, Move, TrimStart, TrimEnd }

    private sealed record EntrySpan(int Index, string Id, double Start, double End);

    private readonly WaveLayer _waves;
    private readonly PlayheadLayer _playhead;
    private double? _pixelsPerSecond; // null = fit the whole song
    private double _offsetSec;
    private bool _dragging;
    private DateTime _lastScrub;
    private string? _boundaryDrag;   // the section whose start is being dragged
    private double _boundaryX;
    private EntryDragMode _entryMode;
    private PlanItem? _entryItem;
    private double _entryDownX;
    private double _entryX;
    private bool _entryMoved;
    private bool _entryCopy;
    private string? _offsetTrack;    // the track whose waveform is being dragged sideways (Shift-drag)
    private double _offsetDownX;
    private double _offsetBase;      // its offset when the drag began
    private double _offsetShift;     // seconds dragged so far

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

    /// <summary>An arrangement entry was clicked (selects it).</summary>
    public event Action<string>? EntrySelected;

    /// <summary>An arrangement entry was double-clicked (open its options).</summary>
    public event Action<string>? EntryActivated;

    /// <summary>An entry was dragged to a 0-based position: (entry id, index, copy).</summary>
    public event Action<string, int, bool>? EntryMoved;

    /// <summary>An entry edge was dragged: (entry id, from bar, to bar).</summary>
    public event Action<string, int, int>? EntryTrimmed;

    public event Action<string>? EntryMenuRequested;

    /// <summary>A waveform was Shift-dragged sideways (as written): (track id, new offset in seconds).</summary>
    public event Action<string, double>? TrackOffsetDragged;

    public bool AsHeard { get => GetValue(AsHeardProperty); set => SetValue(AsHeardProperty, value); }
    public string? SelectedEntry { get => GetValue(SelectedEntryProperty); set => SetValue(SelectedEntryProperty, value); }

    public SongViewModel? Song { get => GetValue(SongProperty); set => SetValue(SongProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public bool Follow { get => GetValue(FollowProperty); set => SetValue(FollowProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public int SectionsVersion { get => GetValue(SectionsVersionProperty); set => SetValue(SectionsVersionProperty, value); }
    public string? SelectedSection { get => GetValue(SelectedSectionProperty); set => SetValue(SelectedSectionProperty, value); }

    internal string? DraggedSection => _boundaryDrag;
    internal double DragX => _boundaryX;

    internal bool Heard => AsHeard && Song is not null && Song.Plan.DisplayItems.Count > 0;

    internal double Duration => Math.Max(1, Heard ? Song!.Plan.ExpectedDurationSec : Song?.DurationSec ?? 1);

    /// <summary>The playhead on the timeline's clock: open repeats count once, so a vamp loops in place (PB-33).</summary>
    internal double ShownPosition => Heard ? Song!.Plan.EffectiveSec(Position) : Position;

    /// <summary>A click on the timeline as a plan time for the engine (the first pass of an open repeat).</summary>
    private double SeekTime(double x) => Heard ? Song!.Plan.FromEffective(TimeAt(x)) : TimeAt(x);

    internal PlanItem? DraggedItem => _entryMode == EntryDragMode.None || !_entryMoved ? null : _entryItem;
    internal EntryDragMode DragMode => _entryMode;
    internal double EntryDragX => _entryX;
    internal bool EntryCopy => _entryCopy;
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
            // The same song loaded again (a track was changed) keeps its zoom and scroll; another song starts fitted.
            var same = change.OldValue is SongViewModel before && change.NewValue is SongViewModel after && before.Id == after.Id;
            if (!same || _pixelsPerSecond is not { } pps || pps <= Math.Max(1, Bounds.Width) / Duration * 1.0001)
            {
                _pixelsPerSecond = null;
                _offsetSec = 0;
            }
            else
            {
                _offsetSec = ClampOffset(_offsetSec);
            }

            InvalidateMeasure();
            Redraw();
        }
        else if (change.Property == AsHeardProperty)
        {
            Fit();
        }
        else if (change.Property == SelectedEntryProperty || change.Property == VersionProperty || change.Property == BoundsProperty
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
        var x = XAt(ShownPosition);
        if (x < 0 || x > Bounds.Width * 0.9)
        {
            _offsetSec = ClampOffset(ShownPosition - Bounds.Width * 0.1 / PixelsPerSecond);
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

        if (Heard && InSectionsLane(p))
        {
            var item = EntryItemAt(p.X);
            if (item?.EntryId is { } entryId)
            {
                EntrySelected?.Invoke(entryId);
                if (point.Properties.IsRightButtonPressed)
                {
                    EntryMenuRequested?.Invoke(entryId);
                    e.Handled = true;
                    return;
                }

                if (point.Properties.IsLeftButtonPressed)
                {
                    if (e.ClickCount >= 2)
                    {
                        EntryActivated?.Invoke(entryId);
                        e.Handled = true;
                        return;
                    }

                    _entryItem = item;
                    _entryDownX = p.X;
                    _entryX = p.X;
                    _entryMoved = false;
                    _entryCopy = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                    var trimmable = item.Kind == PlanItemKind.Section && item.HasSource && item.LengthBars > 0;
                    _entryMode = trimmable && Math.Abs(XAt(item.PlanStart) - p.X) <= GrabPixels ? EntryDragMode.TrimStart
                        : trimmable && Math.Abs(XAt(item.PlanEnd) - p.X) <= GrabPixels ? EntryDragMode.TrimEnd
                        : EntryDragMode.Move;
                    e.Pointer.Capture(this);
                    _playhead.InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
        }
        else if (InSectionsLane(p))
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
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !Heard && p.Y >= TracksTop
            && (int)((p.Y - TracksTop) / LaneHeight) is var lane && lane < Song.Tracks.Count && !Song.Tracks[lane].Missing)
        {
            _offsetTrack = Song.Tracks[lane].Id;
            _offsetBase = Song.Tracks[lane].Track.OffsetSec;
            _offsetDownX = p.X;
            _offsetShift = 0;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        SeekRequested?.Invoke(SeekTime(p.X));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        if (_entryMode != EntryDragMode.None)
        {
            _entryX = Math.Clamp(p.X, 0, Bounds.Width);
            if (Math.Abs(_entryX - _entryDownX) > 4)
                _entryMoved = true;
            _entryCopy = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            _playhead.InvalidateVisual();
            return;
        }

        if (_offsetTrack is not null)
        {
            var limit = MainViewModel.MaxTrackOffsetSec;
            var wanted = _offsetBase + (p.X - _offsetDownX) / PixelsPerSecond;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                wanted = _offsetBase + Math.Round((wanted - _offsetBase) / Song!.Grid.BeatLength) * Song.Grid.BeatLength; // whole beats, as with sections
            else if (Math.Abs(wanted) * PixelsPerSecond < 4)
                wanted = 0; // close to the song start: lands exactly on it
            _offsetShift = Math.Clamp(wanted, -limit, limit) - _offsetBase;
            _waves.InvalidateVisual();
            UpdateOffsetCursor(e.KeyModifiers);
            return;
        }

        if (_boundaryDrag is not null)
        {
            _boundaryX = Math.Clamp(p.X, 0, Bounds.Width);
            _playhead.InvalidateVisual();
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && OverLane(p))
        {
            UpdateOffsetCursor(e.KeyModifiers);
            return;
        }

        if (Heard)
        {
            var edge = InSectionsLane(p) && EntryItemAt(p.X) is { Kind: PlanItemKind.Section, HasSource: true } hit
                       && (Math.Abs(XAt(hit.PlanStart) - p.X) <= GrabPixels || Math.Abs(XAt(hit.PlanEnd) - p.X) <= GrabPixels);
            Cursor = edge ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
            if (!_dragging || DateTime.UtcNow - _lastScrub < TimeSpan.FromMilliseconds(60))
                return;
            _lastScrub = DateTime.UtcNow;
            SeekRequested?.Invoke(SeekTime(p.X));
            return;
        }

        Cursor = InSectionsLane(p) && BoundaryAt(p.X) is not null ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        if (!_dragging || DateTime.UtcNow - _lastScrub < TimeSpan.FromMilliseconds(60))
            return;
        _lastScrub = DateTime.UtcNow;
        SeekRequested?.Invoke(SeekTime(p.X)); // scrub
    }

    // ---- The offset cursor: Shift over a lane, with a variant for Alt (whole beats) ----------------------------

    private Point? _pointer;
    private TopLevel? _top;
    private static Cursor? _beatCursor;

    private bool OverLane(Point p) => !Heard && Song is not null && p.Y >= TracksTop && (int)((p.Y - TracksTop) / LaneHeight) < Song.Tracks.Count;

    /// <summary>Left-right arrows; with Alt the arrows have a bar at each end, for the snap to beats.</summary>
    private void UpdateOffsetCursor(KeyModifiers modifiers) =>
        Cursor = modifiers.HasFlag(KeyModifiers.Alt) ? BeatCursor : new Cursor(StandardCursorType.SizeWestEast);

    // Black arrows on a white outline, as the system cursors are; a cursor has no theme.
    private static Cursor BeatCursor => _beatCursor ??= MakeBeatCursor();

    private static Cursor MakeBeatCursor()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            void Draw(IBrush brush, double width)
            {
                var pen = new Pen(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                context.DrawLine(pen, new Point(8, 16), new Point(24, 16));
                context.DrawLine(pen, new Point(8, 16), new Point(13, 11));
                context.DrawLine(pen, new Point(8, 16), new Point(13, 21));
                context.DrawLine(pen, new Point(24, 16), new Point(19, 11));
                context.DrawLine(pen, new Point(24, 16), new Point(19, 21));
                context.DrawLine(pen, new Point(4, 9), new Point(4, 23));
                context.DrawLine(pen, new Point(28, 9), new Point(28, 23));
            }

            Draw(Brushes.White, 4.5);
            Draw(Brushes.Black, 2);
        }

        return new Cursor(bitmap, new PixelPoint(16, 16));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnModifierKey, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _top?.AddHandler(KeyUpEvent, OnModifierKey, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnModifierKey);
        _top?.RemoveHandler(KeyUpEvent, OnModifierKey);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
    }

    /// <summary>Shift or Alt pressed or released with the pointer already still over a lane: the cursor follows at once.</summary>
    private void OnModifierKey(object? sender, KeyEventArgs e)
    {
        var down = e.RoutedEvent == KeyDownEvent;
        var modifiers = e.KeyModifiers;
        // The modifiers of the key event may not yet include the key itself.
        modifiers = e.Key switch
        {
            Key.LeftShift or Key.RightShift => down ? modifiers | KeyModifiers.Shift : modifiers & ~KeyModifiers.Shift,
            Key.LeftAlt or Key.RightAlt => down ? modifiers | KeyModifiers.Alt : modifiers & ~KeyModifiers.Alt,
            _ => modifiers,
        };
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt))
            return;

        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        if (_offsetTrack is not null || (shift && _pointer is { } p && OverLane(p)))
            UpdateOffsetCursor(modifiers);
        else if (!shift && _pointer is not null)
            Cursor = Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_entryMode != EntryDragMode.None)
        {
            var mode = _entryMode;
            var item = _entryItem;
            var moved = _entryMoved;
            var copy = _entryCopy;
            _entryMode = EntryDragMode.None;
            _entryItem = null;
            e.Pointer.Capture(null);
            _playhead.InvalidateVisual();
            if (item?.EntryId is { } entryId && moved)
            {
                var x = e.GetPosition(this).X;
                if (mode == EntryDragMode.Move)
                {
                    var spans = EntrySpans();
                    var mine = spans.FirstOrDefault(sp => sp.Id == entryId);
                    var t = TimeAt(x);
                    if (copy)
                    {
                        EntryMoved?.Invoke(entryId, spans.Count(sp => (sp.Start + sp.End) / 2 < t), true);
                    }
                    else if (mine is not null)
                    {
                        var index = spans.Count(sp => sp.Id != entryId && (sp.Start + sp.End) / 2 < t);
                        EntryMoved?.Invoke(entryId, index, false);
                    }
                }
                else if (TrimBars(item, mode, x) is { } trim)
                {
                    EntryTrimmed?.Invoke(entryId, trim.From, trim.To);
                }
            }

            return;
        }

        if (_offsetTrack is { } offsetTrack)
        {
            var offset = _offsetBase + _offsetShift;
            _offsetTrack = null;
            e.Pointer.Capture(null);
            _waves.InvalidateVisual();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && OverLane(e.GetPosition(this)))
                UpdateOffsetCursor(e.KeyModifiers);
            else
                Cursor = Cursor.Default;
            if (Math.Abs(_offsetShift) >= 0.0005)
                TrackOffsetDragged?.Invoke(offsetTrack, offset);
            return;
        }

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
        SeekRequested?.Invoke(SeekTime(e.GetPosition(this).X));
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

    // ---- Arrangement lane (as heard) ---------------------------------------------------------

    /// <summary>The plan item under x in the arrangement lane (items that belong to an entry only).</summary>
    private PlanItem? EntryItemAt(double x)
    {
        if (Song is null)
            return null;
        var t = TimeAt(x);
        var hit = Song.Plan.DisplayItems.LastOrDefault(i => i.EntryId is not null && i.PlanStart <= t + 1e-6 && t < Math.Max(i.PlanEnd, i.PlanStart + 6 / PixelsPerSecond));
        if (hit is not null)
            return hit;
        // Edges are grabbed from just outside the block as well.
        return Song.Plan.DisplayItems.Where(i => i.EntryId is not null && (Math.Abs(XAt(i.PlanStart) - x) <= GrabPixels || Math.Abs(XAt(i.PlanEnd) - x) <= GrabPixels))
            .OrderBy(i => Math.Abs(XAt(i.PlanEnd) - x)).FirstOrDefault();
    }

    /// <summary>Each entry as one span from its first to its last item.</summary>
    private List<EntrySpan> EntrySpans() =>
        Song is null
            ? []
            : Song.Plan.DisplayItems.Where(i => i.EntryId is not null)
                .GroupBy(i => i.EntryIndex)
                .OrderBy(g => g.Key)
                .Select(g => new EntrySpan(g.Key, g.First().EntryId!, g.Min(i => i.PlanStart), g.Max(i => i.PlanEnd)))
                .ToList();

    /// <summary>The bar range an edge drag would give, or null when the item cannot be trimmed.</summary>
    private (int From, int To)? TrimBars(PlanItem item, EntryDragMode mode, double x)
    {
        var size = TrimSize(item);
        if (size is null)
            return null;
        var (from, to, total, bar) = size.Value;
        var bars = (int)Math.Round((TimeAt(x) - item.PlanStart) / bar);
        if (mode == EntryDragMode.TrimStart)
            return (Math.Clamp(from + bars, 1, to), to);
        var end = (int)Math.Round((TimeAt(x) - item.PlanStart) / bar);
        return (from, Math.Clamp(from + end - 1, from, total));
    }

    private static (int From, int To, int Total, double Bar)? TrimSize(PlanItem item)
    {
        if (item.Section is not { } info || item.LengthBars <= 0 || item.Length <= 0)
            return null;
        return (item.FromBar, item.ToBar, Math.Max(info.LengthBars, item.ToBar), item.Length / item.LengthBars);
    }

    internal string? TrimLabel()
    {
        if (_entryItem is not { } item || !_entryMoved || _entryMode is not (EntryDragMode.TrimStart or EntryDragMode.TrimEnd))
            return null;
        if (TrimBars(item, _entryMode, _entryX) is not { } trim || TrimSize(item) is not { } size)
            return null;
        return string.Create(CultureInfo.InvariantCulture, $"bars {trim.From}–{trim.To} of {size.Total}");
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
            if (owner.Heard)
                DrawItems(context, song, width);
            else
                DrawSections(context, song, width);
            DrawCues(context, song, width);
            DrawLanes(context, song, width);
        }

        /// <summary>AUTO-4: ◆ and the name at every place the plan plays a cue's source time (as heard), or at that time (as written).</summary>
        private void DrawCues(DrawingContext context, SongViewModel song, double width)
        {
            var brush = owner.Brush("text-primary");
            foreach (var cue in song.Model.Cues.Where(c => c.Enabled))
            {
                var src = cue.FireSec(song.Grid);
                var times = owner.Heard
                    ? song.Plan.DisplayItems.Where(i => i.HasSource && i.Length > 0 && i.Kind != PlanItemKind.Tail && src >= i.SrcStart && src < i.SrcEnd)
                        .Select(i => i.PlanStart + (src - i.SrcStart))
                    : [src];
                foreach (var t in times)
                {
                    var x = owner.XAt(t);
                    if (x < -60 || x > width)
                        continue;
                    var text = new FormattedText($"◆ {cue.Name}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, owner.SansFace, 10, brush);
                    context.DrawLine(new Pen(brush), new Point(Math.Round(x) + 0.5, RulerHeight), new Point(Math.Round(x) + 0.5, RulerHeight + 4));
                    context.DrawText(text, new Point(x - 4, RulerHeight - text.Height));
                }
            }
        }

        private void DrawRuler(DrawingContext context, SongViewModel song, double width)
        {
            context.FillRectangle(owner.Brush("bg-app"), new Rect(0, 0, width, RulerHeight));
            context.DrawLine(new Pen(owner.Brush("border")), new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));

            var grid = owner.Heard ? song.Plan.SongGrid : song.Plan.SourceGrid; // song bars on the written clock, the same numbers as the readout (PB-32)
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

        private void DrawItems(DrawingContext context, SongViewModel song, double width)
        {
            var top = RulerHeight;
            context.FillRectangle(owner.Brush("bg-inset"), new Rect(0, top, width, SectionsHeight));
            context.DrawLine(new Pen(owner.Brush("border")), new Point(0, TracksTop - 0.5), new Point(width, TracksTop - 0.5));

            var onSection = owner.Brush("text-on-section");
            var neutralText = owner.Brush("text-secondary");
            var focus = new Pen(owner.Brush("focus-ring"), 2);
            foreach (var item in song.Plan.DisplayItems)
            {
                var x0 = Math.Round(owner.XAt(item.PlanStart));
                var x1 = Math.Round(owner.XAt(item.PlanEnd));
                if (x1 < 0 || x0 > width)
                    continue;
                var isHold = item.Kind == PlanItemKind.Hold;
                var rect = new Rect(x0 + 1, top + 3, Math.Max(isHold ? 6 : 1, x1 - x0 - 2), SectionsHeight - 6);
                var coloured = item.Section is not null;
                context.FillRectangle(owner.Brush(coloured ? item.Section!.ColorToken : isHold ? "state-hold" : "bg-raised"), rect, 3);
                if (!coloured)
                    context.DrawRectangle(null, new Pen(owner.Brush("border")), rect, 3, 3);

                using (context.PushClip(rect))
                {
                    var text = item.Label;
                    if (item.IsPartial)
                        text = item.RangeLabel;
                    if (item.Passes != 1)
                        text += item.IsOpen ? $"  {item.Pass}/?" : $"  {item.Pass}/{item.Passes}";
                    if (item.Transpose != 0)
                        text += $"  ({(item.Transpose > 0 ? "+" : "−")}{Math.Abs(item.Transpose)})";
                    if (item.MutedTrackIds.Count > 0 || item.MutedGroups.Count > 0)
                        text += "  muted";
                    if (!isHold)
                    {
                        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, owner.SansFace, 12, coloured ? onSection : neutralText);
                        context.DrawText(formatted, new Point(Math.Max(rect.X, 0) + 6, rect.Y + (rect.Height - formatted.Height) / 2));
                    }
                }

                if (item.EntryId is not null && item.EntryId == owner.SelectedEntry)
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
            var heard = owner.Heard;
            var columnItems = new PlanItem?[heard ? (int)Math.Ceiling(width) : 0];
            if (heard)
            {
                var items = song.Plan.DisplayItems;
                var k = 0;
                for (var x = 0; x < columnItems.Length; x++)
                {
                    var t = offset + (x + 0.5) / pps;
                    while (k + 1 < items.Count && items[k].PlanEnd <= t)
                        k++;
                    columnItems[x] = items[k].PlanStart <= t && t < items[k].PlanEnd + 1e-9 ? items[k] : null;
                }
            }

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
                var kind = new byte[columns]; // 0 nothing to play, 1 audio, 2 audio muted by the arrangement
                var lag = track.Track.OffsetSec + (track.Id == owner._offsetTrack ? owner._offsetShift : 0); // the file starts this long after the song start
                var fileEnd = peaks.Count * peaks.SecondsPerBucket;
                for (var x = 0; x < columns; x++)
                {
                    var t0 = offset + x / pps;
                    var t1 = t0 + 1 / pps;
                    var item = heard ? columnItems[x] : null;
                    if (heard)
                    {
                        if (item is null || !item.HasSource)
                        {
                            tops[x] = bottoms[x] = mid;
                            continue;
                        }

                        var s0 = item.SrcStart + (t0 - item.PlanStart);
                        var s1 = item.SrcStart + (Math.Min(t1, item.PlanEnd) - item.PlanStart);
                        if (s1 - lag <= 0 || s0 - lag >= fileEnd)
                        {
                            tops[x] = bottoms[x] = mid;
                            continue;
                        }

                        var (lo, hi) = peaks.Range(s0 - lag, Math.Max(s1, s0 + 1e-4) - lag);
                        tops[x] = mid - Math.Clamp(hi, 0, 1) * half - 0.5;
                        bottoms[x] = mid - Math.Clamp(lo, -1, 0) * half + 0.5;
                        kind[x] = item.MutedTrackIds.Contains(track.Id) ? (byte)2 : (byte)1;
                    }
                    else
                    {
                        if (t1 - lag <= 0 || t0 - lag >= fileEnd)
                        {
                            tops[x] = bottoms[x] = mid;
                            continue;
                        }

                        var (lo, hi) = peaks.Range(t0 - lag, t1 - lag);
                        tops[x] = mid - Math.Clamp(hi, 0, 1) * half - 0.5;
                        bottoms[x] = mid - Math.Clamp(lo, -1, 0) * half + 0.5;
                        kind[x] = 1;
                    }
                }

                // One figure per run of columns, so gaps and arrangement mutes stay separate shapes.
                var normal = new StreamGeometry();
                var dimmed = new StreamGeometry();
                using (var gn = normal.Open())
                using (var gd = dimmed.Open())
                {
                    var x = 0;
                    while (x < columns)
                    {
                        if (kind[x] == 0)
                        {
                            x++;
                            continue;
                        }

                        var runKind = kind[x];
                        var end = x;
                        while (end + 1 < columns && kind[end + 1] == runKind)
                            end++;
                        var g = runKind == 1 && !silent ? gn : gd;
                        g.BeginFigure(new Point(x, tops[x]), true);
                        for (var c = x + 1; c <= end; c++)
                            g.LineTo(new Point(c, tops[c]));
                        for (var c = end; c >= x; c--)
                            g.LineTo(new Point(c, bottoms[c]));
                        g.EndFigure(true);
                        x = end + 1;
                    }
                }

                context.DrawGeometry(wave, null, normal);
                context.DrawGeometry(muted, null, dimmed);

                if (heard)
                {
                    // Where nothing plays (a click or pad bar, a hold) the lane shows a flat line.
                    var flat = new Pen(owner.Brush("divider"));
                    for (var x = 0; x < columns; x++)
                    {
                        if (kind[x] != 0)
                            continue;
                        var end = x;
                        while (end + 1 < columns && kind[end + 1] == 0)
                            end++;
                        context.DrawLine(flat, new Point(x, mid), new Point(end + 1, mid));
                        x = end;
                    }
                }
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

            if (owner.DraggedItem is { } dragged)
            {
                var dashed = new Pen(owner.Brush("focus-ring"), 2, new DashStyle([3, 2], 0));
                var dx = Math.Round(owner.EntryDragX) + 0.5;
                context.DrawLine(dashed, new Point(dx, RulerHeight), new Point(dx, Bounds.Height));
                var label = owner.DragMode == EntryDragMode.Move
                    ? (owner.EntryCopy ? $"Copy {dragged.Label}" : $"Move {dragged.Label}")
                    : owner.TrimLabel();
                if (label is not null)
                {
                    var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, owner.SansFace, 12, owner.Brush("text-primary"));
                    var lx = Math.Clamp(dx + 6, 0, Math.Max(0, Bounds.Width - text.Width - 8));
                    context.DrawRectangle(owner.Brush("bg-raised"), new Pen(owner.Brush("border")), new Rect(lx - 4, RulerHeight + 4, text.Width + 8, text.Height + 4), 3, 3);
                    context.DrawText(text, new Point(lx, RulerHeight + 6));
                }
            }

            var x = Math.Round(owner.XAt(owner.ShownPosition)) + 0.5;
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
