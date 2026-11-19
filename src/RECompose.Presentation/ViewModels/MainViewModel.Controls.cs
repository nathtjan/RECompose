// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Controls;
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Settings;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>Keys, foot pedals and controllers act through the same live actions as the on-screen keys (LIVE-50…55).</summary>
public sealed partial class MainViewModel
{
    private ControlsViewModel? _controls;
    private RelayCommand? _openControls;

    internal AppSettings CurrentSettings => _settings;

    internal IDialogs Dialogs => _dialogs;

    internal void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        _settings = change(_settings);
        _store.SaveSettings(_settings);
    }

    /// <summary>Settings › Controls and › Playback.</summary>
    public ControlsViewModel Controls => _controls ??= new ControlsViewModel(this);

    public RelayCommand OpenControlsCommand => _openControls ??= new RelayCommand(() => OpenSettingsAsync(SettingsTab.Controls));

    internal void ReopenMidiOut()
    {
        if (_engine is { } engine)
            _ = OpenMidiOutAsync(engine);
    }

    internal void ReopenControlOutput()
    {
        _controlOutputOpen = false;
        Controls.ControlOutputStatus = string.Empty;
        if (_engine is { } engine)
            _ = OpenControlOutputAsync(engine);
    }

    private bool _controlOutputOpen;

    internal bool HasControlOutput => _controlOutputOpen && _engine is not null;

    /// <summary>The controller's own output for feedback, apart from the automation's MIDI out (protocol 18).</summary>
    private async Task OpenControlOutputAsync(EngineClient engine)
    {
        var port = _settings.ControlOutput;
        try
        {
            _controlOutputOpen = await engine.SetControlOutputAsync(port);
            // Windows lets one app at a time open a port: the usual reason it does not open.
            if (port is not null) // without a port Controls is not needed yet
            {
                _ui.Post(() =>
                {
                    Controls.ControlOutputStatus = _controlOutputOpen ? string.Empty : $"Can't open {port}. It is not connected, or another app is using it.";
                    Controls.Feedback(all: true);
                });
            }
            if (port is not null)
                AppLog.Write("Controls", _controlOutputOpen ? $"Controller feedback: {port}" : $"Controller feedback: can't open {port}");
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            _controlOutputOpen = false;
            AppLog.Error("Controls", ex);
        }
    }

    internal void SendControlFeedback(IReadOnlyList<byte[]> messages)
    {
        if (_engine is { } engine)
            _ = engine.SendControlMidiAsync(messages).ContinueWith(t => AppLog.Error("Controls", t.Exception!.InnerException!), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>The type of the section playing, for a lit section button.</summary>
    internal SectionType? CurrentSectionType =>
        _live?.Committed is { Items.Count: > 0 } plan && _song is not null ? plan.Items[plan.ItemIndexAt(_planPosition)].Section?.Type : null;

    internal bool IsTileCurrent(string? position) =>
        int.TryParse(position, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Tiles.Count && Tiles[n - 1].IsCurrent;

    /// <summary>The level of a fader target as a controller value (0–127), for a motorised fader; null when there is none.</summary>
    internal int? FaderValue(string? target)
    {
        if (target == TrackGroups.Pads)
            return (int)Math.Round((Pads.VolumeDb + 40) / 40 * 127);
        if (FaderTracks(target) is not { Count: > 0 } tracks)
            return null;
        return (int)Math.Round((tracks.Max(t => t.GainDb) - TrackViewModel.MinimumDb) / (TrackViewModel.MaximumDb - TrackViewModel.MinimumDb) * 127);
    }

    private List<TrackViewModel>? FaderTracks(string? target) => _song is not { } song
        ? null
        : target == "Master" ? [song.Master] : [.. song.Tracks.Where(t => t.Group == target)];

    private readonly SoftTakeover _takeover = new();

    /// <summary>In Service lock a MIDI fader stops here, so a slip can't silence a track (the screen's faders still can).</summary>
    private const double ServiceFloorDb = -40, PadServiceFloorDb = -30;

    /// <summary>
    /// A MIDI fader: the pads' volume, the master, or every track of a group, which moves together keeping their balance
    /// (the loudest follows the fader). One undo step per gesture; the preset in use takes the move as an on-screen fader's.
    /// </summary>
    private void MoveFader(ControlEvent e)
    {
        if (e.Binding is not { Target: { } target } binding)
            return;
        if (target == TrackGroups.Pads)
        {
            var pad = ControlProfiles.FaderDb(e.Value, -40, 0);
            if (binding.SoftTakeover && !_takeover.Accept(target, pad, Pads.VolumeDb))
                return;
            Pads.VolumeDb = IsServiceLock ? Math.Max(pad, PadServiceFloorDb) : pad;
            _takeover.Applied(target, Pads.VolumeDb);
            return;
        }

        if (FaderTracks(target) is not { Count: > 0 } tracks)
            return;
        var db = ControlProfiles.FaderDb(e.Value, TrackViewModel.MinimumDb, TrackViewModel.MaximumDb);
        var current = tracks.Max(t => t.GainDb);
        if (binding.SoftTakeover && !_takeover.Accept(target, db, current))
            return;
        if (IsServiceLock)
            db = Math.Max(db, ServiceFloorDb);
        var delta = db - current;
        if (Math.Abs(delta) < 0.05)
            return;

        var before = tracks.Select(t => _committed.TryGetValue(t.Id, out var c) ? c : (t.GainDb, t.Mute, t.Solo)).ToList();
        foreach (var track in tracks)
            track.Apply(gainDb: Math.Round(track.GainDb + delta, 1));
        var after = tracks.Select(t => (t.GainDb, t.Mute, t.Solo)).ToList();
        void Restore(List<(double Gain, bool Mute, bool Solo)> values)
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                tracks[i].Apply(values[i].Gain, values[i].Mute, values[i].Solo);
                _committed[tracks[i].Id] = values[i];
                SendTrack(tracks[i], "gain");
            }

            ScheduleSave();
        }

        Restore(after);
        _takeover.Applied(target, tracks.Max(t => t.GainDb));
        _undo.Push(new Core.Commands.UndoableChange($"Fader {target} {tracks.Max(t => t.GainDb).ToString("+0.0;−0.0;0.0", System.Globalization.CultureInfo.InvariantCulture)} dB",
            () => Restore(before), () => Restore(after)) { MergeKey = $"fader:{target}" });
    }

    private void OnMidi(MidiMessage message) => _ui.Post(() => Controls.Midi(message));

    /// <summary>Starts listening to the controllers once the engine is there (LIVE-52).</summary>
    private async Task OpenMidiAsync(EngineClient engine)
    {
        engine.MidiReceived += OnMidi;
        try
        {
            var open = await engine.SetMidiInputsAsync(null);
            _midiInputs = open;
            if (open.Count > 0)
                AppLog.Write("Controls", $"MIDI in: {string.Join(", ", open)}");
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Controls", ex); // no controllers is a normal state
        }
    }

    /// <summary>What a control does (LIVE-50). Guarded actions are ignored in Service lock; the screen's hold keys are the way (LIVE-3).</summary>
    public void RunControl(ControlAction action, ActionSource source)
    {
        _ = RunControlAsync(action, source);
    }

    /// <summary>A control with what it applies to: a section, a tile, a pad, a fader; the rest as <see cref="RunControlAsync(ControlAction, ActionSource)"/>.</summary>
    public void RunControl(ControlEvent e, ActionSource source)
    {
        _ = RunControlAsync(e, source);
    }

    internal async Task RunControlAsync(ControlEvent e, ActionSource source)
    {
        var target = e.Binding?.Target;
        if (e.Action is ControlAction.JumpToSection or ControlAction.JumpToTile && e.Binding?.PerformOnly == true && !IsPerform)
            return;
        switch (e.Action)
        {
            case ControlAction.JumpToSection:
                await JumpToSectionTypeAsync(target, source);
                break;
            case ControlAction.JumpToTile:
                if (int.TryParse(target, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Tiles.Count
                    && Tiles[n - 1] is { IsCurrent: false, IsHold: false, IsTarget: false } tile)
                    await LiveAsync(LiveActionKind.JumpToItem, tile.Id, source: source);
                break;
            case ControlAction.PadKey:
                await PadKeyAsync(e);
                break;
            case ControlAction.PadPlayStop:
                if (Pads.HasSet)
                    await Pads.PlayStopAsync();
                break;
            case ControlAction.Fader:
                MoveFader(e);
                break;
            default:
                await RunControlAsync(e.Action, source);
                break;
        }
    }

    /// <summary>The next section of a type, as voice's "go to the chorus" does (LIVE-50).</summary>
    private async Task JumpToSectionTypeAsync(string? target, ActionSource source)
    {
        if (!Enum.TryParse<SectionType>(target, out var type) || EnsureLive() is not { } live || _song is null)
            return;
        AppLog.Write("Controls", $"{PerformReadouts.SourceTag(source)}: {SectionTypes.DisplayName(type)}");
        switch (ControlProfiles.FindSection(live.Committed, _planPosition, type))
        {
            case { ItemId: { } item }:
                await LiveAsync(LiveActionKind.JumpToItem, item, source: source);
                break;
            case { SectionId: { } section }:
                await LiveAsync(LiveActionKind.JumpToSection, sectionId: section, source: source);
                break;
            default:
                ShowToast($"There is no {SectionTypes.DisplayName(type)} in this song.");
                break;
        }
    }

    /// <summary>A pad key from a keyboard or a button (SL-42): a press starts or stops it; with Hold it sounds while held.</summary>
    private async Task PadKeyAsync(ControlEvent e)
    {
        if (e.Binding is not { } binding || ControlProfiles.PitchClass(binding, e.Number) is not { } pc || !Pads.HasSet)
            return;
        if (e.Value > 0)
        {
            if (!binding.Hold)
                await Pads.PressAsync(pc);
            else if (Pads.PlayingKey != pc)
                await Pads.PlayAsync(pc);
        }
        else if (binding.Hold && Pads.PlayingKey == pc)
        {
            await Pads.StopAsync();
        }
    }

    internal async Task RunControlAsync(ControlAction action, ActionSource source)
    {
        if (action is ControlAction.Talk or ControlAction.TalkEnd)
        {
            Talk(action == ControlAction.Talk);
            return;
        }

        AppLog.Write("Controls", $"{PerformReadouts.SourceTag(source)}: {ControlDefaults.Name(action)}");

        // VC-46: GO or any pedal confirms what voice proposed.
        if ((action == ControlAction.Go || source == ActionSource.Pedal) && await ConfirmVoiceAsync())
            return;

        switch (action)
        {
            case ControlAction.Go:
                await GoAsync();
                break;
            case ControlAction.Next:
                await NextItemAsync(source);
                break;
            case ControlAction.Back:
                if (CanCancel)
                    await CancelLiveAsync();
                else if (!IsServiceLock)
                    await RestartSectionAsync();
                break;
            case ControlAction.Cancel:
                await CancelLiveAsync();
                break;
            case ControlAction.PlayPause:
                await PlayPauseAsync();
                break;
            case ControlAction.Stop:
                if (IsServiceLock)
                    ShowToast("Stop needs a held press in Service lock.");
                else
                    await StopAsync();
                break;
            case ControlAction.MoveOn:
                await LiveAsync(LiveActionKind.MoveOn, source: source);
                break;
            case ControlAction.Vamp:
                await LiveAsync(LiveActionKind.Vamp, source: source);
                break;
            case ControlAction.RepeatOnce:
                await LiveAsync(LiveActionKind.RepeatOnce, source: source);
                break;
            case ControlAction.End:
                await LiveAsync(LiveActionKind.End, source: source);
                break;
            case ControlAction.KeyUp:
                StepKey(1, LoadedEntry?.Id);
                break;
            case ControlAction.KeyDown:
                StepKey(-1, LoadedEntry?.Id);
                break;
            case ControlAction.Panic:
                await PanicAsync();
                break;
            case ControlAction.RestartSection:
                await RestartSectionAsync();
                break;
            case ControlAction.LoopSection:
                await ToggleLoopAsync();
                break;
        }
    }

    /// <summary>
    /// The page-turner's forward key: GO in a hold, move on from a repeat, else the next plan item (LIVE-50). With a change
    /// pending it takes the item after the pending target.
    /// </summary>
    private async Task NextItemAsync(ActionSource source)
    {
        if (_holding)
        {
            await GoAsync();
            return;
        }

        if (EnsureLive() is not { } live || _song is null)
            return;
        var plan = live.Committed;
        var index = plan.ItemIndexAt(_planPosition);
        if (plan.Items[index].IsOpen)
        {
            await LiveAsync(LiveActionKind.MoveOn, source: source);
            return;
        }

        PlanItem? next = null;
        for (var i = index + 1; i < plan.Items.Count && next is null; i++)
        {
            if (plan.Items[i].Kind == PlanItemKind.Section && plan.Items[i].Length > 0)
                next = plan.Items[i];
        }

        if (live.Pending is { } pending)
        {
            // Pressing again moves the target on one more item.
            next = pending.Plan.Items.FirstOrDefault(i => i.Kind == PlanItemKind.Section && i.PlanStart >= pending.ExecuteAt + 0.002 && i.Label.ToUpperInvariant() != pending.Target.ToUpperInvariant())
                   ?? next;
        }

        if (next is not null)
            await LiveAsync(LiveActionKind.JumpToItem, next.Id, source: source);
    }
}
