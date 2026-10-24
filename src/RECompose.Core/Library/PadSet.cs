// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Import;
using RECompose.Core.Music;

namespace RECompose.Core.Library;

/// <summary>Loop points inside a pad file, in seconds (DM-60, SL-46).</summary>
public sealed record PadLoop
{
    public double StartSec { get; init; }
    public double EndSec { get; init; }

    /// <summary>Equal-power crossfade at the loop seam, in seconds.</summary>
    public double CrossfadeSec { get; init; } = DefaultCrossfadeSec;

    public const double DefaultCrossfadeSec = 2;

    /// <summary>SL-46 without embedded loop points: 25 % to 90 % of the file.</summary>
    public static PadLoop Default(double durationSec) => new()
    {
        StartSec = durationSec * 0.25,
        EndSec = durationSec * 0.90,
        CrossfadeSec = Math.Min(DefaultCrossfadeSec, durationSec * 0.65 / 2),
    };
}

/// <summary>A pad set: one file per pitch class (DM-60).</summary>
public sealed record PadSet
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }

    /// <summary>Absolute path of the file for each pitch class 0..11 (C = 0).</summary>
    public Dictionary<int, string> Samples { get; init; } = [];

    /// <summary>Loop points shared by every file, or null for each file's own (embedded or the SL-46 default).</summary>
    public PadLoop? Loop { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public bool IsComplete => Enumerable.Range(0, 12).All(Samples.ContainsKey);

    public string? FileFor(int pitchClass) => Samples.TryGetValue(((pitchClass % 12) + 12) % 12, out var file) ? file : null;

    public PadSet Normalized() => this with { Samples = Samples ?? [] };
}

/// <summary>padsets.json (DM-90).</summary>
public sealed record PadLibrary
{
    public int SchemaVersion { get; init; } = 2;
    public List<PadSet> PadSets { get; init; } = [];

    /// <summary>The selected pad set (SL-41), or null for the first one.</summary>
    public string? CurrentId { get; init; }

    public PadSet? Current => PadSets.FirstOrDefault(p => p.Id == CurrentId) ?? PadSets.FirstOrDefault();
}

/// <summary>
/// SL-47: a folder whose audio files are named by key and cover all twelve pitch classes is a pad set, not a
/// song. Enharmonic names count as the same pitch class (C# = Db). A folder with a click or guide track is a song.
/// </summary>
public static class PadSetDetector
{
    /// <summary>The pad set in <paramref name="files"/> (absolute paths), or null when the folder is a song.</summary>
    public static PadSet? Detect(string folder, IEnumerable<string> files)
    {
        var audio = files
            .Where(f => ImportRules.AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance)
            .ToList();
        if (audio.Count < 12)
            return null;
        if (audio.Any(f => ImportRules.Detect(Path.GetFileName(f)).Role != TrackRole.Music))
            return null;

        var samples = new Dictionary<int, string>();
        var keyed = new List<string>();
        foreach (var file in audio)
        {
            if (ImportRules.ParseKey(Path.GetFileNameWithoutExtension(file)) is not { } key)
                continue;
            keyed.Add(file);
            samples.TryAdd(key.PitchClass, file); // the first name wins when two spell the same pitch
        }

        if (samples.Count < 12)
            return null;

        return new PadSet
        {
            Id = PadSet.NewId(),
            Name = CommonName(keyed.Select(f => Path.GetFileNameWithoutExtension(f)!), Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))),
            Folder = folder,
            Samples = samples,
        };
    }

    /// <summary>The common prefix of the file names without separators ("OB Pad"), else the folder name.</summary>
    public static string CommonName(IEnumerable<string> names, string folderName)
    {
        string? prefix = null;
        foreach (var name in names)
        {
            if (prefix is null)
            {
                prefix = name;
                continue;
            }

            var n = 0;
            while (n < prefix.Length && n < name.Length && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(name[n]))
                n++;
            prefix = prefix[..n];
        }

        var trimmed = (prefix ?? string.Empty).Trim(' ', '-', '_', '(', '[', '.');
        return trimmed.Length > 0 ? trimmed : folderName;
    }

    /// <summary>The twelve pad keys in button order, with real accidentals: C D♭ D E♭ E F / F♯ G A♭ A B♭ B (UI-12).</summary>
    public static IReadOnlyList<Key> ButtonKeys { get; } = Enumerable.Range(0, 12).Select(pc => new Key(pc)).ToList();
}
