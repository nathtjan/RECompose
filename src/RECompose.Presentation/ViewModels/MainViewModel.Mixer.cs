// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Library;
using RECompose.Core.Services;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

// Mix presets (PB-81, DM-13): the MIXER tab's select, Save as new, Rename and Delete. A service entry chooses its
// preset; a library song plays its default. Fader moves change the preset in use (SaveMix).
public sealed partial class MainViewModel
{
    private bool _refreshingPresets;
    private MixPresetChoice? _currentPreset;

    public ObservableCollection<MixPresetChoice> MixPresetChoices { get; } = [];

    public MixPresetChoice? CurrentMixPreset
    {
        get => _currentPreset;
        set
        {
            if (_refreshingPresets || value is null || !Set(ref _currentPreset, value) || _song is not { } song)
                return;
            if (song.Mix.Id == value.Id)
                return;
            Flush(); // the faders belong to the preset they were moved in
            if (song.Entry is { } entry)
            {
                SetEntryMixPreset(entry.Id, value.Id);
            }
            else
            {
                var before = song.Model.DefaultMixPresetId;
                void Apply(string? id)
                {
                    UpdateSong(song.Id, s => s with { DefaultMixPresetId = id, UpdatedAt = DateTimeOffset.UtcNow });
                    ApplyMixPreset();
                    RefreshMixPresets();
                }

                Apply(value.Id);
                _undo.Push(new Core.Commands.UndoableChange($"Mix preset {value.Name}", () => Apply(before), () => Apply(value.Id)));
            }

            ShowToast($"Mix preset {value.Name}", canUndo: true);
        }
    }

    public RelayCommand SaveMixAsNewCommand => _saveMixAsNew ??= new RelayCommand(SaveMixAsNewAsync, () => _song is not null);
    public RelayCommand RenameMixCommand => _renameMix ??= new RelayCommand(RenameMixAsync, () => _song is not null);
    public RelayCommand DeleteMixCommand => _deleteMix ??= new RelayCommand(DeleteMixAsync, () => _song?.Model.MixPresets.Count > 1);

    private RelayCommand? _saveMixAsNew, _renameMix, _deleteMix;

    private void RefreshMixPresets()
    {
        _refreshingPresets = true;
        try
        {
            MixPresetChoices.Clear();
            if (_song is { } song)
            {
                var presets = song.Model.MixPresets.Count > 0 ? song.Model.MixPresets : [song.Mix];
                foreach (var preset in presets)
                    MixPresetChoices.Add(new MixPresetChoice(preset.Id, preset.Name));
            }

            // Always announce it: clearing the list cleared the select, and an equal choice wouldn't raise a change.
            _currentPreset = MixPresetChoices.FirstOrDefault(c => c.Id == _song?.Mix.Id);
            OnPropertyChanged(nameof(CurrentMixPreset));
        }
        finally
        {
            _refreshingPresets = false;
        }

        DeleteMixCommand.Refresh();
    }

    /// <summary>"Save as new…": the strips as they are become a new preset, which the entry (or the song) then uses.</summary>
    private async Task SaveMixAsNewAsync()
    {
        if (_song is not { } song || await _dialogs.AskNameAsync("Save mix preset", "Name", "Rehearsal – band live", "Save") is not { } name)
            return;
        Flush();
        var model = song.Model;
        var current = song.Mix;
        var preset = current with
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = Core.Arrangements.ArrangementEdit.UniqueName(name, model.MixPresets.Select(p => p.Name)),
            Tracks = new Dictionary<string, TrackMix>(current.Tracks),
        };
        var presets = model.MixPresets.Count > 0 ? model.MixPresets : [current];
        var songAfter = model with
        {
            MixPresets = [.. presets, preset],
            DefaultMixPresetId = song.Entry is null ? preset.Id : model.DefaultMixPresetId ?? current.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var servicesAfter = song.Entry is { } entry && LoadedService is { } service
            ? [ServiceEdit.Update(service, entry.Id, e => e with { MixPresetId = preset.Id })]
            : new List<Service>();
        Commit($"Saved mix preset {preset.Name}", songAfter, servicesAfter);
        RefreshMixPresets();
        ShowToast($"Saved mix preset {preset.Name}", canUndo: true);
    }

    private async Task RenameMixAsync()
    {
        if (_song is not { } song)
            return;
        var current = song.Mix;
        if (await _dialogs.AskNameAsync("Rename mix preset", "Name", current.Name, "Rename") is not { } name || name.Trim() == current.Name)
            return;
        var unique = Core.Arrangements.ArrangementEdit.UniqueName(name, song.Model.MixPresets.Where(p => p.Id != current.Id).Select(p => p.Name));
        var renamed = current with { Name = unique };
        var presets = song.Model.MixPresets.Count > 0 ? song.Model.MixPresets.Select(p => p.Id == current.Id ? renamed : p).ToList() : [renamed];
        Commit($"Renamed mix preset {current.Name} to {unique}", song.Model with { MixPresets = presets, UpdatedAt = DateTimeOffset.UtcNow }, []);
        RefreshMixPresets();
    }

    /// <summary>Deletes the preset in use; entries that used it play the song's default (DM-13).</summary>
    private async Task DeleteMixAsync()
    {
        if (_song is not { } song || song.Model.MixPresets.Count <= 1)
            return;
        var current = song.Mix;
        var users = _services.SelectMany(s => s.Entries).Count(e => e.SongId == song.Id && e.MixPresetId == current.Id);
        var message = users > 1 ? $"{users} service entries use {current.Name}. They will use the song's default preset." : $"Delete the mix preset {current.Name}?";
        if (!await _dialogs.ConfirmAsync("Delete mix preset", message, "Delete"))
            return;
        var presets = song.Model.MixPresets.Where(p => p.Id != current.Id).ToList();
        var defaultId = song.Model.DefaultMixPresetId == current.Id ? presets[0].Id : song.Model.DefaultMixPresetId;
        var servicesAfter = _services
            .Where(s => s.Entries.Any(e => e.SongId == song.Id && e.MixPresetId == current.Id))
            .Select(s => s with { Entries = s.Entries.Select(e => e.SongId == song.Id && e.MixPresetId == current.Id ? e with { MixPresetId = defaultId } : e).ToList() })
            .ToList();
        _saveVersion++; // a pending autosave belongs to the deleted preset
        Commit($"Deleted mix preset {current.Name}", song.Model with { MixPresets = presets, DefaultMixPresetId = defaultId, UpdatedAt = DateTimeOffset.UtcNow }, servicesAfter);
        ApplyMixPreset();
        RefreshMixPresets();
        ShowToast($"Deleted mix preset {current.Name}", canUndo: true);
    }
}
