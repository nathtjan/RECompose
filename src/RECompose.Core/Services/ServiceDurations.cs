// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Timing;

namespace RECompose.Core.Services;

/// <summary>One entry of a service with what it plays: its song (null when missing) and its plan.</summary>
public sealed record PlannedEntry(ServiceEntry Entry, Song? Song, SongPlan? Plan);

/// <summary>A length as heard; <see cref="Open"/> adds the "+" of an end that waits for a cue.</summary>
public readonly record struct HeardLength(double Seconds, bool Open)
{
    public string Text => Format.Clock(Seconds) + (Open ? "+" : string.Empty);
}

/// <summary>ARR-80: the length of each entry at its tempo, and of the whole service.</summary>
public static class ServiceDurations
{
    /// <summary>
    /// How much an entry adds to the service: up to where the next entry starts. A Stop plays the whole plan; a
    /// Segue hands over when the tail starts, plus its gap; a Crossfade when the tail starts (the overlap counts
    /// once); a Pad or Carry-over transition waits for Play, so it adds nothing and marks the length open, unless
    /// the next song starts by itself after some bars of click. The last entry plays its whole plan.
    /// </summary>
    public static HeardLength Contribution(PlannedEntry entry, PlannedEntry? next)
    {
        if (entry.Plan is not { } plan || plan.IsEmpty)
            return new HeardLength(0, false);

        var tf = plan.TempoFactor;
        var open = plan.HasOpenEnd;
        var whole = plan.HeardDurationSec;
        if (next is null)
            return new HeardLength(whole, open);

        var transition = entry.Entry.TransitionAfter;
        var atTransition = plan.TransitionPointSec / tf;
        switch (transition.Type)
        {
            case TransitionType.Segue:
                return new HeardLength(atTransition + transition.SegueGapBeats * plan.ChartGrid.BeatLength / tf, open);

            case TransitionType.Crossfade:
                return new HeardLength(atTransition, open);

            case TransitionType.Pad when transition.AutoStartNext:
            {
                var bpm = ServiceText.ClickBpm(transition, next.Song?.Bpm);
                var bpb = next.Song?.BeatsPerBar ?? 4;
                return new HeardLength(Math.Max(whole, atTransition + transition.AutoStartAfterBars * bpb * 60 / bpm), open);
            }

            case TransitionType.Pad:
            case TransitionType.CarryOver:
                return new HeardLength(whole, true);

            default:
                return new HeardLength(whole, open);
        }
    }

    public static HeardLength Total(IReadOnlyList<PlannedEntry> entries)
    {
        var seconds = 0.0;
        var open = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var part = Contribution(entries[i], i + 1 < entries.Count ? entries[i + 1] : null);
            seconds += part.Seconds;
            open |= part.Open;
        }

        return new HeardLength(seconds, open);
    }

    /// <summary>The service block's second line (UI-12): "4 songs · 24:30+", "1 song · 4:13", "No songs".</summary>
    public static string Summary(IReadOnlyList<PlannedEntry> entries)
    {
        if (entries.Count == 0)
            return "No songs";
        var songs = entries.Count == 1 ? "1 song" : $"{entries.Count.ToString(CultureInfo.InvariantCulture)} songs";
        return $"{songs} · {Total(entries).Text}";
    }
}

/// <summary>The plan of a service entry (PB-40, PB-41).</summary>
public static class EntryPlan
{
    public static SongPlan Build(ServiceEntry entry, Song song, double tailBars = 2) =>
        Build(song, EntryArrangement.Resolve(entry, song).Arrangement, PlanOptions.For(entry, song, tailBars));

    /// <summary>A library song outside a service: its default variant at the original key and tempo.</summary>
    public static SongPlan Build(Song song, double tailBars = 2) =>
        Build(song, EntryArrangement.ForLibrarySong(song).Arrangement, PlanOptions.For(song, tailBars));

    private static SongPlan Build(Song song, Arrangements.Arrangement? arrangement, PlanOptions options) =>
        PlanBuilder.Build(
            song.Grid,
            Sections.SectionList.Describe(song.Sections, song.Grid, song.DurationSec),
            song.DurationSec,
            arrangement,
            song.Tracks.Where(t => !t.Hidden).ToList(),
            options);
}
