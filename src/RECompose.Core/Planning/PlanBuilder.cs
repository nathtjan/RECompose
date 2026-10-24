// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Planning;

/// <summary>Settings of plan building (SET-20). Service entries add the count-in mode in M4.</summary>
public sealed record PlanOptions
{
    /// <summary>Length of the ring-out after the last section (SET-20 "Ending tail length").</summary>
    public double TailBars { get; init; } = 2;

    /// <summary>Use the stems' own lead-in before the first section (SL-36 "Track", the default).</summary>
    public bool TrackLeadIn { get; init; } = true;
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
                });
            }

            return new SongPlan(chartGrid, sections, items, durationSec, isAsWritten: arrangement is null);
        }

        var ordered = sections.OrderBy(s => s.StartSec).ToList();
        var s0 = ordered[0].StartSec;
        var barLen = chartGrid.BarLength;

        // PB-41 step 2: the stems' own lead-in when it is at least half a beat, otherwise dropped.
        if (options.TrackLeadIn && s0 >= chartGrid.BeatLength / 2)
        {
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
            });
            cursor = s0;
        }

        if (arrangement is null)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];
                items.Add(SectionItem(s, $"{s.Id}:1", null, i, 1, 1, s.LengthBars, s.StartSec, s.EndSec, ref cursor, null));
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

                        AddSection(items, entry, index, section, ref cursor, barLen, tracks);
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
                        });
                        cursor += length;
                        break;
                    }

                    case EntryKind.Hold:
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
                        });
                        break;
                }
            }
        }

        AddTail(items, options, chartGrid, durationSec);
        return new SongPlan(chartGrid, sections, items, durationSec, isAsWritten: arrangement is null);
    }

    private static void AddSection(List<PlanItem> items, ArrangementEntry entry, int index, SectionInfo section, ref double cursor, double barLen, IReadOnlyList<Track> tracks)
    {
        var sectionBars = Math.Max(1, section.LengthBars);
        var from = Math.Clamp(entry.Range.FromBar, 1, sectionBars);
        var to = Math.Clamp(entry.Range.ToBar ?? sectionBars, from, sectionBars);
        var srcStart = section.StartSec + (from - 1) * barLen;
        var srcEnd = to >= sectionBars ? section.EndSec : section.StartSec + to * barLen;
        srcEnd = Math.Max(srcStart, srcEnd);
        var partial = from > 1 || to < sectionBars;

        var open = entry.Repeat.UntilCued;
        var passes = open ? 1 : Math.Max(1, entry.Repeat.Count);
        for (var pass = 1; pass <= passes; pass++)
        {
            var first = pass == 1;
            var last = pass == passes;
            var muted = entry.Mutes.IsEmpty || (entry.Mutes.FirstPassOnly && !first)
                ? (IReadOnlyList<string>)[]
                : MutedTracks(entry.Mutes, tracks);
            var groups = entry.Mutes.IsEmpty || (entry.Mutes.FirstPassOnly && !first) ? (IReadOnlyList<string>)[] : entry.Mutes.Groups;
            var item = SectionItem(section, $"{entry.Id}:{pass}", entry, index, pass, open ? 0 : passes, to - from + 1, srcStart, srcEnd, ref cursor, muted) with
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
        double srcStart, double srcEnd, ref double cursor, IReadOnlyList<string>? muted)
    {
        var length = srcEnd - srcStart;
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
    private static void AddTail(List<PlanItem> items, PlanOptions options, TempoGrid grid, double durationSec)
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
            Id = "tail",
            Kind = PlanItemKind.Tail,
            Label = "Ending",
            HasSource = true,
            SrcStart = last.SrcEnd,
            SrcEnd = last.SrcEnd + length,
            PlanStart = last.PlanEnd,
            PlanEnd = last.PlanEnd + length,
            LengthBars = Math.Max(1, (int)Math.Ceiling(length / grid.BarLength - Tolerance)),
            FadeOutSec = length,
        });
    }
}
