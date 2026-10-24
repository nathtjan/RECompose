// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RECompose.Core.Library;
using RECompose.Core.Services;
using RECompose.Core.Settings;

namespace RECompose.Core.Persistence;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Song))]
[JsonSerializable(typeof(LibraryIndex))]
[JsonSerializable(typeof(Service))]
[JsonSerializable(typeof(PadLibrary))]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class RecomposeJsonContext : JsonSerializerContext;

/// <summary>
/// The data folder (DM-90): library.json, songs/&lt;id&gt;.json, services/&lt;id&gt;.json, padsets.json, settings.json, cache/.
/// Every write is atomic (temp file, then rename), so a crash never leaves half a file.
/// </summary>
public sealed class DataStore(string root)
{
    public string Root { get; } = root;
    public string SongsFolder => Path.Combine(Root, "songs");
    public string ServicesFolder => Path.Combine(Root, "services");
    public string PeaksFolder => Path.Combine(Root, "cache", "peaks");

    /// <summary>%APPDATA%\RECompose on Windows, ~/Library/Application Support/RECompose on macOS.</summary>
    public static DataStore Default()
    {
        var baseFolder = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        var overridden = Environment.GetEnvironmentVariable("RECOMPOSE_DATA");
        return new DataStore(string.IsNullOrEmpty(overridden) ? Path.Combine(baseFolder, "RECompose") : overridden);
    }

    public LibraryIndex LoadLibrary() =>
        Read(Path.Combine(Root, "library.json"), RecomposeJsonContext.Default.LibraryIndex) ?? new LibraryIndex();

    public void SaveLibrary(LibraryIndex library) =>
        Write(Path.Combine(Root, "library.json"), library, RecomposeJsonContext.Default.LibraryIndex);

    public Song? LoadSong(string id) => Read(SongPath(id), RecomposeJsonContext.Default.Song)?.Normalized();

    public void SaveSong(Song song) => Write(SongPath(song.Id), song, RecomposeJsonContext.Default.Song);

    public void DeleteSong(string id)
    {
        if (File.Exists(SongPath(id)))
            File.Delete(SongPath(id));
    }

    /// <summary>Every service in the data folder; damaged files are skipped.</summary>
    public List<Service> LoadServices()
    {
        if (!Directory.Exists(ServicesFolder))
            return [];
        return Directory.EnumerateFiles(ServicesFolder, "*.json")
            .Select(path => Read(path, RecomposeJsonContext.Default.Service))
            .OfType<Service>()
            .Select(s => s.Normalized())
            .ToList();
    }

    public Service? LoadService(string id) => Read(ServicePath(id), RecomposeJsonContext.Default.Service)?.Normalized();

    public void SaveService(Service service) => Write(ServicePath(service.Id), service, RecomposeJsonContext.Default.Service);

    public void DeleteService(string id)
    {
        if (File.Exists(ServicePath(id)))
            File.Delete(ServicePath(id));
    }

    public PadLibrary LoadPads()
    {
        var pads = Read(Path.Combine(Root, "padsets.json"), RecomposeJsonContext.Default.PadLibrary) ?? new PadLibrary();
        return pads with { PadSets = (pads.PadSets ?? []).Where(p => p is not null).Select(p => p.Normalized()).ToList() };
    }

    public void SavePads(PadLibrary pads) => Write(Path.Combine(Root, "padsets.json"), pads, RecomposeJsonContext.Default.PadLibrary);

    public AppSettings LoadSettings() =>
        Read(Path.Combine(Root, "settings.json"), RecomposeJsonContext.Default.AppSettings) ?? new AppSettings();

    public void SaveSettings(AppSettings settings) =>
        Write(Path.Combine(Root, "settings.json"), settings, RecomposeJsonContext.Default.AppSettings);

    public string PeaksPath(string songId, string trackId) => Path.Combine(PeaksFolder, songId, trackId + ".peaks");

    private string SongPath(string id) => Path.Combine(SongsFolder, id + ".json");

    private string ServicePath(string id) => Path.Combine(ServicesFolder, id + ".json");

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (JsonException)
        {
            return null; // a damaged file reads as missing rather than crashing the app
        }
    }

    private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, value, type);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// A fingerprint of a set of audio files for duplicate detection (SL-55): file sizes and
    /// the first and last 64 KB of each file, in name order. Fast on a 16-stem folder.
    /// </summary>
    public static string AudioHash(IEnumerable<string> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var file in files.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(file);
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file).ToLowerInvariant() + ":" + stream.Length + ";"));
            var read = stream.Read(buffer, 0, buffer.Length);
            sha.AppendData(buffer, 0, read);
            if (stream.Length > buffer.Length * 2)
            {
                stream.Seek(-buffer.Length, SeekOrigin.End);
                read = stream.Read(buffer, 0, buffer.Length);
                sha.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
