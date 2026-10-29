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
[JsonSerializable(typeof(Recovery.Journal))]
[JsonSerializable(typeof(Recovery.AppState))]
[JsonSerializable(typeof(Rendering.RenderIndex))]
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

    /// <summary>Rendered keys and tempos (REL-10) and their index.</summary>
    public string RenderFolder => Path.Combine(Root, "cache", "render");

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

    public void SaveSong(Song song)
    {
        KeepVersion(SongPath(song.Id), song.Id);
        Write(SongPath(song.Id), song, RecomposeJsonContext.Default.Song);
    }

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

    public void SaveService(Service service)
    {
        KeepVersion(ServicePath(service.Id), service.Id);
        Write(ServicePath(service.Id), service, RecomposeJsonContext.Default.Service);
    }

    // ---- Version history (DM-90, REL-45) ---------------------------------------------------------------

    /// <summary>At most one version per document in this time.</summary>
    public static readonly TimeSpan VersionInterval = TimeSpan.FromMinutes(5);

    public const int VersionsKept = 20;

    /// <summary>The clock for versions; tests set it.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public string VersionsFolder => Path.Combine(Root, "versions");

    /// <summary>
    /// Before a document is replaced, the file as it was goes to versions/&lt;id&gt;/&lt;timestamp&gt;.json, unless the newest
    /// version is less than five minutes old; the 20 newest are kept. A failure here never stops the save.
    /// </summary>
    private void KeepVersion(string path, string id)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var folder = Path.Combine(VersionsFolder, id);
            var versions = ListVersions(id);
            var now = Now();
            if (versions.Count > 0 && now - versions[0].At < VersionInterval)
                return;
            Directory.CreateDirectory(folder);
            File.Copy(path, Path.Combine(folder, now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + ".json"), overwrite: true);
            foreach (var old in ListVersions(id).Skip(VersionsKept))
                File.Delete(old.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The kept versions of a song or a service, newest first.</summary>
    public List<DocumentVersion> ListVersions(string id)
    {
        var folder = Path.Combine(VersionsFolder, id);
        if (!Directory.Exists(folder))
            return [];
        var list = new List<DocumentVersion>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (DateTimeOffset.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                list.Add(new DocumentVersion(at, file));
        }

        return [.. list.OrderByDescending(v => v.At)];
    }

    public Service? LoadServiceVersion(DocumentVersion version) => Read(version.Path, RecomposeJsonContext.Default.Service)?.Normalized();

    public Song? LoadSongVersion(DocumentVersion version) => Read(version.Path, RecomposeJsonContext.Default.Song)?.Normalized();

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

    /// <summary>The journal of the last session (DM-80, REL-30), or null when there is none or it cannot be read.</summary>
    public Recovery.Journal? LoadJournal() => Read(Path.Combine(Root, "journal.json"), RecomposeJsonContext.Default.Journal);

    public void SaveJournal(Recovery.Journal journal) => Write(Path.Combine(Root, "journal.json"), journal, RecomposeJsonContext.Default.Journal);

    public void DeleteJournal()
    {
        var path = Path.Combine(Root, "journal.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>state.json (DM-80): the mode, the lock and whether the last run ended cleanly.</summary>
    public Recovery.AppState LoadState() => Read(Path.Combine(Root, "state.json"), RecomposeJsonContext.Default.AppState) ?? new Recovery.AppState();

    public void SaveState(Recovery.AppState state) => Write(Path.Combine(Root, "state.json"), state, RecomposeJsonContext.Default.AppState);

    public Rendering.RenderIndex LoadRenderIndex() =>
        Read(Path.Combine(RenderFolder, "index.json"), RecomposeJsonContext.Default.RenderIndex) ?? new Rendering.RenderIndex();

    public void SaveRenderIndex(Rendering.RenderIndex index) =>
        Write(Path.Combine(RenderFolder, "index.json"), index, RecomposeJsonContext.Default.RenderIndex);

    public string PeaksPath(string songId, string trackId) => Path.Combine(PeaksFolder, songId, trackId + ".peaks");

    private string SongPath(string id) => Path.Combine(SongsFolder, id + ".json");

    private string ServicePath(string id) => Path.Combine(ServicesFolder, id + ".json");

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        // Readers share delete access so an atomic replace (Write) never fails because of them, and on
        // Windows a read that lands in the instant of a replace is a sharing violation: try again shortly.
        for (var attempt = 1; ; attempt++)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize(stream, type);
            }
            catch (JsonException)
            {
                return null; // a damaged file reads as missing rather than crashing the app
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(10 * attempt);
            }
        }
    }

    private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";

        // On Windows a virus scanner or the indexer may hold a file it just saw for a moment, and then the
        // replace is refused. A save must not be lost for that (DM-90): try again shortly, then give up loudly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(stream, value, type);
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
            {
                Thread.Sleep(15 * attempt);
            }
        }
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

/// <summary>A kept version of a document (DM-90): when it was replaced, and its file.</summary>
public sealed record DocumentVersion(DateTimeOffset At, string Path);
