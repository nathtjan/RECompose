// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Commands;
using RECompose.Core.Import;
using RECompose.Core.Library;
using RECompose.Engine;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

// Changes to the loaded song's tracks: add, delete, swap the audio, move, offset. Edit track… is in MainViewModel.cs.
public sealed partial class MainViewModel
{
    /// <summary>A track can start up to this long before or after the song start.</summary>
    public const double MaxTrackOffsetSec = 60;

    /// <summary>Saves the song with its new track list and reloads it, so the engine plays the stems as set. Undoable.</summary>
    private async Task SaveTracksAsync(Song edited, string log, string toast)
    {
        var loaded = _song!;
        var before = loaded.Model;
        var model = edited with { DurationSec = TrackLayout.DurationSec(edited.Tracks), UpdatedAt = DateTimeOffset.UtcNow };
        PersistSong(model);
        AppLog.Write("Tracks", $"{model.Title}: {log}");
        _undo.Push(new UndoableChange(toast, () => RestoreTracks(before, "undid"), () => RestoreTracks(model, "redid")));
        ShowToast(toast, canUndo: true);
        await ReloadKeepingPositionAsync(loaded);
    }

    /// <summary>Loads the song again for its changed tracks, with the playhead where it was.</summary>
    private Task ReloadKeepingPositionAsync(SongViewModel loaded) =>
        LoadSongAsync(loaded.Id, force: true, startFrame: (long)Math.Round(PlanPositionSec * loaded.SampleRate), entry: loaded.Entry, serviceId: _loadedServiceId);

    /// <summary>
    /// Undo and redo of a track change: puts back the track list (and the analyses tied to tracks) on the song as it is now,
    /// so edits made since, such as sections, stay. The song reloads if it is the loaded one.
    /// </summary>
    private void RestoreTracks(Song snapshot, string how)
    {
        if (_store.LoadSong(snapshot.Id) is not { } current)
            return;

        AppLog.Write("Tracks", $"{current.Title}: {how} a track change, {snapshot.Tracks.Count} tracks");

        foreach (var track in snapshot.Tracks)
        {
            // A swapped file leaves a waveform that is not its own.
            if (current.Tracks.FirstOrDefault(t => t.Id == track.Id) is { } now && now.File != track.File)
                ClearPeaks(current.Id, track.Id);
        }

        PersistSong(current with
        {
            Tracks = snapshot.Tracks,
            GuideAnalysis = snapshot.GuideAnalysis,
            ClickAnalysis = snapshot.ClickAnalysis,
            DurationSec = TrackLayout.DurationSec(snapshot.Tracks),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        if (_song is { } loaded && loaded.Id == snapshot.Id)
            _ = ReloadKeepingPositionAsync(loaded);
    }

    /// <summary>Removes a track's cached waveform so it is drawn again from the file; false when it can't be removed.</summary>
    private bool ClearPeaks(string songId, string trackId)
    {
        try
        {
            File.Delete(_store.PeaksPath(songId, trackId));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"A waveform couldn't be cleared: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes the song and its library row (the row's meta line counts the tracks).</summary>
    private void PersistSong(Song model)
    {
        _store.SaveSong(model);
        var library = _store.LoadLibrary();
        library.Songs.RemoveAll(s => s.Id == model.Id);
        library.Songs.Add(SongSummary.From(model));
        _store.SaveLibrary(library);
        if (_allSongs.FirstOrDefault(s => s.Id == model.Id) is { } item)
            item.Summary = SongSummary.From(model);
        RefreshLibrary();
    }

    /// <summary>The track to change, or null with a toast when the song is playing: these changes reload the song.</summary>
    private Track? StoppedTrack(string trackId, string doing)
    {
        if (_song is not { } loaded || loaded.Model.Tracks.FirstOrDefault(t => t.Id == trackId) is not { } track)
            return null;
        if (IsPlaying)
        {
            ShowToast($"Stop the song before {doing}.");
            return null;
        }

        return track;
    }

    /// <summary>Probes a picked file; null with a toast when it can't be read.</summary>
    private async Task<ImportCandidate?> ProbeAsync(string path)
    {
        try
        {
            var file = await _import.ProbeFileAsync(path);
            if (file.Info.Ok)
                return file;
            ShowToast($"Can't use {Path.GetFileName(path)}: {file.Info.Problem}");
        }
        catch (Exception ex) when (ex is IOException or EngineUnavailableException or InvalidOperationException)
        {
            ShowToast($"Can't read {Path.GetFileName(path)}: {ex.Message}");
        }

        return null;
    }

    /// <summary>Add track…: a new stem for the loaded song, with the role and group its name suggests.</summary>
    public async Task AddTrackAsync()
    {
        if (_song is not { } loaded)
            return;
        if (IsPlaying)
        {
            ShowToast("Stop the song before adding a track.");
            return;
        }

        if (await _dialogs.PickAudioFileAsync("Add track") is not { } path || await ProbeAsync(path) is not { } file)
            return;

        var initial = new TrackEdit(file.Name, ImportRowViewModel.RoleName(file.Detected.Role), file.Detected.Group, OutputChoices.ToLabel(file.Detected.DefaultOutput));
        if (await _dialogs.EditTrackAsync(initial, OutputChoices.For(_device?.OutputChannels.Count ?? 2), "Add track", "Add") is not { } edit)
            return;

        var role = ImportRowViewModel.RoleOf(edit.Role);
        var track = new Track
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(edit.Name) ? file.Name : edit.Name.Trim(),
            File = file.File,
            Role = role,
            Group = TrackGroups.For(role, edit.Group),
            Channels = file.Info.Channels,
            SampleRate = file.Info.SampleRate,
            Frames = file.Info.Frames,
            Output = OutputChoices.ToOutput(edit.Output),
        };
        await SaveTracksAsync(loaded.Model with { Tracks = TrackLayout.Add(loaded.Model.Tracks, track) }, $"added track {track.Name}", $"Added {track.Name}");
    }

    /// <summary>Delete track: the stem leaves the song; its audio file is never touched.</summary>
    public async Task DeleteTrackAsync(string trackId)
    {
        if (StoppedTrack(trackId, "deleting a track") is not { } track)
            return;
        var model = _song!.Model;
        if (model.Tracks.Count == 1)
        {
            ShowToast("A song needs at least one track.");
            return;
        }

        if (!await _dialogs.ConfirmAsync("Delete track", $"Delete {track.Name} from {model.Title}? The audio file stays where it is.", "Delete"))
            return;

        // Its mix settings and waveform stay in their files, so undo brings the track back as it was.
        var edited = model with
        {
            Tracks = TrackLayout.Remove(model.Tracks, trackId),
            GuideAnalysis = model.GuideAnalysis?.TrackId == trackId ? null : model.GuideAnalysis,
            ClickAnalysis = model.ClickAnalysis?.TrackId == trackId ? null : model.ClickAnalysis,
        };
        await SaveTracksAsync(edited, $"deleted track {track.Name}", $"Deleted {track.Name}");
    }

    /// <summary>Swap audio…: another file plays as this track, keeping its name, group, output and mix.</summary>
    public async Task SwapTrackAudioAsync(string trackId)
    {
        if (StoppedTrack(trackId, "swapping a track's audio") is not { } track)
            return;
        if (await _dialogs.PickAudioFileAsync($"Swap audio for {track.Name}") is not { } path || await ProbeAsync(path) is not { } file)
            return;

        var model = _song!.Model;
        if (!ClearPeaks(model.Id, trackId)) // the waveform is drawn again from the new file
            return;

        var swapped = track with { File = file.File, Channels = file.Info.Channels, SampleRate = file.Info.SampleRate, Frames = file.Info.Frames };
        await SaveTracksAsync(model with { Tracks = [.. model.Tracks.Select(t => t.Id == trackId ? swapped : t)] }, $"swapped the audio of {track.Name} for {Path.GetFileName(path)}", $"{track.Name} now plays {Path.GetFileName(path)}");
    }

    /// <summary>Move up or down a lane, left or right on the mixer (-1 or +1). The order is saved with the song; the song keeps playing.</summary>
    public void MoveTrack(string trackId, int delta)
    {
        if (_song?.Tracks.FirstOrDefault(t => t.Id == trackId) is { } shown)
            MoveTrackTo(trackId, _song.Tracks.IndexOf(shown) + delta);
    }

    /// <summary>Drag and drop: the track goes to a 0-based place among the lanes or strips.</summary>
    public void MoveTrackTo(string trackId, int index)
    {
        if (_song is not { } song || song.Tracks.FirstOrDefault(t => t.Id == trackId) is not { } shown)
            return;

        var moved = TrackLayout.MoveTo(song.Model.Tracks, trackId, index);
        var to = moved.FindIndex(t => t.Id == trackId);
        var from = song.Tracks.IndexOf(shown);
        if (to == from)
            return;

        var oldOrder = song.Model.Tracks.Select(t => t.Id).ToList();
        var newOrder = moved.Select(t => t.Id).ToList();
        ApplyOrder(song.Id, newOrder);
        AppLog.Write("Tracks", $"{song.Title}: moved {shown.Name} to place {to + 1} of {moved.Count}");
        _undo.Push(new UndoableChange($"Moved {shown.Name}",
            () => { AppLog.Write("Tracks", $"{song.Title}: undid moving {shown.Name}"); ApplyOrder(song.Id, oldOrder); },
            () => { AppLog.Write("Tracks", $"{song.Title}: redid moving {shown.Name}"); ApplyOrder(song.Id, newOrder); }));
    }

    /// <summary>Puts the song's tracks in this order, in the file and, when it is loaded, on screen without reloading.</summary>
    private void ApplyOrder(string songId, IReadOnlyList<string> ids)
    {
        if (_store.LoadSong(songId) is not { } current)
            return;

        var tracks = TrackLayout.InOrder(current.Tracks, ids);
        var edited = current with { Tracks = tracks, UpdatedAt = DateTimeOffset.UtcNow };
        PersistSong(edited);
        if (_song is not { } song || song.Id != songId)
            return;

        song.Model = edited;
        for (var i = 0; i < tracks.Count; i++)
        {
            var from = song.Tracks.ToList().FindIndex(t => t.Id == tracks[i].Id);
            if (from >= 0 && from != i)
                song.Tracks.Move(from, i);
        }
    }

    /// <summary>Set offset…: milliseconds later (positive) or earlier (negative) than the song start, to line a stem up.</summary>
    public async Task SetTrackOffsetAsync(string trackId)
    {
        if (StoppedTrack(trackId, "setting a track's offset") is not { } track)
            return;

        var current = Math.Round(track.OffsetSec * 1000, 1).ToString("0.#", CultureInfo.InvariantCulture);
        if (await _dialogs.AskNameAsync($"Offset for {track.Name}", "Milliseconds, + later, − earlier", current, "Set") is not { } text)
            return;

        if (!double.TryParse(text.Replace('−', '-').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || Math.Abs(ms) > MaxTrackOffsetSec * 1000)
        {
            ShowToast($"Enter the offset in milliseconds, up to {MaxTrackOffsetSec:0} seconds either way, such as 40 or −25.");
            return;
        }

        await SetTrackOffsetAsync(trackId, Math.Round(ms) / 1000);
    }

    /// <summary>Sets a track's offset in seconds (typed, or dragged on the timeline). Whole milliseconds, within the limit.</summary>
    public async Task SetTrackOffsetAsync(string trackId, double seconds)
    {
        if (StoppedTrack(trackId, "setting a track's offset") is not { } track)
            return;

        var sec = Math.Round(Math.Clamp(seconds, -MaxTrackOffsetSec, MaxTrackOffsetSec), 3);
        if (sec == track.OffsetSec)
            return;

        var model = _song!.Model;
        await SaveTracksAsync(model with { Tracks = [.. model.Tracks.Select(t => t.Id == trackId ? t with { OffsetSec = sec } : t)] }, $"{track.Name} offset {sec * 1000:0} ms", sec == 0 ? $"{track.Name} starts with the song" : $"{track.Name} starts {Math.Abs(sec) * 1000:0} ms {(sec > 0 ? "late" : "early")}");
    }
}
