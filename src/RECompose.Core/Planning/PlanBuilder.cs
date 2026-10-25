// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;

namespace RECompose.Core.Planning;

/// <summary>Settings of plan building (SET-20) and the service entry's own values (DM-41).</summary>
public sealed record PlanOptions
{
    /// <summary>Length of the ring-out after the last section (SET-20 "Ending tail length").</summary>
    public double TailBars { get; init; } = 2;

    /// <summary>How the plan starts (SL-36): the stems' own lead-in (the default), generated click bars, or the first downbeat.</summary>
    public CountInMode CountIn { get; init; } = CountInMode.Track;

    /// <summary>Bars of a generated count-in, 1 or 2.</summary>
    public int CountInBars { get; init; } = 1;

    /// <summary>The entry's key offset in semitones (DM-41); key lifts add to it (PB-41 step 3).</summary>
    public int KeyOffset { get; init; }

    /// <summary>The entry's tempo as a factor, tempo% / 100 (PB-30 tf).</summary>
    public double TempoFactor { get; init; } = 1;

    /// <summary>The key of the stems, for the pitch a pad plays (SL-44, ARR-39); null when unknown.</summary>
    public Key? OriginalKey { get; init; }

    /// <summary>The options of a service entry: its count-in, key and tempo.</summary>
    public static PlanOptions For(ServiceEntry entry, Song song, double tailBars = 2) => new()
    {
        TailBars = tailBars,
        CountIn = entry.CountIn.Mode,
        CountInBars = entry.CountIn.Bars,
        KeyOffset = entry.KeyOffset,
        TempoFactor = entry.TempoFactor,
        OriginalKey = Key.TryParse(song.OriginalKey, out var key) ? key : null,
    };

    /// <summary>A library song outside a service: original key, 100 %, the stems' lead-in.</summary>
    public static PlanOptions For(Song song, double tailBars = 2) => new()
    {
        TailBars = tailBars,
        OriginalKey = Key.TryParse(song.OriginalKey, out var key) ? key : null,
    };

    /// <summary>The pitch class heard at <paramref name="semitones"/> from the original key, or null.</summary>
    internal int? PitchClassAt(int semitones) => OriginalKey?.Transpose(semitones).PitchClass;
}

/// <summary>
/// PB-41: the arrangement, the sections and the grid become a plan. Pure and deterministic; the
/// engine only executes what this produces.
/// </summary>
public static class PlanBuilder
{
    private const double Tolerance = 1e-6;

    public static SongPlan Build(
        TempoGrid chartGrid,
        IReadOnlyList<SectionInfo> sections,
        double durationSec,
        Arrangement? arrangement,
        IReadOnlyList<Track>? tracks = null,
        PlanOptions? options = null)
    {
        options ??= new PlanOptions();
        tracks ??= [];
        durationSec = Math.Max(0, durationSec);
        var items = new List<PlanItem>();
        var cursor = 0.0;

        if (sections.Count == 0)
        {
            // No sections: the whole file is one item and bar 1 is the first downbeat (milestone 1).
            if (arrangement is null && durationSec > 0)
            {
                items.Add(new PlanItem
                {
                    Id = "song",
                    Kind = PlanItemKind.Song,
                    Label = string.Empty,
                    HasSource = true,
                    SrcStart = 0,
                    SrcEnd = durationSec,
                    PlanStart = 0,
                    PlanEnd = durationSec,
                    KeyTotal = options.KeyOffset,
                    SongPitchClass = options.PitchClassAt(options.KeyOffset),
                });
            }

            return new SongPlan(chartGrid, sections, items, durationSec, isAsWritten: arrangement is null, options.TempoFactor);
        }

        var ordered = sections.OrderBy(s => s.StartSec).ToList();
        var s0 = ordered[0].StartSec;
        var barLen = chartGrid.BarLength;

        // PB-41 step 2 (SL-36): the stems' own lead-in when it is at least half a beat, otherwise dropped;
        // or whole bars of generated click that end on the first section's downbeat; or nothing.
        switch (options.CountIn)
        {
            case CountInMode.Track when s0 >= chartGrid.BeatLength / 2:
                items.Add(new PlanItem
                {
                    Id = "lead-in",
                    Kind = PlanItemKind.LeadIn,
                    Label = "Lead-in",
                    HasSource = true,
                    SrcStart = 0,
                    SrcEnd = s0,
                    PlanStart = 0,
                    PlanEnd = s0,
                    KeyTotal = options.KeyOffset,
                    SongPitchClass = options.PitchClassAt(options.KeyOffset),
                });
                cursor = s0;
                break;

            case CountInMode.Generated:
            {
                var bars = Math.Clamp(options.CountInBars, 1, 2);
                items.Add(new PlanItem
                {
                    Id = "count-in",
                    Kind = PlanItemKind.CountIn,
                    Label = "Count-in",
                    PlanStart = 0,
                    PlanEnd = bars * barLen,
                    LengthBars = bars,
                    ToBar = bars,
                    KeyTotal = options.KeyOffset,
                    SongPitchClass = options.PitchClassAt(options.KeyOffset),
                });
                cursor = bars * barLen;
                break;
            }
        }

        if (arrangement is null)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];
                items.Add(SectionItem(s, $"{s.Id}:1", null, i, 1, 1, s.LengthBars, s.StartSec, s.EndSec, ref cursor, null, options));
            }
        }
        else
        {
            var byId = ordered.ToDictionary(s => s.Id);
            for (var index = 0; index < arrangement.Entries.Count; index++)
            {
                var entry = arrangement.Entries[index];
                switch (entry.Kind)
                {
                    case EntryKind.Section:
                        if (entry.SectionId is null || !byId.TryGetValue(entry.SectionId, out var section))
                            break; // the section was removed: skipped here, flagged by ArrangementValidator

                        AddSection(items, entry, index, section, ref cursor, barLen, tracks, options);
                        break;

                    case EntryKind.Click:
                    case EntryKind.Pad:
                    {
                        var bars = Math.Max(1, entry.LengthBars);
                        var length = bars * barLen;
                        items.Add(new PlanItem
                        {
                            Id = $"{entry.Id}:1",
                            Kind = entry.Kind == EntryKind.Click ? PlanItemKind.Click : PlanItemKind.Pad,
                            Label = entry.Kind == EntryKind.Click ? "Click" : "Pad",
                            EntryId = entry.Id,
                            EntryIndex = index,
                            PlanStart = cursor,
                            PlanEnd = cursor + length,
                            LengthBars = bars,
                            FromBar = 1,
                            ToBar = bars,
                            Note = entry.Note,
                            KeyTotal = options.KeyOffset,
                            SongPitchClass = options.PitchClassAt(options.KeyOffset),
                            PadPitchClass = entry.Kind == EntryKind.Pad ? PadPitchClass(entry, options) : null,
                        });
                        cursor += length;
                        break;
                    }

                    case EntryKind.Hold:
                    {
                        // PB-52: the previous section rings out first (unless it is cut), then the clock waits for GO.
                        AddTail(items, options, chartGrid, durationSec, $"tail:{entry.Id}");
                        var before = items.LastOrDefault();
                        cursor = before?.PlanEnd ?? cursor;
                        items.Add(new PlanItem
                        {
                            Id = $"{entry.Id}:1",
                            Kind = PlanItemKind.Hold,
                            Label = "Hold",
                            EntryId = entry.Id,
                            EntryIndex = index,
                            PlanStart = cursor,
                            PlanEnd = cursor,
                            Note = entry.Note,
                            KeyTotal = before?.KeyTotal ?? options.KeyOffset,
                            SongPitchClass = before is null ? options.PitchClassAt(options.KeyOffset) : before.SongPitchClass,
                            HoldPad = entry.Hold.Pad,
                            HoldClick = entry.Hold.Click,
                        });

                        // GO starts with a count-in of click, if the hold asks for one (ARR-38).
                        if (entry.Hold.CountInBars > 0)
                        {
                            var bars = entry.Hold.CountInBars;
                            items.Add(new PlanItem
                            {
                                Id = $"{entry.Id}:count-in",
                                Kind = PlanItemKind.CountIn,
                                Label = "Count-in",
                                EntryId = entry.Id,
                                EntryIndex = index,
                                PlanStart = cursor,
                                PlanEnd = cursor + bars * barLen,
                                LengthBars = bars,
                                ToBar = bars,
                                AfterHold = true,
                                KeyTotal = before?.KeyTotal ?? options.KeyOffset,
                                SongPitchClass = before is null ? options.PitchClassAt(options.KeyOffset) : before.SongPitchClass,
                            });
                            cursor += bars * barLen;
                        }

                        break;
                    }
                }
            }
        }

        AddTail(items, options, chartGrid, durationSec);
        return new SongPlan(chartGrid, sections, items, durationSec, isAsWritten: arrangement is null, options.TempoFactor);
    }

    /// <summary>ARR-39: the pad key of pad bars, a chosen key or the entry key; null when neither is known.</summary>
    private static int? PadPitchClass(ArrangementEntry entry, PlanOptions options) =>
        Key.TryParse(entry.PadKey, out var key) ? key.PitchClass : options.PitchClassAt(options.KeyOffset);

    private static void AddSection(List<PlanItem> items, ArrangementEntry entry, int index, SectionInfo section, ref double cursor, double barLen, IReadOnlyList<Track> tracks, PlanOptions options)
    {
        var sectionBars = Math.Max(1, section.LengthBars);
        var from = Math.Clamp(entry.Range.FromBar, 1, sectionBars);
        var to = Math.Clamp(entry.Range.ToBar ?? sectionBars, from, sectionBars);
        var srcStart = section.StartSec + (from - 1) * barLen;
        var srcEnd = to >= sectionBars ? section.EndSec : section.StartSec + to * barLen;
        srcEnd = Math.Max(srcStart, srcEnd);
        var partial = from > 1 || to < sectionBars;

        // An open repeat is built as all its passes (up to MaxPasses, PB-51); moving on ends it at the end of the pass
        // that plays. Its readouts count it once (SongPlan).
        var open = entry.Repeat.UntilCued;
        var passes = open ? Math.Max(1, entry.Repeat.MaxPasses) : Math.Max(1, entry.Repeat.Count);
        for (var pass = 1; pass <= passes; pass++)
        {
            var first = pass == 1;
            var last = pass == passes;
            var muted = entry.Mutes.IsEmpty || (entry.Mutes.FirstPassOnly && !first)
                ? (IReadOnlyList<string>)[]
                : MutedTracks(entry.Mutes, tracks);
            var groups = entry.Mutes.IsEmpty || (entry.Mutes.FirstPassOnly && !first) ? (IReadOnlyList<string>)[] : entry.Mutes.Groups;
            var item = SectionItem(section, $"{entry.Id}:{pass}", entry, index, pass, open ? 0 : passes, to - from + 1, srcStart, srcEnd, ref cursor, muted, options) with
            {
                FromBar = from,
                ToBar = to,
                IsPartial = partial,
                MutedGroups = groups,
            };
            var length = item.Length;
            items.Add(item with
            {
                FadeInSec = first ? Math.Min(entry.FadeInBars * barLen, length) : 0,
                FadeOutSec = last ? Math.Min(entry.FadeOutBars * barLen, length) : 0,
            });
        }
    }

    private static PlanItem SectionItem(
        SectionInfo section, string id, ArrangementEntry? entry, int index, int pass, int passes, int bars,
        double srcStart, double srcEnd, ref double cursor, IReadOnlyList<string>? muted, PlanOptions options)
    {
        var length = srcEnd - srcStart;
        var key = options.KeyOffset + (entry?.Transpose ?? 0);
        var item = new PlanItem
        {
            Id = id,
            Kind = PlanItemKind.Section,
            Label = section.Label,
            EntryId = entry?.Id,
            EntryIndex = entry is null ? -1 : index,
            Section = section,
            Pass = pass,
            Passes = passes,
            HasSource = true,
            SrcStart = srcStart,
            SrcEnd = srcEnd,
            PlanStart = cursor,
            PlanEnd = cursor + length,
            LengthBars = bars,
            FromBar = 1,
            ToBar = bars,
            Transpose = entry?.Transpose ?? 0,
            KeyTotal = key,
            SongPitchClass = options.PitchClassAt(key),
            MutedTrackIds = muted ?? [],
            Note = entry?.Note,
            Ending = entry?.Ending ?? EndingMode.Continue,
        };
        cursor += length;
        return item;
    }

    /// <summary>The ids of the tracks a mute spec silences: its tracks and every music track in its groups. Click and guide are never muted by a group.</summary>
    private static List<string> MutedTracks(MuteSpec mutes, IReadOnlyList<Track> tracks)
    {
        var ids = new List<string>(mutes.TrackIds);
        foreach (var track in tracks)
        {
            if (track.Hidden || track.IsClickOrGuide || ids.Contains(track.Id))
                continue;
            if (mutes.Groups.Any(g => string.Equals(g, track.Group, StringComparison.OrdinalIgnoreCase)))
                ids.Add(track.Id);
        }

        return ids;
    }

    /// <summary>
    /// PB-41 step 4: after the last item, if it is a section whose ending isn't Cut, the source audio
    /// keeps going for <see cref="PlanOptions.TailBars"/> with a fade-out. It is shortened to the audio left
    /// in the file and left out when that is under half a beat (an Ending that reaches the end of the file).
    /// </summary>
    private static void AddTail(List<PlanItem> items, PlanOptions options, TempoGrid grid, double durationSec, string id = "tail")
    {
        var last = items.LastOrDefault();
        if (last is null || last.Kind != PlanItemKind.Section || last.Ending == EndingMode.Cut || options.TailBars <= 0)
            return;

        var available = durationSec - last.SrcEnd;
        var length = Math.Min(options.TailBars * grid.BarLength, available);
        if (length < grid.BeatLength / 2)
            return;

        // A whole number of beats keeps the plan on the grid when the file allows it.
        var beats = Math.Floor(length / grid.BeatLength + Tolerance);
        if (beats >= 1)
            length = beats * grid.BeatLength;

        items.Add(new PlanItem
        {
            Id = id,
            Kind = PlanItemKind.Tail,
            Label = "Ending",
            HasSource = true,
            SrcStart = last.SrcEnd,
            SrcEnd = last.SrcEnd + length,
            PlanStart = last.PlanEnd,
            PlanEnd = last.PlanEnd + length,
            LengthBars = Math.Max(1, (int)Math.Ceiling(length / grid.BarLength - Tolerance)),
            FadeOutSec = length,
            KeyTotal = last.KeyTotal,
            SongPitchClass = last.SongPitchClass,
        });
    }
}
