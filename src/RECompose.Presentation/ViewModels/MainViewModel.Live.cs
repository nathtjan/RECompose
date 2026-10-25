// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Planning;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Live control (milestone 5): the playing song's plan can change while it plays. An action becomes a pending plan in
/// <see cref="LivePlan"/>, is sent to the engine as a swap at its execute point, and becomes the plan the screen shows
/// when the playhead passes that point (PB-44, PB-45, PB-47). Holds wait for GO (PB-52).
/// </summary>
public sealed partial class MainViewModel
{
    private LivePlan? _live;
    private bool _holding;
    private double _holdSec;
    private PerformReadout? _perform;

    /// <summary>The live plan of the loaded song, rebuilt whenever the plan changes by other means (an edit, a new song).</summary>
    private LivePlan? EnsureLive()
    {
        if (_song is null)
        {
            _live = null;
            return null;
        }

        if (_live is null || (!ReferenceEquals(_live.Committed, _song.Plan) && _live.Pending is null))
            _live = new LivePlan(_song.Plan, new LiveOptions { PlanOptions = _song.CurrentPlanOptions, ContinueAsWritten = _settings.ContinueAsWritten });
        return _live;
    }

    /// <summary>What the Perform screen shows now (LIVE-10, LIVE-20…23).</summary>
    public PerformReadout? Perform { get => _perform; private set => Set(ref _perform, value); }

    private PerformReadout UpdatePerform()
    {
        var live = EnsureLive();
        var perform = live is null
            ? throw new InvalidOperationException("No song is loaded.")
            : PerformReadouts.Build(live, _planPosition, _holding, _holdSec);
        Perform = perform;
        SyncTiles(perform.Strip);
        RefreshLiveCommands();
        OnPropertyChanged(nameof(Strip));
        OnPropertyChanged(nameof(NextState));
        OnPropertyChanged(nameof(NextDetail));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(PendingAtSec));
        OnPropertyChanged(nameof(HoldText));
        OnPropertyChanged(nameof(HoldTimerText));
        OnPropertyChanged(nameof(SongBarValue));
        OnPropertyChanged(nameof(ShowPauseIcon));
        OnPropertyChanged(nameof(ShowGoIcon));
        OnPropertyChanged(nameof(IsHolding));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(CanCancel));
        return perform;
    }

    public IReadOnlyList<StripTile> Strip => _perform?.Strip ?? [];
    public NextState NextState => _perform?.Next.State ?? NextState.Planned;
    public string NextDetail => _perform?.Next.Detail ?? string.Empty;
    public bool IsPending => _perform?.Next.State == NextState.Pending;
    public double? PendingAtSec => _perform?.PendingAtSec;
    public bool CanCancel => _live?.Pending is not null;

    /// <summary>The clock waits for GO (PB-52).</summary>
    public bool IsHolding => _holding;

    /// <summary>"Holding for 0:23" (PB-33).</summary>
    public string HoldText => _holding ? PerformReadouts.HoldText(_holdSec) : string.Empty;

    /// <summary>The hold timer on its own, "0:23", under the label HOLDING FOR.</summary>
    public string HoldTimerText => _holding ? RECompose.Core.Timing.Format.Clock(_holdSec) : string.Empty;

    /// <summary>"3.2 / 49": the song bar without its word, for the narrow readouts column.</summary>
    public string SongBarValue => SongBarText.StartsWith("bar ", StringComparison.Ordinal) ? SongBarText[4..] : SongBarText;

    /// <summary>The play key shows a pause glyph while playing, GO in a hold.</summary>
    public bool ShowPauseIcon => IsPlaying && !_holding;

    public bool ShowGoIcon => _holding;

    /// <summary>The elapsed and the remaining time of PB-33, one value each.</summary>
    public string ElapsedText => TimeText.Split(" / ", 2)[0];

    public string RemainingText => TimeText.Split(" / ", 2) is { Length: 2 } parts ? parts[1] : string.Empty;

    /// <summary>When a jump or a vamp takes effect (PB-44). The default is the end of the section.</summary>
    public JumpTiming Timing
    {
        get => Enum.TryParse<JumpTiming>(_settings.JumpTiming, out var timing) ? timing : JumpTiming.EndOfSection;
        set
        {
            if (Timing == value)
                return;
            _settings = _settings with { JumpTiming = value.ToString() };
            _store.SaveSettings(_settings);
            OnPropertyChanged();
        }
    }

    private void OnHoldChanged()
    {
        OnPropertyChanged(nameof(IsHolding));
        OnPropertyChanged(nameof(PlayLabel));
        UpdateReadouts();
    }

    /// <summary>The playhead passed the execute point of the pending action: its plan is the plan now.</summary>
    private void TickLive()
    {
        if (_live is not { } live || _song is not { } song)
            return;
        if (!live.Tick(_planPosition))
            return;

        song.AdoptLivePlan(live.Committed);
        Sections.Refresh();
        if (song.Entry is not null)
            _ = SendTransitionAsync(live.Committed);
        UpdateReadouts();
    }

    /// <summary>Runs a live action on the playing song (PB-44…47, PB-50…52). Returns what happened.</summary>
    public async Task<LiveResult> LiveAsync(LiveActionKind kind, string? itemId = null, string? sectionId = null, ActionSource source = ActionSource.Manual, JumpTiming? timing = null)
    {
        if (EnsureLive() is not { } live)
            return new LiveResult(LiveOutcome.Rejected, null, "Nothing is loaded.");
        if (State != TransportState.Playing)
            return new LiveResult(LiveOutcome.Rejected, null, "Live changes need the song playing.");

        if (kind == LiveActionKind.MoveOn && _holding)
        {
            await GoAsync();
            return new LiveResult(LiveOutcome.ReleaseHold, null, "Go.");
        }

        var result = live.Apply(new LiveAction(kind, timing ?? Timing, itemId, sectionId, source), _planPosition, _holding);
        switch (result.Outcome)
        {
            case LiveOutcome.Queued:
                AppLog.Write("Live", $"{kind} ({SourceName(source)}) → {result.Pending!.Target} at {result.Pending.ExecuteAt:0.000}");
                await SendPlanAsync(null, result.Pending.Plan, result.Pending.ExecuteAt);
                UpdateReadouts();
                break;
            default:
                if (result.Message.Length > 0)
                    ShowToast(result.Message);
                break;
        }

        return result;
    }

    private static string SourceName(ActionSource source) => PerformReadouts.SourceTag(source);

    /// <summary>Withdraws the pending action; the plan the engine swaps to at the same point is the one that was playing (PB-45).</summary>
    public async Task CancelLiveAsync()
    {
        if (_live is not { Pending: { } pending } live)
            return;
        live.Cancel();
        await SendPlanAsync(null, live.Committed, pending.ExecuteAt);
        UpdateReadouts();
    }

    /// <summary>Tests: feeds the view model a snapshot as if the engine had sent it.</summary>
    internal void ApplySnapshotForTests(EngineSnapshot snapshot) => ApplySnapshot(snapshot);

    /// <summary>GO: releases the hold (PB-52).</summary>
    public async Task GoAsync()
    {
        if (_engine is { } engine && _holding)
            await engine.GoAsync();
    }
}
