// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using RECompose.Core.Library;
using RECompose.Core.Music;

namespace RECompose.Core.Import;

/// <summary>Role, group and default output for one file name (SL-52).</summary>
public readonly record struct DetectedTrack(TrackRole Role, string Group, bool Recognised)
{
    /// <summary>Click and guide go to the Cue output, everything else to Main (PB-85).</summary>
    public string DefaultOutput => Role is TrackRole.Music ? "main" : "cue";
}

/// <summary>Import rules: role and group detection, lane order, BPM and key parsing (SL-52, SL-53).</summary>
public static partial class ImportRules
{
    /// <summary>Audio files the engine reads (WAV, AIFF, FLAC, Ogg Vorbis, MP3).</summary>
    public static IReadOnlySet<string> AudioExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".wave", ".aif", ".aiff", ".flac", ".ogg", ".mp3" };

    // Checked in this order; the first match wins. Acoustic comes before Electric so that
    // "Acoustic Gtr" isn't taken by "gtr", and Bass before Keys so "Synth Bass" is Bass.
    private static readonly (TrackRole Role, string Group, string[] Words)[] Rules =
    [
        (TrackRole.Click, TrackGroups.Click, ["click", "metronome", "met"]),
        (TrackRole.SpokenGuide, TrackGroups.Guide, ["spoken"]),
        (TrackRole.Guide, TrackGroups.Guide, ["guide", "cues", "cue"]),
        (TrackRole.Music, TrackGroups.Drums, ["drum", "drums", "kit", "overhead", "kick", "snare"]),
        (TrackRole.Music, TrackGroups.Percussion, ["perc", "shaker", "tamb", "loop"]),
        (TrackRole.Music, TrackGroups.Bass, ["bass", "sub", "808"]),
        (TrackRole.Music, TrackGroups.AcousticGuitar, ["ag", "acoustic"]),
        (TrackRole.Music, TrackGroups.Keys, ["keys", "key", "piano", "rhodes", "organ", "wurli", "synth", "pad"]),
        (TrackRole.Music, TrackGroups.ElectricGuitar, ["eg", "electric", "elec", "gtr", "guitar"]),
        (TrackRole.Music, TrackGroups.Vocals, ["vox", "vocal", "choir", "bgv", "alto", "tenor", "soprano"]),
        (TrackRole.Music, TrackGroups.Strings, ["strings", "horns", "brass", "orch", "violin", "cello"]),
    ];

    /// <summary>Detects role and group from a file name such as "Synth Bass.wav" (SL-52).</summary>
    public static DetectedTrack Detect(string fileName)
    {
        var tokens = Tokens(Path.GetFileNameWithoutExtension(fileName));
        foreach (var (role, group, words) in Rules)
        {
            foreach (var word in words)
            {
                // Whole tokens; longer words also match as a prefix ("drums", "vocals", "percussion").
                if (tokens.Any(t => t == word || (word.Length >= 4 && t.StartsWith(word, StringComparison.Ordinal))))
                    return new DetectedTrack(role, group, true);
            }
        }

        return new DetectedTrack(TrackRole.Music, TrackGroups.Other, false);
    }

    /// <summary>
    /// Lane order (SL-52): Click, Spoken guide, Guide, then the groups in order, natural-sorted
    /// by name within a group.
    /// </summary>
    public static List<Track> InLaneOrder(IEnumerable<Track> tracks)
    {
        var ordered = tracks.OrderBy(LaneRank).ThenBy(t => t.Name, NaturalComparer.Instance).ToList();
        return ordered.Select((t, i) => t with { Order = i }).ToList();
    }

    /// <summary>Where a track's lane group comes in the default lane order: click, spoken guide, guide, then the groups.</summary>
    public static int LaneRank(Track t)
    {
        if (t.Role == TrackRole.Click) return 0;
        if (t.Role == TrackRole.SpokenGuide) return 1;
        if (t.Role == TrackRole.Guide) return 2;
        var index = TrackGroups.LaneOrder.ToList().IndexOf(t.Group);
        return 10 + (index < 0 ? TrackGroups.LaneOrder.Count : index);
    }

    /// <summary>SL-53: "72 BPM", "72bpm", "120.5 - bpm". Returns null if there is none.</summary>
    public static double? ParseBpm(string text)
    {
        var match = BpmPattern().Match(text);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)
            ? bpm
            : null;
    }

    /// <summary>
    /// SL-53: a key token between separators ("Champion - Bb - 72bpm", "Song_F#m"). The last
    /// candidate wins, since titles come first in vendor names.
    /// </summary>
    public static Key? ParseKey(string text)
    {
        Key? found = null;
        foreach (Match match in KeyPattern().Matches(text))
            if (Key.TryParse(match.Groups[1].Value, out var key))
                found = key;
        return found;
    }

    /// <summary>Reads BPM and key from the folder name first, then from the file names.</summary>
    public static (double? Bpm, Key? Key) ParseMeta(string folderName, IEnumerable<string> fileNames)
    {
        var names = fileNames.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var bpm = ParseBpm(folderName) ?? names.Select(ParseBpm).FirstOrDefault(b => b is not null);
        var key = ParseKey(folderName) ?? names.Select(ParseKey).FirstOrDefault(k => k is not null);
        return (bpm, key);
    }

    /// <summary>Meter from text such as "3/4" or "6-8"; null if absent.</summary>
    public static (int BeatsPerBar, int BeatUnit)? ParseMeter(string text)
    {
        var match = MeterPattern().Match(text);
        return match.Success ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>A display name for a track from its file name: "Synth Bass".</summary>
    public static string TrackName(string fileName) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(fileName).Replace('_', ' '), @"\s+", " ").Trim();

    private static string[] Tokens(string name) =>
        TokenSplit().Split(name.ToLowerInvariant()).Where(t => t.Length > 0).ToArray();

    [GeneratedRegex(@"(\d{2,3}(?:\.\d)?)\s*-?\s*bpm", RegexOptions.IgnoreCase)]
    private static partial Regex BpmPattern();

    // A key between separators: start/space/-/_/(/[ before, and end/space/-/_/)/]/. after.
    [GeneratedRegex(@"(?:^|[\s\-_(\[])([A-G](?:#|b|♯|♭)?m?)(?=$|[\s\-_)\].])")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"(?<!\d)([2-9]|1[0-2])\s*[/\-]\s*(2|4|8|16)(?!\d)")]
    private static partial Regex MeterPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex TokenSplit();
}

/// <summary>Orders "Keys 2" before "Keys 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0) return byLength;
                var byDigits = string.CompareOrdinal(a, b);
                if (byDigits != 0) return byDigits;
            }
            else
            {
                var c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
