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

    /// <summary>Export service…: the service and its songs' details, no audio (REL-45).</summary>
    public async Task ExportServiceAsync()
    {
        if (_service is not { } service)
            return;
        var name = string.Concat(service.Name.Split(Path.GetInvalidFileNameChars())) + ServiceExchange.Extension;
        if (await _dialogs.PickSaveFileAsync("Export service", name, ServiceExchange.Extension) is not { } path)
            return;

        try
        {
            var songs = service.Entries.Select(e => e.SongId).Distinct().Select(_store.LoadSong).OfType<Song>().ToList();
            await using (var file = File.Create(path))
                ServiceExchange.Export(service, songs, file);
            ShowToast($"Exported {service.Name} · {songs.Count} song{(songs.Count == 1 ? string.Empty : "s")}, no audio.");
            AppLog.Write("Services", $"Exported {service.Name} to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner = $"The service could not be exported ({ex.Message}).";
        }
    }

    /// <summary>Import service…: adds the service, matches its songs to the library, and lists the stems that are missing.</summary>
    public async Task ImportServiceAsync()
    {
        if (await _dialogs.PickOpenFileAsync("Import service", ServiceExchange.Extension) is not { } path)
            return;

        ServiceImport result;
        try
        {
            await using var file = File.OpenRead(path);
            var library = _allSongs.Select(s => _store.LoadSong(s.Id)).OfType<Song>().ToList();
            result = ServiceExchange.Import(file, library, File.Exists, () => Guid.NewGuid().ToString("N"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Banner = $"The service could not be imported ({ex.Message}).";
            return;
        }

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
