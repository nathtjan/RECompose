// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Timing;

namespace RECompose.Core.Library;

/// <summary>Gain, mute and solo of one track in a mix preset (DM-13).</summary>
public sealed record TrackMix
{
    public double GainDb { get; init; }
    public bool Mute { get; init; }
    public bool Solo { get; init; }
}

/// <summary>A named mix (DM-13). Milestone 1 uses only "Default".</summary>
public sealed record MixPreset
{
    public required string Id { get; init; }
    public string Name { get; init; } = "Default";
    public double MasterGainDb { get; init; }
    public Dictionary<string, TrackMix> Tracks { get; init; } = [];

    public TrackMix For(string trackId) => Tracks.TryGetValue(trackId, out var mix) ? mix : new TrackMix();
}

/// <summary>Where the audio came from (DM-10).</summary>
public sealed record SongSource
{
    public string Type { get; init; } = "folder";
    public required string Path { get; init; }
}

/// <summary>A library song: stems and metadata (DM-10). Sections and arrangements arrive in M2/M3.</summary>
public sealed record Song
{
    public int SchemaVersion { get; init; } = 2;
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Artist { get; init; }
    public string? Ccli { get; init; }
    public required SongSource Source { get; init; }

    /// <summary>Key of the stems, spelled as in DM-11 ("Bb", "F#m"), or null if unknown.</summary>
    public string? OriginalKey { get; init; }

    public double Bpm { get; init; } = 120;
    public int BeatsPerBar { get; init; } = 4;
    public int BeatUnit { get; init; } = 4;
    public double FirstDownbeatSec { get; init; }
    public double DurationSec { get; init; }

    /// <summary>True when BPM and meter were assumed rather than read (SEC-1 step 4).</summary>
    public bool TempoAssumed { get; init; }

    public List<Track> Tracks { get; init; } = [];
    public List<MixPreset> MixPresets { get; init; } = [];
    public string? DefaultMixPresetId { get; init; }

    /// <summary>Fingerprint of the audio files, for duplicate detection (SL-55).</summary>
    public string? AudioHash { get; init; }

    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public TempoGrid Grid => new(Bpm, BeatsPerBar, FirstDownbeatSec);

    public MixPreset DefaultMix =>
        MixPresets.FirstOrDefault(p => p.Id == DefaultMixPresetId) ?? MixPresets.FirstOrDefault()
        ?? new MixPreset { Id = "default" };

    /// <summary>"Bb · 72 BPM · 3/4 · 12 tracks" (SL-56). Keys use real accidentals.</summary>
    public string MetaLine
    {
        get
        {
            var key = Music.Key.TryParse(OriginalKey, out var k) ? k.ToDisplayString() : "Key ?";
            return $"{key} · {Format.Bpm(Bpm)} BPM · {BeatsPerBar}/{BeatUnit} · {Tracks.Count} tracks";
        }
    }
}

/// <summary>One row of library.json.</summary>
public sealed record SongSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Artist { get; init; }
    public string MetaLine { get; init; } = string.Empty;
    public string? AudioHash { get; init; }

    public static SongSummary From(Song song) => new()
    {
        Id = song.Id,
        Title = song.Title,
        Artist = song.Artist,
        MetaLine = song.MetaLine,
        AudioHash = song.AudioHash,
    };
}

/// <summary>library.json (DM-90).</summary>
public sealed record LibraryIndex
{
    public int SchemaVersion { get; init; } = 2;
    public List<SongSummary> Songs { get; init; } = [];
}
