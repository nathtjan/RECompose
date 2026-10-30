// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace RECompose.Core.Logging;

/// <summary>One line of the LOG tab (AUTO-50): when, its filter chip, and the text as shown ("Section Chorus 2: MIDI note E3").</summary>
public sealed record LogEntry(DateTime Time, string Chip, string Text)
{
    /// <summary>"HH:MM:SS  text".</summary>
    public string Line => $"{Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {Text}";
}

/// <summary>The LOG tab's filter chips (AUTO-50) and how a log category maps to them.</summary>
public static class LogChips
{
    public const string All = "All";
    public const string Errors = "Errors";

    public static IReadOnlyList<string> Chips { get; } =
        [All, "Voice", "Pedal", "Jumps", "Sections", "Cues", "Guide", "Tracks", "Transport", "Assistant", Errors];

    /// <summary>The chip of a category the app logs under; other categories show under All only.</summary>
    public static string Of(string category) => category switch
    {
        "Voice" => "Voice",
        "Controls" or "Pedal" => "Pedal",
        "Live" or "Jumps" => "Jumps",
        "Sections" => "Sections",
        "Cues" => "Cues",
        "Guide" => "Guide",
        "Tracks" => "Tracks",
        "Transport" or "Perform" or "Recovery" or "Audio" => "Transport",
        "Assistant" => "Assistant",
        "Errors" => Errors,
        _ => string.Empty,
    };

    /// <summary>The chip shows the entry: All shows everything; the search matches the text, ignoring case.</summary>
    public static bool Shows(LogEntry entry, string chip, string? search) =>
        (chip == All || entry.Chip == chip)
        && (string.IsNullOrWhiteSpace(search) || entry.Text.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Export (AUTO-50): text lines, or CSV with time, category and message.</summary>
    public static string Export(IEnumerable<LogEntry> entries, bool csv)
    {
        var sb = new StringBuilder();
        if (csv)
            sb.Append("time,category,message\n");
        foreach (var e in entries)
        {
            if (csv)
                sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(Csv(e.Chip)).Append(',').Append(Csv(e.Text)).Append('\n');
            else
                sb.Append(e.Line).Append('\n');
        }

        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n']) < 0 ? s : "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>One song as it went during the service (AUTO-60): what was planned and what was heard, with the log lines in between.</summary>
public sealed record ReportSong
{
    public required string Title { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Tempo { get; init; } = string.Empty;
    public string Arrangement { get; init; } = string.Empty;
    public DateTime Started { get; init; }
    public IReadOnlyList<string> Planned { get; init; } = [];

    /// <summary>The items heard, in order, with the time each began.</summary>
    public List<(DateTime At, string Label)> Played { get; init; } = [];

    /// <summary>The log lines while it was loaded.</summary>
    public List<LogEntry> Entries { get; init; } = [];
}

/// <summary>The service report (AUTO-60): Markdown for review after the service.</summary>
public static class ServiceReport
{
    public static string Build(string serviceName, DateTime started, DateTime ended, IReadOnlyList<ReportSong> songs)
    {
        static string T(DateTime t) => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        var duration = ended - started;
        sb.Append("# ").Append(serviceName).Append('\n').Append('\n');
        sb.Append(started.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)).Append(" · ").Append(T(started)).Append("–").Append(T(ended))
            .Append(" · ").Append(((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture)).Append(" min\n");

        var changes = 0;
        var dropouts = 0;
        var fallbacks = 0;
        foreach (var song in songs)
        {
            sb.Append("\n## ").Append(song.Title).Append('\n').Append('\n');
            var meta = new[] { song.Key, song.Tempo, song.Arrangement }.Where(s => !string.IsNullOrEmpty(s));
            sb.Append(string.Join(" · ", meta)).Append(" · started ").Append(T(song.Started)).Append('\n').Append('\n');
            sb.Append("- Planned: ").Append(song.Planned.Count == 0 ? "—" : string.Join(", ", song.Planned)).Append('\n');
            sb.Append("- As played: ").Append(song.Played.Count == 0 ? "—" : string.Join(", ", Collapse(song.Played.Select(p => p.Label)))).Append('\n');

            var live = song.Entries.Where(e => e.Chip is "Jumps" || (e.Chip == "Voice" && !e.Text.StartsWith("Voice ignored", StringComparison.Ordinal))
                                               || (e.Chip == "Pedal" && e.Text.StartsWith("Pedal", StringComparison.Ordinal))).ToList();
            changes += song.Entries.Count(e => e.Chip == "Jumps");
            Section(sb, "Live changes", live);
            Section(sb, "Ignored voice commands", song.Entries.Where(e => e.Text.StartsWith("Voice ignored", StringComparison.Ordinal)).ToList());
            Section(sb, "Errors and warnings", song.Entries.Where(e => e.Chip == LogChips.Errors || e.Text.StartsWith("File missing", StringComparison.Ordinal)).ToList());
            dropouts += song.Entries.Count(e => e.Text.Contains("device lost", StringComparison.OrdinalIgnoreCase) || e.Text.Contains("xrun", StringComparison.OrdinalIgnoreCase));
            fallbacks += song.Entries.Count(e => e.Text.Contains("Live key", StringComparison.Ordinal));
        }

        sb.Append("\n## Summary\n\n");
        sb.Append("- Songs: ").Append(songs.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("- Live changes: ").Append(changes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("- Audio dropouts and failovers: ").Append(dropouts.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("- Live key fallbacks: ").Append(fallbacks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<LogEntry> entries)
    {
        if (entries.Count == 0)
            return;
        sb.Append("- ").Append(title).Append(":\n");
        foreach (var e in entries)
            sb.Append("  - ").Append(e.Line).Append('\n');
    }

    /// <summary>Further passes of a vamp shown once with a count: "Chorus 2 ×3".</summary>
    private static IEnumerable<string> Collapse(IEnumerable<string> labels)
    {
        string? last = null;
        var count = 0;
        foreach (var label in labels)
        {
            if (label == last)
            {
                count++;
                continue;
            }

            if (last is not null)
                yield return count > 1 ? $"{last} ×{count.ToString(CultureInfo.InvariantCulture)}" : last;
            last = label;
            count = 1;
        }

        if (last is not null)
            yield return count > 1 ? $"{last} ×{count.ToString(CultureInfo.InvariantCulture)}" : last;
    }
}
