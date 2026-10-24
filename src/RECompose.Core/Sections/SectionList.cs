// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Timing;

namespace RECompose.Core.Sections;

/// <summary>A section with its derived fields (DM-20): index, label, badge, end, chart bar and length.</summary>
public sealed record SectionInfo(
    Section Section,
    int Index,
    string Label,
    string? Badge,
    string ColorToken,
    double StartSec,
    double EndSec,
    int ChartBar,
    int LengthBars)
{
    public string Id => Section.Id;
    public SectionType Type => Section.Type;
}

/// <summary>What M or "+ At playhead" did (SEC-52/53).</summary>
public sealed record MarkResult(List<Section> Sections, Section? Added, Section? Existing, int ChartBar)
{
    public bool AlreadyThere => Existing is not null;
}

/// <summary>
/// Derived fields and the edit operations of SEC-41 and SEC-50…53. Every operation is pure:
/// it returns a new, time-sorted list and leaves the input alone, so each edit is one undo step.
/// </summary>
public static class SectionList
{
    private const double Tolerance = 0.001;

    public static List<Section> Sorted(IEnumerable<Section> sections) =>
        sections.OrderBy(s => s.StartSec).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// SEC-41: when two or more sections share a name, each gets its occurrence number in
    /// time order ("Chorus 1", "Chorus 2"); unique names stay as they are.
    /// </summary>
    public static List<string> Labels(IReadOnlyList<Section> sorted)
    {
        var counts = sorted.GroupBy(s => Key(s.Name)).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        var labels = new List<string>(sorted.Count);
        foreach (var section in sorted)
        {
            var key = Key(section.Name);
            var name = section.Name.Trim();
            if (counts[key] < 2)
            {
                labels.Add(name);
                continue;
            }

            seen[key] = seen.TryGetValue(key, out var n) ? n + 1 : 1;
            labels.Add($"{name} {seen[key].ToString(CultureInfo.InvariantCulture)}");
        }

        return labels;
    }

    /// <summary>The sections in time order with their derived fields.</summary>
    public static List<SectionInfo> Describe(IEnumerable<Section> sections, TempoGrid grid, double durationSec)
    {
        var sorted = Sorted(sections);
        var labels = Labels(sorted);
        var infos = new List<SectionInfo>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i];
            var end = i + 1 < sorted.Count ? sorted[i + 1].StartSec : Math.Max(durationSec, s.StartSec);
            var bars = Math.Max(1, (int)Math.Ceiling((end - s.StartSec) / grid.BarLength - 0.01));
            infos.Add(new SectionInfo(s, i, labels[i], SectionTypes.Badge(s.Type, labels[i]), SectionTypes.ColorToken(s.Type),
                s.StartSec, end, Readouts.ChartBar(grid, s.StartSec).Bar, bars));
        }

        return infos;
    }

    /// <summary>The section playing at <paramref name="seconds"/>, or null before the first one.</summary>
    public static SectionInfo? At(IReadOnlyList<SectionInfo> infos, double seconds)
    {
        SectionInfo? current = null;
        foreach (var info in infos)
        {
            if (info.StartSec <= seconds + Tolerance)
                current = info;
            else
                break;
        }

        return current;
    }

    // ---- Edits (SEC-50, SEC-51) -------------------------------------------------------

    /// <summary>Renames a section; its type follows the name unless it was overridden (SEC-43).</summary>
    public static List<Section> Rename(IEnumerable<Section> sections, string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return Sorted(sections);
        return Replace(sections, id, s => s with
        {
            Name = name,
            Type = s.TypeOverridden ? s.Type : SectionTypes.FromName(name),
            Edited = s.Edited || s.Name != name,
        });
    }

    /// <summary>Changes the type independently of the name (SEC-43).</summary>
    public static List<Section> SetType(IEnumerable<Section> sections, string id, SectionType type) =>
        Replace(sections, id, s => s with { Type = type, TypeOverridden = true, Edited = true });

    /// <summary>
    /// Moves a start to the nearest bar (or beat), keeping it strictly between its neighbours.
    /// Returns the list unchanged when the move isn't possible.
    /// </summary>
    public static List<Section> Move(IEnumerable<Section> sections, string id, double seconds, TempoGrid grid, double durationSec, bool snapToBeat = false)
    {
        var sorted = Sorted(sections);
        var index = sorted.FindIndex(s => s.Id == id);
        if (index < 0)
            return sorted;

        var start = snapToBeat ? grid.NearestBeatTime(seconds) : grid.NearestBarTime(seconds);
        var previous = index > 0 ? sorted[index - 1].StartSec : double.NegativeInfinity;
        var next = index + 1 < sorted.Count ? sorted[index + 1].StartSec : durationSec;
        if (start <= previous + Tolerance || start >= next - Tolerance || Math.Abs(start - sorted[index].StartSec) < Tolerance)
            return sorted;

        sorted[index] = sorted[index] with { StartSec = start, Edited = true };
        return Sorted(sorted);
    }

    /// <summary>Earlier or later by whole bars (review flow, SEC-31).</summary>
    public static List<Section> Nudge(IEnumerable<Section> sections, string id, int bars, TempoGrid grid, double durationSec)
    {
        var section = sections.FirstOrDefault(s => s.Id == id);
        return section is null
            ? Sorted(sections)
            : Move(sections, id, section.StartSec + bars * grid.BarLength, grid, durationSec);
    }

    public static List<Section> Delete(IEnumerable<Section> sections, string id) =>
        Sorted(sections.Where(s => s.Id != id));

    /// <summary>Removes the following section, so this one runs on to the start after it (SEC-50).</summary>
    public static List<Section> MergeWithNext(IEnumerable<Section> sections, string id)
    {
        var sorted = Sorted(sections);
        var index = sorted.FindIndex(s => s.Id == id);
        if (index < 0 || index + 1 >= sorted.Count)
            return sorted;
        sorted.RemoveAt(index + 1);
        sorted[index] = sorted[index] with { Edited = true };
        return sorted;
    }

    /// <summary>Splits the section at <paramref name="seconds"/> (nearest bar). The new part keeps the name.</summary>
    public static (List<Section> Sections, Section? Added) Split(IEnumerable<Section> sections, double seconds, TempoGrid grid, double durationSec)
    {
        var sorted = Sorted(sections);
        var start = grid.NearestBarTime(seconds);
        var host = sorted.LastOrDefault(s => s.StartSec < start - Tolerance);
        if (host is null || start >= durationSec - Tolerance || sorted.Any(s => Math.Abs(s.StartSec - start) < Tolerance))
            return (sorted, null);

        var added = host with
        {
            Id = Section.NewId(),
            StartSec = start,
            Origin = SectionOrigin.Manual,
            Confidence = null,
            NeedsReview = false,
            Edited = false,
            CallId = null,
        };
        sorted.Add(added);
        return (Sorted(sorted), added);
    }

    /// <summary>Clears the review flag (SEC-31 Accept).</summary>
    public static List<Section> Accept(IEnumerable<Section> sections, string id) =>
        Replace(sections, id, s => s with { NeedsReview = false });

    /// <summary>Sets a start exactly (the table's Start field and its Playhead button, SEC-51): snapped to the nearest bar.</summary>
    public static List<Section> SetStart(IEnumerable<Section> sections, string id, double seconds, TempoGrid grid, double durationSec) =>
        Move(sections, id, seconds, grid, durationSec);

    /// <summary>
    /// SEC-52: a section at the playhead, snapped to the nearest bar. The name comes from the
    /// nearest guide call within ±2 bars, otherwise "Section {n}". When a section already starts
    /// there, nothing is added and <see cref="MarkResult.Existing"/> says which one.
    /// </summary>
    public static MarkResult Mark(IEnumerable<Section> sections, double seconds, TempoGrid grid, double durationSec, IEnumerable<GuideCall> calls)
    {
        var sorted = Sorted(sections);
        var start = grid.NearestBarTime(seconds);
        if (start >= durationSec - Tolerance && durationSec > 0)
            start = grid.NearestBarTime(durationSec - grid.BarLength);
        var chartBar = Readouts.ChartBar(grid, start).Bar;

        if (sorted.FirstOrDefault(s => Math.Abs(s.StartSec - start) < Tolerance) is { } existing)
            return new MarkResult(sorted, null, existing, chartBar);

        var call = calls
            .Where(c => c.SectionName is not null && c.SectionStartSec is not null)
            .Select(c => (Call: c, Distance: Math.Abs(c.SectionStartSec!.Value - start)))
            .Where(c => c.Distance <= 2 * grid.BarLength + Tolerance)
            .OrderBy(c => c.Distance)
            .Select(c => c.Call)
            .FirstOrDefault();

        var name = call?.SectionName ?? $"Section {(sorted.Count + 1).ToString(CultureInfo.InvariantCulture)}";
        var added = Section.Create(name, start, SectionOrigin.Marked) with { CallId = call?.Id };
        sorted.Add(added);
        return new MarkResult(Sorted(sorted), added, null, chartBar);
    }

    /// <summary>Puts every start back on the nearest bar line of a new grid (after re-analysing the click).</summary>
    public static List<Section> Resnap(IEnumerable<Section> sections, TempoGrid grid)
    {
        var result = new List<Section>();
        foreach (var s in Sorted(sections))
        {
            var start = grid.NearestBarTime(s.StartSec);
            if (result.Count > 0 && start <= result[^1].StartSec + Tolerance)
                continue; // two starts collapsed onto one bar: keep the first
            result.Add(s with { StartSec = start });
        }

        return result;
    }

    private static List<Section> Replace(IEnumerable<Section> sections, string id, Func<Section, Section> change) =>
        Sorted(sections.Select(s => s.Id == id ? change(s) : s));

    private static string Key(string name) => name.Trim().ToLowerInvariant();
}
