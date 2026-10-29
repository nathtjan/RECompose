// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text.Json;
using RECompose.Core.Library;
using RECompose.Core.Persistence;

namespace RECompose.Core.Services;

/// <summary>What importing a <c>.recompose-service</c> file did (REL-45).</summary>
/// <param name="Service">The service to add, with a new id and its entries pointing at library songs.</param>
/// <param name="NewSongs">Songs the library did not have, to add (their stems where the export said).</param>
/// <param name="Missing">"Champion: Keys.wav" for every stem that is not on this computer.</param>
public sealed record ServiceImport(Service Service, IReadOnlyList<Song> NewSongs, IReadOnlyList<string> Missing);

/// <summary>
/// Export and import of a service (REL-45): a zip with <c>service.json</c> and <c>songs/&lt;id&gt;.json</c> for every song it
/// uses, no audio. Import finds each song in the library by id, then by audio hash, then by title, and adds the rest.
/// </summary>
public static class ServiceExchange
{
    public const string Extension = ".recompose-service";
    private const string ServiceEntryName = "service.json";

    public static void Export(Service service, IEnumerable<Song> songs, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, ServiceEntryName, service, RecomposeJsonContext.Default.Service);
        foreach (var song in songs.Where(s => service.Entries.Any(e => e.SongId == s.Id)).DistinctBy(s => s.Id))
            Add(zip, $"songs/{song.Id}.json", song, RecomposeJsonContext.Default.Song);
    }

    private static void Add<T>(ZipArchive zip, string name, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(stream, value, type);
    }

    /// <exception cref="InvalidDataException">The file is not a service export.</exception>
    public static ServiceImport Import(Stream input, IReadOnlyList<Song> library, Func<string, bool> fileExists, Func<string> newId)
    {
        Service service;
        var songs = new List<Song>();
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry(ServiceEntryName) ?? throw new InvalidDataException("This file is not a RECompose service.");
            using (var stream = entry.Open())
                service = JsonSerializer.Deserialize(stream, RecomposeJsonContext.Default.Service) ?? throw new InvalidDataException("This file is not a RECompose service.");
            foreach (var songEntry in zip.Entries.Where(e => e.FullName.StartsWith("songs/", StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal)))
            {
                using var stream = songEntry.Open();
                if (JsonSerializer.Deserialize(stream, RecomposeJsonContext.Default.Song) is { } song)
                    songs.Add(song.Normalized());
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This service file is damaged.", ex);
        }

        // Song and track ids become file names in the data folder; one from a shared file must not point outside it.
        if (songs.Any(s => !IsSafeId(s.Id) || s.Tracks.Any(t => !IsSafeId(t.Id))))
            throw new InvalidDataException("This service file is damaged.");

        var map = new Dictionary<string, string>();
        var added = new List<Song>();
        var missing = new List<string>();
        foreach (var song in songs)
        {
            var match = library.FirstOrDefault(s => s.Id == song.Id)
                ?? library.FirstOrDefault(s => song.AudioHash is not null && s.AudioHash == song.AudioHash)
                ?? library.FirstOrDefault(s => string.Equals(s.Title, song.Title, StringComparison.OrdinalIgnoreCase));
            var used = match ?? song;
            if (match is null)
                added.Add(song);
            map[song.Id] = used.Id;
            missing.AddRange(used.Tracks.Where(t => !t.Hidden && !fileExists(t.File)).Select(t => $"{used.Title}: {Path.GetFileName(t.File)}"));
        }

        var entries = service.Entries
            .Where(e => map.ContainsKey(e.SongId))
            .Select(e => e with { SongId = map[e.SongId] })
            .ToList();
        var imported = service.Normalized() with { Id = newId(), Entries = entries, LastCheck = null, UpdatedAt = DateTimeOffset.UtcNow };
        return new ServiceImport(imported, added, missing);
    }

    private static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id) && id == Path.GetFileName(id) && id is not ("." or "..") && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
