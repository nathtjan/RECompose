// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using RECompose.Core.Library;
using RECompose.Core.Persistence;

namespace RECompose.Core.Services;

/// <summary>What importing a service file did (REL-45).</summary>
/// <param name="Service">The service to add, with a new id and its entries pointing at library songs.</param>
/// <param name="NewSongs">Songs the library did not have, to add; with the file's audio, their stems point at the copies made.</param>
/// <param name="UpdatedSongs">Library songs to save: replaced by the file's, or given the file's copies of stems they were missing.</param>
/// <param name="Missing">"Champion: Keys.wav" for every stem that is not on this computer.</param>
/// <param name="HadAudio">The file carried the stems.</param>
public sealed record ServiceImport(Service Service, IReadOnlyList<Song> NewSongs, IReadOnlyList<Song> UpdatedSongs, IReadOnlyList<string> Missing, bool HadAudio);

/// <summary>What to do with a song of the file that the library has, with other contents.</summary>
public enum SongChoice
{
    /// <summary>Keep the library's song; the service uses it.</summary>
    UseLibrary,

    /// <summary>Replace the library's song with the file's (the old one stays among its earlier versions).</summary>
    UseFile,

    /// <summary>Add the file's song as a copy, "Champion (imported)", for this service.</summary>
    KeepBoth,
}

/// <summary>A song of the file and the library's song with the same audio, which differ in <paramref name="Differences"/>.</summary>
public sealed record SongConflict(Song Incoming, Song Library, IReadOnlyList<string> Differences);

/// <summary>
/// Export and import of a service (REL-45): a zip with <c>service.json</c>, <c>songs/&lt;id&gt;.json</c> for every song it
/// uses, and with audio <c>media/&lt;song&gt;/&lt;track&gt;.&lt;ext&gt;</c> for every stem, stored as is; <c>manifest.json</c> says
/// which. Import finds each song in the library by its audio, asks (<see cref="Conflicts"/>) where the two differ, adds the
/// rest, and copies the file's stems where this computer lacks them.
/// </summary>
public static class ServiceExchange
{
    public const string Extension = ".recservice";

    /// <summary>The extension of exports before audio could go with them; they still import.</summary>
    public const string OldExtension = ".recompose-service";

    private const string ServiceEntryName = "service.json";
    private const string ManifestEntryName = "manifest.json";

    /// <summary>
    /// Writes the export; with <paramref name="openAudio"/> (a stem by its path, or null when it is not there) each stem goes in
    /// too. Returns "Champion: Keys.wav" for each stem that could not be included.
    /// </summary>
    public static IReadOnlyList<string> Export(Service service, IEnumerable<Song> songs, Stream output, Func<string, Stream?>? openAudio = null)
    {
        var missing = new List<string>();
        var used = songs.Where(s => service.Entries.Any(e => e.SongId == s.Id)).DistinctBy(s => s.Id).ToList();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        AddJson(zip, ManifestEntryName, new JsonObject { ["format"] = 1, ["audio"] = openAudio is not null });
        Add(zip, ServiceEntryName, service, RecomposeJsonContext.Default.Service);
        foreach (var song in used)
            Add(zip, $"songs/{song.Id}.json", song, RecomposeJsonContext.Default.Song);

        if (openAudio is null)
            return missing;
        foreach (var song in used)
        {
            foreach (var track in song.Tracks)
            {
                using var audio = openAudio(track.File);
                if (audio is null)
                {
                    missing.Add($"{song.Title}: {Path.GetFileName(track.File)}");
                    continue;
                }

                // Audio barely compresses; stored, it is written and read at disk speed.
                using var entry = zip.CreateEntry(MediaName(song.Id, track), CompressionLevel.NoCompression).Open();
                audio.CopyTo(entry);
            }
        }

        return missing;
    }

    private static void Add<T>(ZipArchive zip, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(stream, value, type);
    }

    private static void AddJson(ZipArchive zip, string name, JsonObject value)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new Utf8JsonWriter(stream);
        value.WriteTo(writer);
    }

    /// <summary>A stem's place in the file, made only of ids checked to be plain names and a plain extension.</summary>
    private static string MediaName(string songId, Track track) => $"media/{songId}/{track.Id}{SafeExtension(track.File)}";

    private static string SafeExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length is > 1 and <= 8 && ext[1..].All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// The file's songs that are in the library (the same audio) but differ from it: sections, variants, cues, mix or tracks.
    /// Each needs a <see cref="SongChoice"/> before <see cref="Import"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a service export.</exception>
    public static IReadOnlyList<SongConflict> Conflicts(Stream input, IReadOnlyList<Song> library)
    {
        var songs = Read(input, (_, _, songs) => songs);
        return songs
            .Select(s => (Incoming: s, Library: MatchFor(s, library)))
            .Where(p => p.Library is not null && !SameContent(p.Incoming, p.Library))
            .Select(p => new SongConflict(p.Incoming, p.Library!, Differences(p.Incoming, p.Library!)))
            .ToList();
    }

    /// <summary>What differs between two copies of a song, in words: "sections", "variants", "cues", "mix", "tracks"…</summary>
    public static IReadOnlyList<string> Differences(Song a, Song b)
    {
        // A part differs when taking it from b changes a.
        (string Name, Func<Song, Song> FromB)[] parts =
        [
            ("sections", s => s with { Sections = b.Sections }),
            ("variants", s => s with { Arrangements = b.Arrangements, DefaultArrangementId = b.DefaultArrangementId }),
            ("cues", s => s with { Cues = b.Cues, Automation = b.Automation }),
            ("mix", s => s with { MixPresets = b.MixPresets, DefaultMixPresetId = b.DefaultMixPresetId }),
            ("tracks", s => s with { Tracks = b.Tracks }),
            ("key, tempo or meter", s => s with { OriginalKey = b.OriginalKey, Bpm = b.Bpm, BeatsPerBar = b.BeatsPerBar, BeatUnit = b.BeatUnit, FirstDownbeatSec = b.FirstDownbeatSec }),
            ("title, notes or details", s => s with { Title = b.Title, Artist = b.Artist, Ccli = b.Ccli, Notes = b.Notes }),
        ];
        var original = Comparable(a);
        return [.. parts.Where(p => Comparable(p.FromB(a)) != original).Select(p => p.Name)];
    }

    /// <summary>
    /// Reads an export. <paramref name="mediaFolder"/> is where the file's stems are copied, one folder per song; null leaves
    /// them in the file. <paramref name="choices"/> says, by the file's song id, what to do where it conflicts with the library;
    /// a song without one uses the library's.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a service export.</exception>
    public static ServiceImport Import(Stream input, IReadOnlyList<Song> library, Func<string, bool> fileExists, Func<string> newId, string? mediaFolder = null,
        IReadOnlyDictionary<string, SongChoice>? choices = null) =>
        Read(input, (zip, service, songs) =>
        {
            var hadAudio = zip.Entries.Any(e => e.FullName.StartsWith("media/", StringComparison.Ordinal));
            return Match(zip, service, songs, library, fileExists, newId, hadAudio ? mediaFolder : null, hadAudio, choices ?? new Dictionary<string, SongChoice>());
        });

    private static T Read<T>(Stream input, Func<ZipArchive, Service, List<Song>, T> use)
    {
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry(ServiceEntryName) ?? throw new InvalidDataException("This file is not a RECompose service.");
            Service service;
            using (var stream = entry.Open())
                service = JsonSerializer.Deserialize(stream, RecomposeJsonContext.Default.Service) ?? throw new InvalidDataException("This file is not a RECompose service.");
            var songs = new List<Song>();
            foreach (var songEntry in zip.Entries.Where(e => e.FullName.StartsWith("songs/", StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal)))
            {
                using var stream = songEntry.Open();
                if (JsonSerializer.Deserialize(stream, RecomposeJsonContext.Default.Song) is { } song)
                    songs.Add(song.Normalized());
            }

            // Song and track ids become file names in the data folder; one from a shared file must not point outside it.
            if (songs.Any(s => !IsSafeId(s.Id) || s.Tracks.Any(t => !IsSafeId(t.Id))))
                throw new InvalidDataException("This service file is damaged.");
            return use(zip, service, songs);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This service file is damaged.", ex);
        }
    }

    /// <summary>The library's copy of a song: the same audio (hash), or the same id when either has no hash yet. Titles never match.</summary>
    private static Song? MatchFor(Song song, IReadOnlyList<Song> library) =>
        (song.AudioHash is { } hash ? library.FirstOrDefault(s => s.AudioHash == hash) : null)
        ?? library.FirstOrDefault(s => s.Id == song.Id && (s.AudioHash is null || song.AudioHash is null));

    /// <summary>
    /// The same song as far as a service can tell: everything but what belongs to one computer (the id, where the stems
    /// are, when it was made or saved) and what the audio gives anyway (click and guide analysis).
    /// </summary>
    private static bool SameContent(Song a, Song b) => Comparable(a) == Comparable(b);

    private static string Comparable(Song s) => JsonSerializer.Serialize(s with
    {
        Id = string.Empty,
        Source = new SongSource { Path = string.Empty },
        Tracks = [.. s.Tracks.Select(t => t with { File = string.Empty })],
        ClickAnalysis = null,
        GuideAnalysis = null,
        CreatedAt = default,
        UpdatedAt = default,
    }, RecomposeJsonContext.Default.Song);

    private static ServiceImport Match(ZipArchive zip, Service service, List<Song> songs, IReadOnlyList<Song> library, Func<string, bool> fileExists,
        Func<string> newId, string? mediaFolder, bool hadAudio, IReadOnlyDictionary<string, SongChoice> choices)
    {
        var map = new Dictionary<string, string>();
        var added = new List<Song>();
        var updated = new List<Song>();
        var missing = new List<string>();
        foreach (var song in songs)
        {
            var match = MatchFor(song, library);
            var choice = match is null ? SongChoice.KeepBoth
                : SameContent(song, match) ? SongChoice.UseLibrary
                : choices.GetValueOrDefault(song.Id, SongChoice.UseLibrary);

            // Each stem plays from this computer when it is here, else from the file's copy.
            Song used;
            switch (choice)
            {
                case SongChoice.UseLibrary:
                    used = match! with { Tracks = [.. match!.Tracks.Select(t => Locate(t, song, null, match.Id))] };
                    if (!used.Tracks.SequenceEqual(match.Tracks))
                        updated.Add(used);
                    break;
                case SongChoice.UseFile: // the library's song becomes the file's, under its own id so other services keep it
                    used = song with { Id = match!.Id, Source = match.Source, CreatedAt = match.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow };
                    used = used with { Tracks = [.. used.Tracks.Select(t => Locate(t, song, match, match.Id))] };
                    updated.Add(used);
                    break;
                default: // not in the library, or kept as a copy next to the library's
                    used = match is null ? song : song with { Id = newId(), Title = ImportedTitle(song.Title, library.Select(s => s.Title).Concat(added.Select(s => s.Title))), CreatedAt = DateTimeOffset.UtcNow };
                    used = used with { Tracks = [.. used.Tracks.Select(t => Locate(t, song, null, used.Id))] };
                    added.Add(used);
                    break;
            }

            map[song.Id] = used.Id;
            missing.AddRange(used.Tracks.Where(t => !t.Hidden && !fileExists(t.File)).Select(t => $"{used.Title}: {Path.GetFileName(t.File)}"));
        }

        Track Locate(Track t, Song fromFile, Song? other, string mediaSongId)
        {
            if (fileExists(t.File))
                return t;
            var here = other?.Tracks.FirstOrDefault(o => o.Id == t.Id) ?? other?.Tracks.FirstOrDefault(o => o.Name == t.Name);
            if (here is not null && fileExists(here.File))
                return t with { File = here.File };
            var source = fromFile.Tracks.FirstOrDefault(s => s.Id == t.Id) ?? fromFile.Tracks.FirstOrDefault(s => s.Name == t.Name);
            return mediaFolder is not null && source is not null && Extract(zip, fromFile.Id, source, mediaFolder, mediaSongId, t) is { } copy ? t with { File = copy } : t;
        }

        var entries = service.Entries
            .Where(e => map.ContainsKey(e.SongId))
            .Select(e => e with { SongId = map[e.SongId] })
            .ToList();
        var imported = service.Normalized() with { Id = newId(), Entries = entries, LastCheck = null, UpdatedAt = DateTimeOffset.UtcNow };
        return new ServiceImport(imported, added, updated, missing, hadAudio);
    }

    /// <summary>Copies a stem out of the file into <c>mediaFolder/&lt;song&gt;/&lt;track&gt;.&lt;ext&gt;</c>; null when the file lacks it.</summary>
    private static string? Extract(ZipArchive zip, string exportedSongId, Track source, string mediaFolder, string songId, Track target)
    {
        if (zip.GetEntry(MediaName(exportedSongId, source)) is not { } entry)
            return null;
        var folder = Path.Combine(mediaFolder, songId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, target.Id + SafeExtension(source.File));
        entry.ExtractToFile(path, overwrite: true);
        return path;
    }

    /// <summary>
    /// A name none of <paramref name="taken"/> has (case-insensitive): <paramref name="wanted"/> itself when free, else its base
    /// with the first free number, "Sunday (2)", "Sunday (3)"… A wanted name that is itself a numbered copy counts up from its
    /// base, so "Sunday (2)" taken gives "Sunday (3)", never "Sunday (2) (2)".
    /// </summary>
    public static string UniqueName(string wanted, IEnumerable<string> taken)
    {
        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        wanted = wanted.Trim();
        if (!names.Contains(wanted))
            return wanted;
        // "Sunday (2)" is a numbered copy only when "Sunday" exists; "Psalm (23)" alone is a title.
        var match = System.Text.RegularExpressions.Regex.Match(wanted, @"^(.*\S) \((\d+)\)$");
        var baseName = match.Success && names.Contains(match.Groups[1].Value) ? match.Groups[1].Value : wanted;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} ({n.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private const string ImportedSuffix = " (imported)";

    /// <summary>"Champion (imported)", or with a number when the library already has that; never "(imported) (imported)".</summary>
    private static string ImportedTitle(string title, IEnumerable<string> taken)
    {
        var baseTitle = System.Text.RegularExpressions.Regex.Replace(title.Trim(), @" \(imported\)( \(\d+\))?$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return UniqueName(baseTitle + ImportedSuffix, taken);
    }

    private static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id) && id == Path.GetFileName(id) && id is not ("." or "..") && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
