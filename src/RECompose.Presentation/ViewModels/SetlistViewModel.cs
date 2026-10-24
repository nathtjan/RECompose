// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Arrangements;
using RECompose.Core.Services;
using RECompose.Core.Timing;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>A service in the picker (UI-11): "Sunday 26 Oct · 9:00".</summary>
public sealed record ServiceChoice(string Id, string Title)
{
    public override string ToString() => Title;
}

public sealed record CountInChoice(CountInMode Mode, int Bars, string Name)
{
    public static readonly IReadOnlyList<CountInChoice> All =
    [
        new(CountInMode.Track, 1, "From the track"),
        new(CountInMode.Generated, 1, "Click, 1 bar"),
        new(CountInMode.Generated, 2, "Click, 2 bars"),
        new(CountInMode.None, 1, "None"),
    ];

    public static CountInChoice For(CountIn countIn) =>
        All.FirstOrDefault(c => c.Mode == countIn.Mode && (c.Mode != CountInMode.Generated || c.Bars == countIn.Bars)) ?? All[0];

    public override string ToString() => Name;
}

public sealed record MixPresetChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One row of the service (SL-3, UI-12): index, title, key, variant, status, and the transition chip after it.</summary>
public sealed class SetlistRowViewModel(ServiceEntry entry, int index) : ObservableObject
{
    private bool _isSelected;
    private bool _isLoaded;
    private bool _isPlaying;

    public ServiceEntry Entry { get; } = entry;
    public string Id => Entry.Id;

    /// <summary>1-based.</summary>
    public int Index { get; } = index;

    public required string Title { get; init; }
    public required string KeyText { get; init; }

    /// <summary>"Short", "Custom for this service", "As written".</summary>
    public required string VariantText { get; init; }

    /// <summary>"38 bars · 1:45" at the entry tempo (ARR-80).</summary>
    public required string LengthText { get; init; }

    public required string ChipText { get; init; }

    /// <summary>Every row but the last has a transition into the next song.</summary>
    public required bool HasChip { get; init; }

    /// <summary>Why the entry can't play as asked ("Section removed — choose a replacement"), or null.</summary>
    public required string? Problem { get; init; }

    public bool HasProblem => Problem is not null;

    public string? Notes => string.IsNullOrWhiteSpace(Entry.Notes) ? null : Entry.Notes;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsLoaded { get => _isLoaded; set => Set(ref _isLoaded, value); }
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    /// <summary>The row's accessible name: "2, Come Ye Sinners, F, Custom for this service, playing".</summary>
    public string AccessibleName => string.Join(", ", new[] { Index.ToString(CultureInfo.InvariantCulture), Title, KeyText, VariantText, IsPlaying ? "playing" : IsLoaded ? "loaded" : null, Problem }.OfType<string>());
}

/// <summary>
/// The service block of the sidebar (UI-12) and the service picker (UI-11), with the settings of the selected entry that
/// the ENTRY tab shows (SL-7): arrangement, key, tempo, count-in, mix preset, notes and transition.
/// </summary>
public sealed class SetlistViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading;
    private ServiceChoice? _current;
    private SetlistRowViewModel? _selected;
    private string _notes = string.Empty;

    internal SetlistViewModel(MainViewModel main)
    {
        _main = main;
        NewServiceCommand = new RelayCommand(_main.NewServiceAsync);
        DuplicateServiceCommand = new RelayCommand(_main.DuplicateServiceAsync, () => HasService);
        RenameServiceCommand = new RelayCommand(_main.RenameServiceAsync, () => HasService);
        DeleteServiceCommand = new RelayCommand(_main.DeleteServiceAsync, () => HasService);
        LoadSelectedCommand = new RelayCommand(() => _selected is { } row ? _main.LoadEntryAsync(row.Id) : Task.CompletedTask, () => HasSelection);
        RemoveSelectedCommand = new RelayCommand(() => { if (_selected is { } row) _main.RemoveEntry(row.Id); }, () => HasSelection);
        MoveUpCommand = new RelayCommand(() => { if (_selected is { } row) _main.MoveServiceEntry(row.Id, row.Index - 2); }, () => _selected is { Index: > 1 });
        MoveDownCommand = new RelayCommand(() => { if (_selected is { } row) _main.MoveServiceEntry(row.Id, row.Index); }, () => _selected is { } r && r.Index < Rows.Count);
        EditTransitionCommand = new RelayCommand(o => o is string id ? _main.EditTransitionAsync(id) : _selected is { } row ? _main.EditTransitionAsync(row.Id) : Task.CompletedTask);
        MakeCustomCommand = new RelayCommand(() => { if (_selected is { } row) _main.MakeEntryCustom(row.Id); }, () => _selected?.Entry.Arrangement.Type == ArrangementChoiceType.Variant);
        RevertCommand = new RelayCommand(() => { if (_selected is { } row) _main.RevertEntry(row.Id); }, () => RevertText is not null);
        SaveAsVariantCommand = new RelayCommand(() => _selected is { } row ? _main.SaveEntryAsVariantAsync(row.Id) : Task.CompletedTask, () => IsCustom);
        KeyDownCommand = new RelayCommand(() => _main.StepKey(-1, _selected?.Id), () => HasSelection);
        KeyUpCommand = new RelayCommand(() => _main.StepKey(1, _selected?.Id), () => HasSelection);
        TempoDownCommand = new RelayCommand(() => _main.StepTempo(-1, _selected?.Id), () => HasSelection);
        TempoUpCommand = new RelayCommand(() => _main.StepTempo(1, _selected?.Id), () => HasSelection);
    }

    // ---- The service ----------------------------------------------------------------------------------------

    public ObservableCollection<ServiceChoice> Services { get; } = [];

    public ServiceChoice? CurrentChoice
    {
        get => _current;
        set
        {
            if (_loading || value is null || !Set(ref _current, value))
                return;
            _main.SelectService(value.Id);
        }
    }

    public bool HasService => _main.CurrentService is not null;
    public bool HasNoService => !HasService;

    /// <summary>"Sunday 26 Oct · 9:00".</summary>
    public string Title => _main.CurrentService is { } s ? ServiceText.Title(s) : string.Empty;

    /// <summary>"4 songs · 24:30+" (ARR-80).</summary>
    public string Summary { get; private set; } = string.Empty;

    public ObservableCollection<SetlistRowViewModel> Rows { get; } = [];

    /// <summary>UI-80: a service with nothing in it yet.</summary>
    public bool IsEmpty => HasService && Rows.Count == 0;

    public RelayCommand NewServiceCommand { get; }
    public RelayCommand DuplicateServiceCommand { get; }
    public RelayCommand RenameServiceCommand { get; }
    public RelayCommand DeleteServiceCommand { get; }
    public RelayCommand LoadSelectedCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand EditTransitionCommand { get; }
    public RelayCommand MakeCustomCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand SaveAsVariantCommand { get; }
    public RelayCommand KeyDownCommand { get; }
    public RelayCommand KeyUpCommand { get; }
    public RelayCommand TempoDownCommand { get; }
    public RelayCommand TempoUpCommand { get; }

    public SetlistRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (_loading || ReferenceEquals(value, _selected))
                return;
            _main.SelectEntry(value?.Id);
        }
    }

    // ---- The selected entry (SL-7, UI-16) ---------------------------------------------------------------------

    public bool HasSelection => _selected is not null;

    /// <summary>"Editing: Champion · service entry 1" (UI-16).</summary>
    public string EditingText => _selected is { } row ? $"Editing: {row.Title} · service entry {row.Index.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

    /// <summary>The selected entry isn't the one loaded: the header shows "not loaded" and a Load button (UI-16).</summary>
    public bool IsSelectionNotLoaded => _selected is { IsLoaded: false };

    public bool IsSelectionLoaded => _selected is { IsLoaded: true };

    /// <summary>"A (−1)".</summary>
    public string KeyText { get; private set; } = string.Empty;

    /// <summary>PB-62: beyond ±5 semitones.</summary>
    public bool KeyWarning => _selected?.Entry.KeyOffset is > 5 or < -5;

    public string KeyTip => KeyWarning ? "Large shifts can sound unnatural" : "Ctrl+↑ and Ctrl+↓";

    /// <summary>"95%".</summary>
    public string TempoText => _selected is { } row ? $"{row.Entry.TempoPercent.ToString(CultureInfo.InvariantCulture)}%" : string.Empty;

    public IReadOnlyList<CountInChoice> CountInChoices => CountInChoice.All;

    public CountInChoice? CountIn
    {
        get => _selected is { } row ? CountInChoice.For(row.Entry.CountIn) : null;
        set
        {
            if (!_loading && value is not null && _selected is { } row)
                _main.SetCountIn(row.Id, new CountIn { Mode = value.Mode, Bars = value.Bars });
        }
    }

    /// <summary>SL-36 Generated sounds once the click generator is in the engine (milestone 4, engine part).</summary>
    public bool CountInNotAudible => _selected?.Entry.CountIn.Mode == CountInMode.Generated;

    public ObservableCollection<MixPresetChoice> MixPresets { get; } = [];

    public MixPresetChoice? MixPreset
    {
        get => _mixPreset;
        set
        {
            if (_loading || value is null || !Set(ref _mixPreset, value) || _selected is not { } row)
                return;
            _main.SetEntryMixPreset(row.Id, value.Id);
        }
    }

    private MixPresetChoice? _mixPreset;

    /// <summary>Shown in the setlist rail and the Perform view (DM-41).</summary>
    public string Notes
    {
        get => _notes;
        set
        {
            if (Set(ref _notes, value) && !_loading && _selected is { } row)
                _main.SetNotes(row.Id, value);
        }
    }

    /// <summary>"Short", "Custom for this service", "As written".</summary>
    public string ArrangementText => _selected?.VariantText ?? string.Empty;

    public bool IsCustom => _selected?.Entry.Arrangement.Type == ArrangementChoiceType.Custom;

    /// <summary>"Revert to "Short"" when the custom arrangement came from a variant that still exists.</summary>
    public string? RevertText { get; private set; }

    public bool CanRevert => RevertText is not null;

    /// <summary>The chip of the transition after the selected entry, or empty for the last one.</summary>
    public string TransitionText => _selected is { HasChip: true } row ? row.ChipText : string.Empty;

    public bool HasTransition => _selected is { HasChip: true };

    // ---- Refresh -------------------------------------------------------------------------------------------------

    /// <summary>Rebuilds the picker, the rows and the selected entry's settings from the services.</summary>
    internal void Refresh()
    {
        _loading = true;
        try
        {
            var current = _main.CurrentService;
            Services.Clear();
            foreach (var s in ServiceText.Ordered(_main.AllServices, DateTime.Now))
                Services.Add(new ServiceChoice(s.Id, ServiceText.Title(s)));
            _current = Services.FirstOrDefault(c => c.Id == current?.Id);

            Rows.Clear();
            Summary = string.Empty;
            if (current is not null)
            {
                var planned = _main.Planned(current);
                for (var i = 0; i < planned.Count; i++)
                {
                    var p = planned[i];
                    var next = i + 1 < planned.Count ? planned[i + 1] : null;
                    Rows.Add(Row(p, i, next));
                }

                Summary = ServiceDurations.Summary(planned);
            }

            var selectedId = _main.SelectedEntry?.Id;
            _selected = Rows.FirstOrDefault(r => r.Id == selectedId);
            UpdatePlaying();
            LoadSelection();
        }
        finally
        {
            _loading = false;
        }

        foreach (var name in new[]
                 {
                     nameof(CurrentChoice), nameof(HasService), nameof(HasNoService), nameof(Title), nameof(Summary), nameof(IsEmpty), nameof(Selected),
                 })
            OnPropertyChanged(name);
        foreach (var c in new[]
                 {
                     DuplicateServiceCommand, RenameServiceCommand, DeleteServiceCommand, LoadSelectedCommand, RemoveSelectedCommand, MoveUpCommand,
                     MoveDownCommand, MakeCustomCommand, RevertCommand, SaveAsVariantCommand, KeyDownCommand, KeyUpCommand, TempoDownCommand, TempoUpCommand,
                 })
            c.Refresh();
    }

    /// <summary>The loaded and playing marks, without rebuilding the rows (the transport changed).</summary>
    internal void UpdatePlaying()
    {
        var loaded = _main.LoadedService?.Id == _main.CurrentService?.Id ? _main.LoadedEntry?.Id : null;
        foreach (var row in Rows)
        {
            row.IsSelected = ReferenceEquals(row, _selected);
            row.IsLoaded = row.Id == loaded;
            row.IsPlaying = row.IsLoaded && _main.IsPlaying;
        }

        OnPropertyChanged(nameof(IsSelectionNotLoaded));
        OnPropertyChanged(nameof(IsSelectionLoaded));
    }

    private SetlistRowViewModel Row(PlannedEntry p, int index, PlannedEntry? next)
    {
        var song = p.Song;
        var resolved = EntryArrangement.Resolve(p.Entry, song);
        string? problem = resolved.Problem;
        if (problem is null && song is not null && resolved.Arrangement is { } arrangement)
        {
            var sections = Core.Sections.SectionList.Describe(song.Sections, song.Grid, song.DurationSec);
            problem = ArrangementValidator.Validate(arrangement, sections).FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
        }

        var nextKey = next?.Song is { } ns ? ServiceText.EntryKey(ns, next.Entry.KeyOffset) : null;
        return new SetlistRowViewModel(p.Entry, index + 1)
        {
            Title = song?.Title ?? "Missing song",
            KeyText = song is null ? "—" : ServiceText.EntryKey(song, p.Entry.KeyOffset)?.ToDisplayString() ?? "Key ?",
            VariantText = resolved.Name,
            LengthText = p.Plan is { IsEmpty: false } plan ? ArrangementSummary.Totals(plan) : string.Empty,
            ChipText = ServiceText.Chip(p.Entry.TransitionAfter, nextKey, next?.Song is { } nb ? nb.Bpm * next.Entry.TempoFactor : null),
            HasChip = next is not null,
            Problem = problem,
        };
    }

    private void LoadSelection()
    {
        var row = _selected;
        var song = row is null ? null : _main.SongOf(row.Entry.SongId);
        KeyText = row is not null && song is not null ? ServiceText.KeyText(song, row.Entry.KeyOffset) : string.Empty;
        Set(ref _notes, row?.Entry.Notes ?? string.Empty, nameof(Notes));

        MixPresets.Clear();
        if (song is not null)
        {
            foreach (var preset in song.MixPresets.Count > 0 ? song.MixPresets : [song.DefaultMix])
                MixPresets.Add(new MixPresetChoice(preset.Id, preset.Name));
        }

        var presetId = row is null || song is null ? null : (song.MixPresets.FirstOrDefault(p => p.Id == row.Entry.MixPresetId) ?? song.DefaultMix).Id;
        _mixPreset = MixPresets.FirstOrDefault(m => m.Id == presetId); // announced below: the refilled list cleared the select

        RevertText = row is not null && song is not null && ServiceEdit.RevertTarget(row.Entry, song) is { } target ? $"Revert to “{target.Name}”" : null;

        foreach (var name in new[]
                 {
                     nameof(HasSelection), nameof(EditingText), nameof(IsSelectionNotLoaded), nameof(IsSelectionLoaded), nameof(KeyText), nameof(KeyWarning),
                     nameof(KeyTip), nameof(TempoText), nameof(CountIn), nameof(CountInNotAudible), nameof(MixPreset), nameof(ArrangementText), nameof(IsCustom), nameof(RevertText),
                     nameof(CanRevert), nameof(TransitionText), nameof(HasTransition),
                 })
            OnPropertyChanged(name);
    }
}
