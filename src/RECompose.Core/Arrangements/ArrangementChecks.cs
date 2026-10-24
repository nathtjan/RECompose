// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Arrangements;

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One line of the live validation (ARR-50).</summary>
public sealed record ArrangementIssue(IssueSeverity Severity, string Message, string? EntryId = null);

/// <summary>ARR-50: the warnings shown in the ENTRY tab and, from M6, in the service check.</summary>
public static class ArrangementValidator
{
    public static List<ArrangementIssue> Validate(Arrangement arrangement, IReadOnlyList<SectionInfo> sections)
    {
        var issues = new List<ArrangementIssue>();
        if (arrangement.Entries.Count == 0)
        {
            issues.Add(new ArrangementIssue(IssueSeverity.Error, "Nothing to play — add sections or switch to As written"));
            return issues;
        }

        var byId = sections.ToDictionary(s => s.Id);
        foreach (var entry in arrangement.Entries)
        {
            if (entry.Kind != EntryKind.Section)
                continue;

            if (entry.SectionId is null || !byId.ContainsKey(entry.SectionId))
            {
                issues.Add(new ArrangementIssue(IssueSeverity.Error, "Section removed — choose a replacement", entry.Id));
                continue;
            }

            if (entry.Range.ToBar is { } to && to < entry.Range.FromBar)
                issues.Add(new ArrangementIssue(IssueSeverity.Error, "A range needs at least 1 bar", entry.Id));
            if (Math.Abs(entry.Transpose) > 5)
                issues.Add(new ArrangementIssue(IssueSeverity.Warning, "Large key lift may sound unnatural", entry.Id));
        }

        var last = arrangement.Entries[^1];
        if ((last.Kind == EntryKind.Section && last.Repeat.UntilCued) || last.Kind == EntryKind.Hold)
            issues.Add(new ArrangementIssue(IssueSeverity.Warning, "Song can only end by cue", last.Id));

        if (last.Kind == EntryKind.Section && last.SectionId is { } id && byId.TryGetValue(id, out var section)
            && section.Type is not (SectionType.Ending or SectionType.Outro) && last.Ending == EndingMode.Continue)
            issues.Add(new ArrangementIssue(IssueSeverity.Info, $"Song will ring out after {section.Label}", last.Id));

        return issues;
    }

    public static bool HasErrors(IEnumerable<ArrangementIssue> issues) => issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>ARR-13: "1. Verse 1 · 2. Chorus 1 · 3. Bridge ×2 · 4. Chorus 3 (+2) — 76 bars · 4:13".</summary>
public static class ArrangementSummary
{
    /// <summary>The numbered labels of the entries.</summary>
    public static string Labels(Arrangement arrangement, IReadOnlyList<SectionInfo> sections)
    {
        var byId = sections.ToDictionary(s => s.Id);
        var parts = new List<string>();
        var n = 0;
        foreach (var entry in arrangement.Entries)
        {
            n++;
            parts.Add($"{n.ToString(CultureInfo.InvariantCulture)}. {EntryText(entry, byId)}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The text of one entry as the list shows it: label, range, repeat, key lift.</summary>
    public static string EntryText(ArrangementEntry entry, IReadOnlyDictionary<string, SectionInfo> byId)
    {
        switch (entry.Kind)
        {
            case EntryKind.Click:
                return $"Click {entry.LengthBars.ToString(CultureInfo.InvariantCulture)}";
            case EntryKind.Pad:
                return $"Pad {entry.LengthBars.ToString(CultureInfo.InvariantCulture)}";
            case EntryKind.Hold:
                return "Hold";
        }

        if (entry.SectionId is null || !byId.TryGetValue(entry.SectionId, out var section))
            return "⚠ Section removed";

        var text = new StringBuilder(section.Label);
        if (!entry.Range.IsWhole)
        {
            var to = entry.Range.ToBar is { } t ? t.ToString(CultureInfo.InvariantCulture) : section.LengthBars.ToString(CultureInfo.InvariantCulture);
            text.Append(CultureInfo.InvariantCulture, $" · {entry.Range.FromBar}–{to}");
        }

        if (entry.Repeat.UntilCued)
            text.Append(" ×?");
        else if (entry.Repeat.Count > 1)
            text.Append(CultureInfo.InvariantCulture, $" ×{entry.Repeat.Count}");
        if (entry.Transpose != 0)
            text.Append(CultureInfo.InvariantCulture, $" ({(entry.Transpose > 0 ? "+" : "−")}{Math.Abs(entry.Transpose)})");
        return text.ToString();
    }

    /// <summary>"76 bars · 4:13" at the entry tempo (ARR-13), with a "+" when an open repeat or hold makes the length open-ended.</summary>
    public static string Totals(SongPlan plan)
    {
        var plus = plan.HasOpenEnd ? "+" : string.Empty;
        var bars = plan.TotalBars == 1 ? "1 bar" : $"{plan.TotalBars.ToString(CultureInfo.InvariantCulture)} bars";
        return $"{bars} · {Format.Clock(plan.HeardDurationSec)}{plus}";
    }
}
