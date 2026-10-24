// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Import;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Engine;

namespace RECompose.Presentation.Services;

/// <summary>One file found in an import folder, before the user confirms (SL-51).</summary>
public sealed record ImportCandidate(string File, string Name, DetectedTrack Detected, AudioFileInfo Info);

/// <summary>What a folder scan found, ready for the review dialog.</summary>
public sealed record ImportScan(
    string Folder,
    string Title,
    double? Bpm,
    string? Key,
    (int BeatsPerBar, int BeatUnit)? Meter,
    IReadOnlyList<ImportCandidate> Files,
    string AudioHash,
    SongSummary? Duplicate);

/// <summary>Folder import (SL-50…55): scan, probe, detect, check duplicates, build the song, compute peaks.</summary>
public sealed class ImportService(DataStore store, EngineTools tools)
{
    /// <summary>Peaks resolution: about 5 ms per bucket at 48 kHz.</summary>
    public const int FramesPerBucket = 256;

    /// <summary>Audio files in the folder and one level of subfolders, in name order.</summary>
    public static List<string> FindAudioFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true })
            .Where(f => ImportRules.AudioExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal)) // macOS resource forks
            .OrderBy(f => f, NaturalComparer.Instance)
            .ToList();

    public async Task<ImportScan> ScanAsync(string folder, IEnumerable<SongSummary> library, CancellationToken ct = default)
    {
        var files = FindAudioFiles(folder);
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

        // Probe in parallel, a few at a time, each in its own short-lived process.
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        var probes = files.Select(async file =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await tools.ProbeAsync(file, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var infos = await Task.WhenAll(probes).ConfigureAwait(false);

        var candidates = files.Select((file, i) => new ImportCandidate(file, ImportRules.TrackName(file), ImportRules.Detect(file), infos[i])).ToList();
        var (bpm, key) = ImportRules.ParseMeta(folderName, files.Select(Path.GetFileName).OfType<string>());
        var meter = ImportRules.ParseMeter(folderName);
        var hash = files.Count > 0 ? await Task.Run(() => DataStore.AudioHash(files), ct).ConfigureAwait(false) : string.Empty;
        var duplicate = library.FirstOrDefault(s => s.AudioHash is not null && s.AudioHash == hash);

        return new ImportScan(folder, folderName, bpm, key?.ToString(), meter, candidates, hash, duplicate);
    }

    /// <summary>Builds and saves the song from the reviewed choices.</summary>
    public Song CreateSong(ImportScan scan, string title, double? bpm, string? key, int beatsPerBar, int beatUnit, IEnumerable<(ImportCandidate File, TrackRole Role, string Group, string Output)> choices)
    {
        var tracks = choices
            .Where(c => c.File.Info.Ok)
            .Select(c => new Track
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = c.File.Name,
                File = c.File.File,
                Role = c.Role,
                Group = c.Role switch { TrackRole.Click => TrackGroups.Click, TrackRole.Guide or TrackRole.SpokenGuide => TrackGroups.Guide, _ => c.Group },
                Channels = c.File.Info.Channels,
                SampleRate = c.File.Info.SampleRate,
                Frames = c.File.Info.Frames,
                Output = c.Output,
            });
        var ordered = ImportRules.InLaneOrder(tracks);

        var mix = new MixPreset { Id = "default", Name = "Default" };
        var song = new Song
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? scan.Title : title.Trim(),
            Source = new SongSource { Path = scan.Folder },
            OriginalKey = key,
            Bpm = bpm ?? 120,
            BeatsPerBar = beatsPerBar,
            BeatUnit = beatUnit,
            TempoAssumed = bpm is null,
            DurationSec = ordered.Count == 0 ? 0 : ordered.Max(t => t.DurationSec),
            Tracks = ordered,
            MixPresets = [mix],
            DefaultMixPresetId = mix.Id,
            AudioHash = scan.AudioHash,
        };

        store.SaveSong(song);
        var library = store.LoadLibrary();
        library.Songs.RemoveAll(s => s.Id == song.Id);
        library.Songs.Add(SongSummary.From(song));
        store.SaveLibrary(library);
        return song;
    }

    /// <summary>Computes missing peaks files for a song (SL-54), a few at a time. Reports each finished track.</summary>
    public async Task ComputePeaksAsync(Song song, Action<string> trackDone, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
        var jobs = song.Tracks.Select(async track =>
        {
            var path = store.PeaksPath(song.Id, track.Id);
            if (File.Exists(path))
            {
                trackDone(track.Id);
                return;
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(track.File) && await tools.PeaksAsync(track.File, path, FramesPerBucket, ct).ConfigureAwait(false))
                    trackDone(track.Id);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(jobs).ConfigureAwait(false);
    }
}
