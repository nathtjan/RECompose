// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Import;
using RECompose.Core.Library;
using RECompose.Core.Services;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>Data safety (DM-90, REL-45): earlier versions of a service, and a service exported to a file and imported again.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Restore earlier version…: the kept versions, newest first; the one chosen replaces the service (one undo step).</summary>
    public async Task RestoreServiceVersionAsync()
    {
        if (_service is not { } service)
            return;
        var versions = _store.ListVersions(service.Id)
            .Select(v => (Version: v, Service: _store.LoadServiceVersion(v)))
            .Where(v => v.Service is not null)
            .ToList();
        if (versions.Count == 0)
        {
            ShowToast("No earlier versions of this service yet. One is kept every five minutes while you edit.");
            return;
        }

        var labels = versions.Select(v =>
        {
            var count = v.Service!.Entries.Count;
            return $"{v.Version.At.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture)} · {v.Service.Name} · {count} song{(count == 1 ? string.Empty : "s")}";
        }).ToList();
        if (await _dialogs.ChooseAsync("Restore earlier version", $"Choose the version of {service.Name} to restore. The current one is kept as a version too.", labels, "Restore") is not { } index)
            return;

        var restored = versions[index].Service! with { Id = service.Id };
        CommitService($"Restore {service.Name} from {labels[index]}", restored);
        ShowToast($"Restored {service.Name} from {versions[index].Version.At.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture)}.");
    }

    /// <summary>Song › Restore earlier version…: the same for the loaded song, e.g. as it was before an assistant turn (DM-90).</summary>
    public async Task RestoreSongVersionAsync()
    {
        if (_song is not { } song)
            return;
        var versions = _store.ListVersions(song.Id)
            .Select(v => (Version: v, Song: _store.LoadSongVersion(v)))
            .Where(v => v.Song is not null)
            .ToList();
        if (versions.Count == 0)
        {
            ShowToast("No earlier versions of this song yet. One is kept every five minutes while you edit.");
            return;
        }

        var labels = versions.Select(v =>
        {
            var count = v.Song!.Sections.Count;
            return $"{v.Version.At.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture)} · {v.Song.Title} · {count} section{(count == 1 ? string.Empty : "s")}";
        }).ToList();
        if (await _dialogs.ChooseAsync("Restore earlier version", $"Choose the version of {song.Title} to restore. The current one is kept as a version too.", labels, "Restore") is not { } index)
            return;

        var restored = versions[index].Song! with { Id = song.Id };
        Commit($"Restore {song.Title} from {labels[index]}", restored, []);
        ShowToast($"Restored {song.Title} from {versions[index].Version.At.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture)}.");
    }

    /// <summary>
    /// Export service… (REL-45): the service and its songs' details, and on request every stem too, so another computer gets
    /// the exact same service. One file type either way.
    /// </summary>
    public async Task ExportServiceAsync()
    {
        if (_service is not { } service)
            return;
        var songs = service.Entries.Select(e => e.SongId).Distinct().Select(_store.LoadSong).OfType<Song>().ToList();
        var audioBytes = songs.SelectMany(s => s.Tracks).Select(t => t.File).Distinct().Where(File.Exists).Sum(f => new FileInfo(f).Length);
        if (await _dialogs.ChooseAsync("Export service",
                "With the audio, another computer gets the exact same service. Without it, that computer needs the songs' stems already.",
                ["Service and songs only", $"With audio ({SizeText(audioBytes)})"], "Export") is not { } choice)
            return;
        var withAudio = choice == 1;

        var name = string.Concat(service.Name.Split(Path.GetInvalidFileNameChars())) + (withAudio ? " (with audio)" : string.Empty) + ServiceExchange.Extension;
        if (await _dialogs.PickSaveFileAsync("Export service", name, ServiceExchange.Extension) is not { } path)
            return;

        try
        {
            if (withAudio)
                ShowToast($"Exporting {service.Name} with audio…");
            var skipped = await Task.Run(() =>
            {
                using var file = File.Create(path);
                return ServiceExchange.Export(service, songs, file, withAudio ? f => File.Exists(f) ? File.OpenRead(f) : null : null);
            });
            var what = $"{songs.Count} song{(songs.Count == 1 ? string.Empty : "s")}, {(withAudio ? "with audio" : "no audio")}";
            ShowToast(skipped.Count == 0 ? $"Exported {service.Name} · {what}." : $"Exported {service.Name} · {what}. {skipped.Count} stem{(skipped.Count == 1 ? " was" : "s were")} not found and left out.");
            foreach (var line in skipped.Take(5))
                AddToastLine(line);
            AppLog.Write("Services", $"Exported {service.Name} to {path} ({what}, {skipped.Count} stems left out)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner = $"The service could not be exported ({ex.Message}).";
        }
    }

    /// <summary>"640 MB", "2.3 GB".</summary>
    internal static string SizeText(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : Math.Max(1, (bytes + (1 << 19)) >> 20).ToString(CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Import service…: adds the service as a new one, matches its songs to the library by their audio, asks for each one
    /// whose copy differs which to use, copies the file's stems where this computer lacks them, and lists any still missing.
    /// </summary>
    public async Task ImportServiceAsync()
    {
        if (await _dialogs.PickOpenFileAsync("Import service", ServiceExchange.Extension, ServiceExchange.OldExtension) is not { } path)
            return;

        ServiceImport result;
        try
        {
            var library = _allSongs.Select(s => _store.LoadSong(s.Id)).OfType<Song>().ToList();
            var conflicts = await Task.Run(() =>
            {
                using var file = File.OpenRead(path);
                return ServiceExchange.Conflicts(file, library);
            });

            var choices = new Dictionary<string, SongChoice>();
            foreach (var conflict in conflicts)
            {
                var title = conflict.Library.Title;
                if (await _dialogs.ChooseAsync("Song differs",
                        $"{title} is in your library with the same audio, but the file's copy has other {string.Join(", ", conflict.Differences)}. Which should the service use?",
                        ["Keep this computer's", "Use the file's (replaces it; the current one stays in its earlier versions)", $"Keep both (adds \"{conflict.Incoming.Title} (imported)\")"],
                        "Continue") is not { } choice)
                    return;
                choices[conflict.Incoming.Id] = (SongChoice)choice;
            }

            result = await Task.Run(() =>
            {
                using var file = File.OpenRead(path);
                return ServiceExchange.Import(file, library, File.Exists, () => Guid.NewGuid().ToString("N"), _store.MediaFolder, choices);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Banner = $"The service could not be imported ({ex.Message}).";
            return;
        }

        // Replaced songs, and songs given the file's copies of stems they were missing; undoable like any edit.
        foreach (var song in result.UpdatedSongs)
            Commit($"Import {song.Title}", song, []);

        result = result with { Service = result.Service with { Name = ServiceExchange.UniqueName(result.Service.Name, _services.Select(s => s.Name)) } };

        if (result.NewSongs.Count > 0)
        {
            var index = _store.LoadLibrary();
            foreach (var song in result.NewSongs)
            {
                _store.SaveSong(song);
                index.Songs.RemoveAll(s => s.Id == song.Id);
                index.Songs.Add(SongSummary.From(song));
                if (_allSongs.All(s => s.Id != song.Id))
                    _allSongs.Add(new SongItemViewModel(SongSummary.From(song)));
            }

            _store.SaveLibrary(index);
            _allSongs.Sort((a, b) => NaturalComparer.Instance.Compare(a.Title, b.Title));
            RefreshLibrary();
        }

        CommitServiceList($"Import {result.Service.Name}", result.Service, null);
        AppLog.Write("Services", $"Imported {result.Service.Name} from {path}: {result.NewSongs.Count} new songs, {result.Missing.Count} stems missing");
        if (result.Missing.Count == 0)
        {
            ShowToast($"Imported {result.Service.Name} · {result.Service.Entries.Count} songs.");
            return;
        }

        ShowToast($"Imported {result.Service.Name}. {result.Missing.Count} stem{(result.Missing.Count == 1 ? " is" : "s are")} missing; the service check lists them with Relink.");
        foreach (var line in result.Missing.Take(5))
            AddToastLine(line);
    }
}
