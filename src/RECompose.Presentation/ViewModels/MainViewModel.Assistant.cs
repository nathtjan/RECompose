// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Assistant;
using RECompose.Core.Commands;
using RECompose.Core.Services;
using RECompose.Core.Settings;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// The assistant (AS-1…60): what its tools see and how their changes are applied. A change goes through <see cref="Commit"/>
/// like a manual edit, so undo, autosave and the live plan (ARR-90) work unchanged.
/// </summary>
public sealed partial class MainViewModel
{
    private AssistantViewModel? _assistant;
    private RelayCommand? _toggleAssistant;

    /// <summary>The assistant panel (UI-17).</summary>
    public AssistantViewModel Assistant => _assistant ??= new AssistantViewModel(this);

    /// <summary>Ctrl+J and the top-bar button (AS-2).</summary>
    public RelayCommand ToggleAssistantCommand => _toggleAssistant ??= new RelayCommand(() => Assistant.IsOpen = !Assistant.IsOpen);

    internal AssistantSettings AssistantSettings => _settings.Assistant ?? new AssistantSettings();

    internal void SaveAssistantSettings(AssistantSettings settings)
    {
        _settings = _settings with { Assistant = settings };
        _store.SaveSettings(_settings);
    }

    internal string DataRoot => _store.Root;

    internal AssistantWorld AssistantWorld() => new()
    {
        Services = _services,
        Current = _service,
        FindSong = SongOf,
        Library = _allSongs.Select(s => s.Summary).ToList(),
        Midi = MidiOut,
        Check = () => RunServiceCheck() ?? throw new InvalidOperationException("No service"),
    };

    /// <summary>The AS-12 snapshot and mode line sent with every message.</summary>
    internal string AssistantState()
    {
        var service = _service;
        AssistantTransport? transport = null;
        if (_song is not null)
        {
            transport = new AssistantTransport(
                _state.ToString().ToLowerInvariant(),
                LoadedEntry is { } e && service?.IndexOf(e.Id) is int i and >= 0 ? i + 1 : null,
                string.Join(" · ", new[] { NowLabel, NowPassText, NowDetail }.Where(s => !string.IsNullOrEmpty(s))),
                SongBarValue, ElapsedText, RemainingText,
                string.IsNullOrEmpty(NextLabel) ? null : $"{NextLabel} {NextTag}".Trim(),
                CanCancel ? NextLabel : null, IsHolding);
        }

        var mode = IsPerform ? "perform" : "prepare";
        var lockMode = IsPerform ? (IsServiceLock ? "service" : "rehearsal") : null;
        var pads = new System.Text.Json.Nodes.JsonObject { ["set"] = Pads.CurrentSet?.Name, ["playing"] = Pads.IsPlaying, ["followSong"] = Pads.Follow };
        var json = Core.Assistant.AssistantState.Build(mode, lockMode, DateTime.Now, service, _services, SongOf,
            service is null ? null : ServiceDurations.Summary(Planned(service)), transport,
            _song?.Model ?? (SelectedEntry is { } selected ? SongOf(selected.SongId) : null), MidiOut, pads);
        return AssistantPrompt.State(mode, lockMode, json);
    }

    /// <summary>Applies a tool's change as one undo step, with a version of every document it replaces (DM-90).</summary>
    internal void ApplyAssistantChange(AssistantChange change)
    {
        if (change.Song is { } song)
            _store.KeepVersionNow(song.Id, isSong: true);
        foreach (var service in change.Services)
            _store.KeepVersionNow(service.Id, isSong: false);

        if (change.NewService is { } created)
            CommitServiceList($"Assistant: {change.Description}", add: created, remove: null);
        else
            Commit($"Assistant: {change.Description}", change.Song, change.Services);
        AppLog.Write("Assistant", change.Description); // the LOG tab's Assistant chip (AUTO-50)
        Entry.Refresh();
        RefreshCues();
    }

    internal int UndoMark => _undo.Count;

    internal UndoableChange? GroupUndo(int mark, string description) => _undo.Group(mark, description);

    /// <summary>The panel's Undo (AS-40): only while the group is the latest step, since undoing it under later edits would lose them.</summary>
    internal bool UndoGroup(UndoableChange group)
    {
        if (!ReferenceEquals(_undo.Top, group))
            return false;
        Undo();
        return true;
    }
}
