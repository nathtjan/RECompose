// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Arrangements;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Planning;

/// <summary>What a plan item is (PB-40).</summary>
public enum PlanItemKind
{
    /// <summary>The whole file, for a song without sections.</summary>
    Song,
    LeadIn,

    /// <summary>Generated click bars before the first section (SL-36).</summary>
    CountIn,
    Section,
    Click,
    Pad,
    Hold,
    Tail,
}

/// <summary>One playable piece of the plan (PB-40): where it comes from in the source and where it sits in plan time.</summary>
public sealed record PlanItem
{
    public required string Id { get; init; }
    public required PlanItemKind Kind { get; init; }

    /// <summary>The section label (SEC-41), "Click", "Pad", "Hold", "Ending" (the tail) or "Lead-in".</summary>
    public required string Label { get; init; }

    public string? EntryId { get; init; }

    /// <summary>Position of the arrangement entry in its list, or −1.</summary>
    public int EntryIndex { get; init; } = -1;

    public SectionInfo? Section { get; init; }

    /// <summary>1-based pass of a repeated entry.</summary>
    public int Pass { get; init; } = 1;

    /// <summary>Passes in total; 0 means until cued (open).</summary>
    public int Passes { get; init; } = 1;

    public bool HasSource { get; init; }
    public double SrcStart { get; init; }
    public double SrcEnd { get; init; }
    public double PlanStart { get; init; }
    public double PlanEnd { get; init; }
    public int LengthBars { get; init; }
    public int FromBar { get; init; } = 1;
    public int ToBar { get; init; }

    /// <summary>Plays part of the section (ARR-31).</summary>
    public bool IsPartial { get; init; }

    /// <summary>The key lift of the arrangement entry (ARR-33).</summary>
    public int Transpose { get; init; }

    /// <summary>Semitones the transposing tracks play at: the entry key plus the lift (PB-41 step 3, keyTotal).</summary>
    public int KeyTotal { get; init; }

    /// <summary>The pitch class heard (original key + <see cref="KeyTotal"/>), for pads that follow the song (SL-44); null when the key is unknown.</summary>
    public int? SongPitchClass { get; init; }

    /// <summary>Pad bars: the pitch class of the pad (ARR-39); null when no key is known.</summary>
    public int? PadPitchClass { get; init; }

    public IReadOnlyList<string> MutedTrackIds { get; init; } = [];
    public IReadOnlyList<string> MutedGroups { get; init; } = [];
    public double FadeInSec { get; init; }
    public double FadeOutSec { get; init; }

    /// <summary>The fade-out the entry asks for on its last pass; a live change that ends a repeat moves it to the pass that is last then.</summary>
    public double NominalFadeOutSec { get; init; }
    public string? Note { get; init; }
    public EndingMode Ending { get; init; }

    /// <summary>Hold: the pad fades in while it waits (ARR-38, PB-52).</summary>
    public bool HoldPad { get; init; }

    /// <summary>Hold: the click keeps running at the song tempo while it waits.</summary>
    public bool HoldClick { get; init; }

    /// <summary>The count-in that starts when GO releases a hold (PB-52).</summary>
    public bool AfterHold { get; init; }

    /// <summary>Added while playing (a vamp pass, a repeat once, a jump target) rather than by the arrangement (PB-40 origin).</summary>
    public bool IsLive { get; init; }

    public double Length => PlanEnd - PlanStart;
    public bool IsOpen => Passes == 0;

    /// <summary>The click generator runs in this item: a count-in or click bars (PB-70).</summary>
    public bool HasClick => Kind is PlanItemKind.CountIn or PlanItemKind.Click;

    /// <summary>"Chorus 1", "Chorus 1 · 1–8" (ARR-31).</summary>
    public string RangeLabel => IsPartial ? $"{Label} · {FromBar.ToString(CultureInfo.InvariantCulture)}–{ToBar.ToString(CultureInfo.InvariantCulture)}" : Label;
}

/// <summary>A piece of the plan as the engine plays it: contiguous in plan seconds, with an optional source start (null = silence).</summary>
public sealed record PlanSegment(double Start, double End, double? Src, double FadeIn, double FadeOut, IReadOnlyList<string> Mute)
{
    /// <summary>Semitones for the tracks that transpose (PB-58).</summary>
    public int Key { get; init; }

    /// <summary>The pitch class heard, for pads with Follow song (SL-44); null when unknown.</summary>
    public int? SongKey { get; init; }

    /// <summary>The click generator runs on the plan's grid (PB-70).</summary>
    public bool Click { get; init; }

    /// <summary>Pad bars: the pitch class the pad plays (ARR-39).</summary>
    public int? Pad { get; init; }
}

/// <summary>The song grid in plan seconds, for the engine's click (PB-70): bar 1 beat 1, the beat length, beats per bar.</summary>
public sealed record PlanGrid(double Origin, double Beat, int BeatsPerBar);

/// <summary>
/// Where the plan clock waits for GO (PB-52). <paramref name="AtSec"/> is the plan second; the pad, if any, plays the pitch
/// class of the item before it; the click runs at the song tempo.
/// </summary>
public sealed record PlanHold(double AtSec, bool Pad, int? PadKey, bool Click);

/// <summary>What the header shows at a position (PB-30…35, LIVE-20 rule 4, LIVE-10).</summary>
public sealed record PlanReadout(
    string NowLabel,
    string NowDetail,
    string? NextLabel,
    string NextTag,
    string Countdown,
    string SongBar,
    string Time,
    string ChartBar,
    double SectionProgress,
    string NowPass = "",
    string NowNote = "",
    string NextNote = "");

/// <summary>
/// The arrangement expanded into what will be heard (PB-40): an ordered, gap-free list of items in
/// plan time. Plan time 0 is the start of the first item; source time is the position in the audio
/// files. Song bars count from the first section item (PB-32).
/// </summary>
public sealed class SongPlan
{
    private const double ToleranceSec = 0.002;

    public SongPlan(TempoGrid chartGrid, IReadOnlyList<SectionInfo> sections, IReadOnlyList<PlanItem> items, double sourceDurationSec, bool isAsWritten, double tempoFactor = 1)
    {
        TempoFactor = tempoFactor > 0 && double.IsFinite(tempoFactor) ? tempoFactor : 1;
        ChartGrid = chartGrid;
        Sections = sections;
        Items = items;
        SourceDurationSec = Math.Max(0, sourceDurationSec);
        IsAsWritten = isAsWritten;
        DurationSec = items.Count > 0 ? items[^1].PlanEnd : 0;

        var first = items.FirstOrDefault(i => i.Kind is PlanItemKind.Section or PlanItemKind.Song);
        // Without sections the plan is the whole file and bar 1 is the first downbeat, as in milestone 1.
        P0 = first is null ? 0 : first.Kind == PlanItemKind.Song ? chartGrid.FirstDownbeatSec : first.PlanStart;
        SongGrid = chartGrid.WithOrigin(P0);
        SourceGrid = chartGrid.WithOrigin(sections.Count > 0 ? sections.Min(x => x.StartSec) : chartGrid.FirstDownbeatSec);

        // PB-33: an open repeat counts as one pass in every total; its further passes are only built so the engine can play them.
        ExtraOpenSec = items.Where(i => i.IsOpen && i.Pass > 1).Sum(i => i.Length);
        ExpectedDurationSec = DurationSec - ExtraOpenSec;

        var lastBody = items.LastOrDefault(i => i.Kind != PlanItemKind.Tail);
        TotalBars = first is null || lastBody is null
            ? 0
            : Math.Max(1, (int)Math.Floor((lastBody.PlanEnd - ExtraOpenSec - P0) / SongGrid.BarLength + ToleranceSec / SongGrid.BarLength));
        HasOpenEnd = items.Any(i => i.IsOpen || i.Kind == PlanItemKind.Hold);

        // SL-30: the transition starts where the last section ends and the tail begins.
        var lastSection = items.LastOrDefault(i => i.Kind is PlanItemKind.Section or PlanItemKind.Song);
        TransitionPointSec = lastSection is not null && items[^1].Kind == PlanItemKind.Tail ? lastSection.PlanEnd : DurationSec;
    }

    /// <summary>The entry tempo as a factor (PB-30 tf). Plan times stay in source-second units; only clock times divide by it.</summary>
    public double TempoFactor { get; }

    /// <summary>The plan's announced length as heard at the entry tempo, open repeats counted once (PB-33, ARR-80).</summary>
    public double HeardDurationSec => ExpectedDurationSec / TempoFactor;

    /// <summary>Plan seconds the further passes of open repeats add; not part of the announced length.</summary>
    public double ExtraOpenSec { get; }

    /// <summary>The plan length with open repeats counted once.</summary>
    public double ExpectedDurationSec { get; }

    /// <summary>Plan time where the transition to the next entry starts: the start of the tail, or the end of the plan (SL-30).</summary>
    public double TransitionPointSec { get; }

    public TempoGrid ChartGrid { get; }

    /// <summary>The grid with bar 1 at the first section item: the ruler and the song bar use it (PB-32).</summary>
    public TempoGrid SongGrid { get; }

    /// <summary>The same bars on the source clock (bar 1 at the first written section): the as-written timeline's ruler.</summary>
    public TempoGrid SourceGrid { get; }

    public IReadOnlyList<SectionInfo> Sections { get; }
    public IReadOnlyList<PlanItem> Items { get; }

    /// <summary>Length of the plan, tail included.</summary>
    public double DurationSec { get; }

    public double SourceDurationSec { get; }

    /// <summary>Plan time of the first section (S0 in plan time).</summary>
    public double P0 { get; }

    /// <summary>Song bars from S0 to the end of the last non-tail item (PB-32).</summary>
    public int TotalBars { get; }

    /// <summary>True when the plan is the written sections in order with no options (PB-42).</summary>
    public bool IsAsWritten { get; }

    public bool HasSections => Sections.Count > 0;
    public bool IsEmpty => Items.Count == 0;
    public bool HasOpenEnd { get; }

    // ---- Time conversion --------------------------------------------------------------

    /// <summary>The item playing at <paramref name="planSec"/>: the last one that started, skipping zero-length items.</summary>
    public int ItemIndexAt(double planSec)
    {
        var index = -1;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item.Length <= 0)
                continue;
            if (index < 0 || item.PlanStart <= planSec + ToleranceSec)
                index = i;
            else
                break;
        }

        return Math.Max(0, index);
    }

    /// <summary>Source time at a plan position, or null when it falls in click or pad bars.</summary>
    public double? SourceAt(double planSec)
    {
        if (Items.Count == 0)
            return null;
        var item = Items[ItemIndexAt(planSec)];
        if (!item.HasSource)
            return null;
        return Math.Clamp(item.SrcStart + (planSec - item.PlanStart), item.SrcStart, item.SrcEnd);
    }

    /// <summary>
    /// Where a source time is played: the first occurrence at or after <paramref name="notBeforePlanSec"/>,
    /// otherwise the first one. Null when the arrangement doesn't play it.
    /// </summary>
    public double? PlanAt(double sourceSec, double notBeforePlanSec = 0)
    {
        double? first = null;
        foreach (var item in Items)
        {
            if (!item.HasSource || item.Length <= 0)
                continue;
            if (sourceSec < item.SrcStart - ToleranceSec || sourceSec > item.SrcEnd + ToleranceSec)
                continue;
            var plan = item.PlanStart + Math.Clamp(sourceSec - item.SrcStart, 0, item.Length);
            first ??= plan;
            if (plan >= notBeforePlanSec - ToleranceSec)
                return plan;
        }

        return first;
    }

    /// <summary>The song grid the engine's click follows: song bars count from S0 (PB-32).</summary>
    public PlanGrid Grid => new(P0, SongGrid.BeatLength, SongGrid.BeatsPerBar);

    /// <summary>The plan as the engine plays it. Holds have no length and are left out (they arrive in M5).</summary>
    /// <summary>
    /// The items as the screen shows them: an open repeat as one block (its further passes hidden) and every time on the
    /// effective clock (<see cref="EffectiveSec"/>), so the timeline and the plan strip agree with the song bars.
    /// </summary>
    public IReadOnlyList<PlanItem> DisplayItems => _display ??= BuildDisplay();

    private IReadOnlyList<PlanItem>? _display;

    private List<PlanItem> BuildDisplay()
    {
        var result = new List<PlanItem>(Items.Count);
        var skipped = 0.0;
        foreach (var item in Items)
        {
            if (item.IsOpen && item.Pass > 1)
            {
                skipped += item.Length;
                continue;
            }

            result.Add(skipped == 0 ? item : item with { PlanStart = item.PlanStart - skipped, PlanEnd = item.PlanEnd - skipped });
        }

        return result;
    }

    /// <summary>The plan position of an effective position: in the first pass of an open repeat, never in a further one (seeking).</summary>
    public double FromEffective(double effectiveSec)
    {
        double skipped = 0;
        foreach (var item in Items)
        {
            if (item.IsOpen && item.Pass > 1)
            {
                skipped += item.Length;
                continue;
            }

            var start = item.PlanStart - skipped;
            if (effectiveSec < start + item.Length - 1e-9 || item.Kind == PlanItemKind.Hold && effectiveSec <= start + 1e-9)
                return Math.Max(item.PlanStart, effectiveSec + skipped);
        }

        return DurationSec;
    }

    public IReadOnlyList<PlanHold> Holds() =>
        Items.Select((item, index) => (item, index)).Where(x => x.item.Kind == PlanItemKind.Hold)
            .Select(x => new PlanHold(x.item.PlanStart, x.item.HoldPad, x.item.HoldPad ? x.item.SongPitchClass : null, x.item.HoldClick))
            .ToList();

    /// <summary>
    /// The plan position with open repeats counted once: inside a further pass it is the same place as in the first
    /// pass, and everything after an open repeat is earlier by the extra passes. The song bar, the total and the
    /// remaining time use it (PB-32, PB-33).
    /// </summary>
    public double EffectiveSec(double planSec)
    {
        if (Items.Count == 0 || ExtraOpenSec <= 0)
            return planSec;

        var index = ItemIndexAt(planSec);
        var item = Items[index];
        double SkipBefore(int upTo) => Items.Take(upTo).Where(i => i.IsOpen && i.Pass > 1).Sum(i => i.Length);

        if (item.IsOpen && item.Pass > 1)
        {
            for (var first = 0; first < index; first++)
            {
                if (Items[first].EntryId == item.EntryId && Items[first].IsOpen && Items[first].Pass == 1)
                    return Items[first].PlanStart - SkipBefore(first) + (planSec - item.PlanStart);
            }
        }

        return planSec - SkipBefore(index);
    }

    public IReadOnlyList<PlanSegment> Segments() =>
        Items.Where(i => i.Length > 0)
            .Select(i => new PlanSegment(i.PlanStart, i.PlanEnd, i.HasSource ? i.SrcStart : null, i.FadeInSec, i.FadeOutSec, i.MutedTrackIds)
            {
                Key = i.KeyTotal,
                SongKey = i.SongPitchClass,
                Click = i.HasClick,
                Pad = i.Kind == PlanItemKind.Pad ? i.PadPitchClass : null,
            })
            .ToList();

    // ---- Readouts ---------------------------------------------------------------------

    public PlanReadout At(double planSec)
    {
        planSec = Math.Clamp(planSec, 0, Math.Max(0, DurationSec));
        var effective = EffectiveSec(planSec);
        var songBar = TotalBars > 0 || Items.Count > 0
            ? $"bar {Readouts.SongBar(SongGrid, effective)} / {TotalBars.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        var open = Items.Any(i => i.PlanStart + i.Length > planSec && (i.IsOpen || i.Kind == PlanItemKind.Hold));
        // PB-33: clock times at the entry tempo.
        var elapsed = Format.Clock(planSec / TempoFactor);
        var remaining = "−" + Format.Clock(Math.Max(0, ExpectedDurationSec - effective) / TempoFactor) + (open ? "+" : string.Empty);
        var time = $"{elapsed} / {remaining}";

        var source = SourceAt(planSec);
        var chartBar = source is { } s ? $"chart bar {Readouts.ChartBar(ChartGrid, s)}" : string.Empty;

        if (Items.Count == 0)
            return new PlanReadout(string.Empty, string.Empty, null, string.Empty, string.Empty, songBar, time, chartBar, 0);

        var index = ItemIndexAt(planSec);
        var item = Items[index];
        var progress = item.Length > 0 ? Math.Clamp((planSec - item.PlanStart) / item.Length, 0, 1) : 0;

        if (item.Kind == PlanItemKind.Song)
            return new PlanReadout(string.Empty, string.Empty, null, string.Empty, string.Empty, songBar, time, chartBar, progress);

        string nowDetail;
        if (item.Kind is PlanItemKind.LeadIn or PlanItemKind.CountIn)
        {
            // PB-31: before S0 a beat countdown instead of a bar.
            var beats = (int)Math.Ceiling((P0 - planSec) / ChartGrid.BeatLength - 0.05);
            nowDetail = $"beat −{Math.Max(1, beats).ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            var k = (int)Math.Floor((planSec - item.PlanStart) / ChartGrid.BarLength + ToleranceSec / ChartGrid.BarLength) + 1;
            k = Math.Clamp(k, 1, Math.Max(1, item.LengthBars));
            nowDetail = $"bar {k.ToString(CultureInfo.InvariantCulture)} / {item.LengthBars.ToString(CultureInfo.InvariantCulture)}";
        }

        var nowPass = item.IsOpen ? "until cued" : item.Passes > 1 ? $"pass {item.Pass.ToString(CultureInfo.InvariantCulture)} of {item.Passes.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        var upcoming = Items.Skip(index + 1).Where(i => i.Length > 0 || i.Kind == PlanItemKind.Hold).Take(2).ToList();
        var next = upcoming.FirstOrDefault();
        if (next?.Kind == PlanItemKind.Tail && upcoming.Count > 1 && upcoming[1].Kind == PlanItemKind.Hold)
            next = upcoming[1]; // the ring-out before a hold belongs to the section; the card says HOLD
        var nextLabel = NextLabel(item, next);
        var tag = IsAsWritten ? "AS WRITTEN" : "ARRANGEMENT";
        return new PlanReadout(
            NowLabelOf(item),
            nowDetail,
            nextLabel,
            tag,
            Countdown(planSec, item.PlanEnd),
            songBar,
            time,
            chartBar,
            progress,
            nowPass,
            item.Note ?? string.Empty,
            next?.Note ?? string.Empty);
    }

    private static string NowLabelOf(PlanItem item) => item.Kind switch
    {
        PlanItemKind.LeadIn => "LEAD-IN",
        PlanItemKind.CountIn => "COUNT-IN",
        PlanItemKind.Tail => "ENDING",
        PlanItemKind.Click => "CLICK",
        PlanItemKind.Pad => "PAD",
        PlanItemKind.Hold => "HOLD",
        _ => item.Label.ToUpperInvariant(),
    };

    private static string NextLabel(PlanItem current, PlanItem? next)
    {
        if (next is null)
            return "END OF SONG";
        return next.Kind switch
        {
            PlanItemKind.Tail => "ENDING",
            PlanItemKind.Click => "CLICK",
            PlanItemKind.Pad => "PAD",
            PlanItemKind.Hold => "HOLD",
            _ when next.Passes != 1 && next.EntryId is not null && next.EntryId == current.EntryId && next.Pass > 1
                => $"{next.Label.ToUpperInvariant()} (pass {next.Pass.ToString(CultureInfo.InvariantCulture)})",
            _ => next.Label.ToUpperInvariant(),
        };
    }

    /// <summary>PB-35: "in 8 bars, 2 beats", "in 8 bars", "in 3 beats" or "now", on the song grid.</summary>
    public string Countdown(double now, double executeAt)
    {
        var beatLen = SongGrid.BeatLength;
        var tol = ToleranceSec / beatLen;
        long BeatIndex(double t) => (long)Math.Floor(SongGrid.TimeToBeat(t) + tol);

        var target = SongGrid.TimeToBeat(executeAt);
        long remaining;
        if (Math.Abs(target - Math.Round(target)) * beatLen < ToleranceSec)
            remaining = BeatIndex(executeAt) - BeatIndex(now);
        else
            remaining = (long)Math.Ceiling((executeAt - now) / beatLen - 0.05); // off the grid

        if (remaining <= 0)
            return "now";
        var bpb = Math.Max(1, SongGrid.BeatsPerBar);
        var bars = remaining / bpb;
        var beats = remaining % bpb;
        if (bars == 0)
            return beats == 1 ? "in 1 beat" : $"in {beats.ToString(CultureInfo.InvariantCulture)} beats";
        var barText = bars == 1 ? "in 1 bar" : $"in {bars.ToString(CultureInfo.InvariantCulture)} bars";
        if (beats == 0)
            return barText;
        return beats == 1 ? $"{barText}, 1 beat" : $"{barText}, {beats.ToString(CultureInfo.InvariantCulture)} beats";
    }

    // ---- Keeping the place when the plan changes (ARR-90) -------------------------------

    /// <summary>
    /// Where to continue in <paramref name="next"/> when the plan changed while at
    /// <paramref name="planSec"/> of <paramref name="previous"/>. After an edit of the same arrangement:
    /// the same entry and pass at the same offset, or the first entry after it when that is gone. After a
    /// switch to another arrangement (or as written): the same source time, or the start. A playhead at the start
    /// stays at the start, so a new count-in or lead-in is heard (SL-36).
    /// </summary>
    public static double Remap(SongPlan previous, double planSec, SongPlan next, bool sameArrangement)
    {
        if (next.Items.Count == 0 || previous.Items.Count == 0 || planSec <= ToleranceSec)
            return 0;

        var old = previous.Items[previous.ItemIndexAt(planSec)];
        if (sameArrangement && old.EntryId is not null)
        {
            var offset = Math.Max(0, planSec - old.PlanStart);
            var same = next.Items.FirstOrDefault(i => i.Id == old.Id);
            if (same is not null)
                return same.PlanStart + Math.Min(offset, Math.Max(0, same.Length - 0.001));

            var after = next.Items.FirstOrDefault(i => i.EntryIndex >= old.EntryIndex && i.Length > 0);
            return after?.PlanStart ?? next.Items[^1].PlanStart;
        }

        if (previous.SourceAt(planSec) is { } source && next.PlanAt(source) is { } mapped)
            return mapped;
        return 0;
    }
}
