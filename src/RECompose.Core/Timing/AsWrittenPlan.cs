// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Sections;

namespace RECompose.Core.Timing;

/// <summary>What the header shows at a position (PB-30…35, LIVE-20 rule 4 for a song played as written).</summary>
public sealed record PlanReadout(
    string NowLabel,
    string NowDetail,
    string? NextLabel,
    string NextTag,
    string Countdown,
    string SongBar,
    string Time,
    string ChartBar,
    double SectionProgress);

/// <summary>One item of the as-written plan: the lead-in or a section.</summary>
public sealed record AsWrittenItem(string Label, double Start, double End, int LengthBars, SectionInfo? Section)
{
    public bool IsLeadIn => Section is null;
}

/// <summary>
/// The plan of a song played as written (PB-41 step 1, "every section in order, no options"):
/// a lead-in when the audio before the first section is at least half a beat, then the
/// sections. Song bars count from the first section (S0, PB-32); without sections the whole
/// file is one item and bar 1 is the first downbeat, as in milestone 1. Arrangements (M3)
/// replace this with the plan builder.
/// </summary>
public sealed class AsWrittenPlan
{
    private const double ToleranceSec = 0.002;

    public AsWrittenPlan(TempoGrid chartGrid, IReadOnlyList<SectionInfo> sections, double durationSec)
    {
        ChartGrid = chartGrid;
        Sections = sections;
        DurationSec = Math.Max(0, durationSec);
        S0 = sections.Count > 0 ? sections[0].StartSec : chartGrid.FirstDownbeatSec;
        SongGrid = chartGrid.WithOrigin(S0);

        var items = new List<AsWrittenItem>();
        if (sections.Count > 0 && S0 >= chartGrid.BeatLength / 2)
            items.Add(new AsWrittenItem("LEAD-IN", 0, S0, 0, null));
        foreach (var s in sections)
            items.Add(new AsWrittenItem(s.Label, s.StartSec, s.EndSec, s.LengthBars, s));
        Items = items;
        TotalBars = Readouts.TotalBars(SongGrid, DurationSec);
    }

    public TempoGrid ChartGrid { get; }

    /// <summary>The grid with bar 1 at S0: the ruler and the song bar use it.</summary>
    public TempoGrid SongGrid { get; }

    public IReadOnlyList<SectionInfo> Sections { get; }
    public IReadOnlyList<AsWrittenItem> Items { get; }
    public double DurationSec { get; }
    public double S0 { get; }

    /// <summary>Song bars from S0 to the end (PB-32).</summary>
    public int TotalBars { get; }

    public bool HasSections => Sections.Count > 0;

    /// <summary>The item playing at <paramref name="seconds"/> (the first one at rest, LIVE-21).</summary>
    public int ItemIndexAt(double seconds)
    {
        var index = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].Start <= seconds + ToleranceSec)
                index = i;
        }

        return index;
    }

    public PlanReadout At(double seconds)
    {
        // A lead-in shorter than half a beat is dropped (PB-41 step 2): the plan starts at S0.
        if (seconds < S0 && (Items.Count == 0 || !Items[0].IsLeadIn) && S0 < ChartGrid.BeatLength / 2)
            seconds = S0;

        var songBar = $"bar {Readouts.SongBar(SongGrid, seconds)} / {TotalBars.ToString(CultureInfo.InvariantCulture)}";
        var (elapsed, remaining) = Readouts.Times(seconds, DurationSec);
        var time = $"{elapsed} / {remaining}";
        var chartBar = $"chart bar {Readouts.ChartBar(ChartGrid, seconds)}";

        if (Items.Count == 0)
            return new PlanReadout(string.Empty, string.Empty, null, string.Empty, string.Empty, songBar, time, chartBar, 0);

        var index = ItemIndexAt(seconds);
        var item = Items[index];
        string nowDetail;
        double progress;
        if (item.IsLeadIn)
        {
            // PB-31: before S0 a beat countdown instead of a bar.
            var beats = (int)Math.Ceiling((S0 - seconds) / ChartGrid.BeatLength - 0.05);
            nowDetail = $"beat −{Math.Max(1, beats).ToString(CultureInfo.InvariantCulture)}";
            progress = item.End > item.Start ? Math.Clamp((seconds - item.Start) / (item.End - item.Start), 0, 1) : 0;
        }
        else
        {
            var k = (int)Math.Floor((seconds - item.Start) / ChartGrid.BarLength + ToleranceSec / ChartGrid.BarLength) + 1;
            k = Math.Clamp(k, 1, Math.Max(1, item.LengthBars));
            nowDetail = $"bar {k.ToString(CultureInfo.InvariantCulture)} / {item.LengthBars.ToString(CultureInfo.InvariantCulture)}";
            progress = item.End > item.Start ? Math.Clamp((seconds - item.Start) / (item.End - item.Start), 0, 1) : 0;
        }

        var next = index + 1 < Items.Count ? Items[index + 1] : null;
        var nextLabel = next is null ? "END OF SONG" : next.Label.ToUpperInvariant();
        var countdown = Countdown(seconds, item.End);
        return new PlanReadout(item.Label.ToUpperInvariant(), nowDetail, nextLabel, "AS WRITTEN", countdown, songBar, time, chartBar, progress);
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
}
