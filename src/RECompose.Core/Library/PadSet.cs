// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Music;

namespace RECompose.Core.Library;

/// <summary>Where a pad set's loop points come from.</summary>
public enum PadLoopMode
{
    /// <summary>Each file's own (a WAV <c>smpl</c> chunk), else 25 % to 90 % of it.</summary>
    File,

    /// <summary>25 % to 90 % of each file, ignoring loop points in the files (SL-46).</summary>
    Default,

    /// <summary><see cref="PadLoop.StartSec"/> to <see cref="PadLoop.EndSec"/> in every file, kept inside each file.</summary>
    Custom,
}

/// <summary>Loop points inside a pad file, in seconds (DM-60, SL-46).</summary>
public sealed record PadLoop
{
    public PadLoopMode Mode { get; init; } = PadLoopMode.Custom;

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

    /// <summary>The set's own volume, added to the pad volume, in dB.</summary>
    public double GainDb { get; init; }

    /// <summary>Keys the app made by transposing another key: pitch class to the pitch class it was made from.</summary>
    public Dictionary<int, int> Made { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public bool IsComplete => Enumerable.Range(0, 12).All(Samples.ContainsKey);

    public string? FileFor(int pitchClass) => Samples.TryGetValue(((pitchClass % 12) + 12) % 12, out var file) ? file : null;

    /// <summary>The pitch classes without a file, in button order.</summary>
    public IEnumerable<int> MissingKeys => Enumerable.Range(0, 12).Where(k => !Samples.ContainsKey(k));

    /// <summary>"12 keys", or "11 keys · E missing", "9 keys · 3 missing".</summary>
    public string MetaLine
    {
        get
        {
            var missing = MissingKeys.ToList();
            return missing.Count switch
            {
                0 => "12 keys",
                <= 2 => $"{Samples.Count} keys · {string.Join(", ", missing.Select(k => new Key(k).ToDisplayString()))} missing",
                _ => $"{Samples.Count} keys · {missing.Count} missing",
            };
        }
    }

    public PadSet Normalized() => this with { Samples = Samples ?? [], Made = Made ?? [] };
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

/// <summary>Pad set names and the pad keys (SL-47, UI-12); the import rules are <see cref="PadImport"/>.</summary>
public static class PadSetDetector
{
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
