// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Controls;
using RECompose.Core.Planning;
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

    internal void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        _settings = change(_settings);
        _store.SaveSettings(_settings);
    }

    /// <summary>Settings › Controls and › Playback.</summary>
    public ControlsViewModel Controls => _controls ??= new ControlsViewModel(this);

    public RelayCommand OpenControlsCommand => _openControls ??= new RelayCommand(() => _dialogs.EditControlsAsync(Controls));

    private void OnMidi(MidiMessage message) => _ui.Post(() => Controls.Midi(message));

    /// <summary>Starts listening to the controllers once the engine is there (LIVE-52).</summary>
    private async Task OpenMidiAsync(EngineClient engine)
    {
        engine.MidiReceived += OnMidi;
        try
        {
            var open = await engine.SetMidiInputsAsync(null);
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

    internal async Task RunControlAsync(ControlAction action, ActionSource source)
    {
        AppLog.Write("Controls", $"{PerformReadouts.SourceTag(source)}: {ControlDefaults.Name(action)}");
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
