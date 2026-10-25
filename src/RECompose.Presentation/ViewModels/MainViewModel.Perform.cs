// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Planning;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>Rehearsal allows everything; Service lock guards what would stop the music (WF-11, LIVE-3).</summary>
public enum LockMode
{
    Rehearsal,
    Service,
}

public sealed record TimingChoice(JumpTiming Timing, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One tile of the plan strip (UI-14).</summary>
public sealed class TileViewModel(StripTile tile) : ObservableObject
{
    private StripTile _tile = tile;
    private bool _armed;

    public string Id => _tile.Id;
    public string Label => _tile.Label;
    public string Detail => _tile.Detail;
    public bool HasDetail => _tile.Detail.Length > 0;
    public bool IsHold => _tile.IsHold;
    public bool IsLive => _tile.IsLive;
    public TileState State => _tile.State;
    public bool IsCurrent => _tile.State == TileState.Current;
    public bool IsPlayed => _tile.State == TileState.Played;
    public bool IsTarget => _tile.State == TileState.Target;
    public bool IsSkipped => _tile.State == TileState.Skipped;

    /// <summary>"tap again to jump" (Service lock, WF-11).</summary>
    public bool Armed { get => _armed; set => Set(ref _armed, value); }

    public bool Same(StripTile other) => _tile == other;

    public void Update(StripTile other)
    {
        _tile = other;
        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class MainViewModel
{
    private bool _isPerform;
    private LockMode _lock = LockMode.Rehearsal;
    private TileViewModel? _armedTile;
    private int _armVersion;
    private RelayCommand? _togglePerform, _go, _vamp, _repeat, _moveOn, _end, _cancel, _panic;

    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    public IReadOnlyList<TimingChoice> TimingChoices { get; } =
    [
        new(JumpTiming.EndOfSection, "End of section"),
        new(JumpTiming.NextBar, "Next bar"),
        new(JumpTiming.NextBeat, "Next beat"),
        new(JumpTiming.Immediately, "Immediately"),
    ];

    public TimingChoice SelectedTiming
    {
        get => TimingChoices.First(c => c.Timing == Timing);
        set
        {
            if (value is not null)
                Timing = value.Timing;
        }
    }

    /// <summary>The Perform view is showing (WF-10). Playback never stops when it changes.</summary>
    public bool IsPerform
    {
        get => _isPerform;
        set
        {
            if (Set(ref _isPerform, value))
            {
                OnPropertyChanged(nameof(IsPrepare));
                if (!value)
                    ClearArmedTile();
            }
        }
    }

    public bool IsPrepare => !_isPerform;

    public LockMode Lock
    {
        get => _lock;
        set
        {
            if (Set(ref _lock, value))
            {
                OnPropertyChanged(nameof(IsServiceLock));
                OnPropertyChanged(nameof(IsRehearsalLock));
                OnPropertyChanged(nameof(LockLabel));
                ClearArmedTile();
                AppLog.Write("Perform", $"Lock: {value}");
                OnLockChanged();
            }
        }
    }

    public bool IsServiceLock { get => _lock == LockMode.Service; set => Lock = value ? LockMode.Service : LockMode.Rehearsal; }
    public bool IsRehearsalLock { get => _lock == LockMode.Rehearsal; set => Lock = value ? LockMode.Rehearsal : LockMode.Service; }
    public string LockLabel => IsServiceLock ? "Service lock" : "Rehearsal";

    public RelayCommand TogglePerformCommand => _togglePerform ??= new RelayCommand(() => IsPerform = !IsPerform);
    public RelayCommand GoCommand => _go ??= new RelayCommand(GoAsync, () => _holding);
    public RelayCommand VampCommand => _vamp ??= new RelayCommand(() => LiveAsync(LiveActionKind.Vamp), () => _song is not null);
    public RelayCommand RepeatCommand => _repeat ??= new RelayCommand(() => LiveAsync(LiveActionKind.RepeatOnce), () => _song is not null);
    public RelayCommand MoveOnCommand => _moveOn ??= new RelayCommand(() => LiveAsync(LiveActionKind.MoveOn), () => _song is not null);
    public RelayCommand EndCommand => _end ??= new RelayCommand(() => LiveAsync(LiveActionKind.End), () => _song is not null);
    public RelayCommand CancelCommand => _cancel ??= new RelayCommand(CancelLiveAsync, () => CanCancel);
    public RelayCommand PanicCommand => _panic ??= new RelayCommand(PanicAsync);

    /// <summary>
    /// PANIC (UI-40, REL-40): the song stops with a short fade, the pads and the transition click stop, and the pending
    /// action is cleared. Always one press, in either lock.
    /// </summary>
    public async Task PanicAsync()
    {
        _live?.Cancel();
        if (_engine is { } engine)
            await engine.PanicAsync();
        AppLog.Write("Perform", "Panic");
        ShowToast("Stopped.");
        UpdateReadouts();
        RememberPanic();
    }

    /// <summary>
    /// A tap on a plan tile (UI-14, WF-11): in Rehearsal lock it jumps; in Service lock the first tap arms the tile and a
    /// second tap within 2 seconds jumps.
    /// </summary>
    public async Task TapTileAsync(TileViewModel tile, ActionSource source = ActionSource.Manual)
    {
        if (tile.IsCurrent || tile.IsHold || tile.IsTarget)
            return;

        if (IsServiceLock)
        {
            if (!ReferenceEquals(_armedTile, tile))
            {
                ClearArmedTile();
                _armedTile = tile;
                tile.Armed = true;
                var version = ++_armVersion;
                _ui.After(TimeSpan.FromSeconds(2), () => _ui.Post(() =>
                {
                    if (version == _armVersion)
                        ClearArmedTile();
                }));
                return;
            }

            ClearArmedTile();
        }

        await LiveAsync(LiveActionKind.JumpToItem, tile.Id, source: source);
    }

    private void ClearArmedTile()
    {
        if (_armedTile is { } tile)
            tile.Armed = false;
        _armedTile = null;
        _armVersion++;
    }

    /// <summary>Keeps the tiles in step with the plan strip; they are updated in place so a tap or a hover is not lost to a redraw.</summary>
    private void SyncTiles(IReadOnlyList<StripTile> strip)
    {
        var same = Tiles.Count == strip.Count;
        for (var i = 0; same && i < strip.Count; i++)
            same = Tiles[i].Same(strip[i]);
        if (same)
            return;

        if (Tiles.Count == strip.Count && Tiles.Select(t => t.Id).SequenceEqual(strip.Select(t => t.Id)))
        {
            for (var i = 0; i < strip.Count; i++)
                if (!Tiles[i].Same(strip[i]))
                    Tiles[i].Update(strip[i]);
            return;
        }

        ClearArmedTile();
        Tiles.Clear();
        foreach (var tile in strip)
            Tiles.Add(new TileViewModel(tile));
    }

    /// <summary>Re-evaluates the live keys after anything that changes what they can do.</summary>
    private void RefreshLiveCommands()
    {
        GoCommand.Refresh();
        CancelCommand.Refresh();
        VampCommand.Refresh();
        RepeatCommand.Refresh();
        MoveOnCommand.Refresh();
        EndCommand.Refresh();
        LoopCommand.Refresh();
        RestartSectionCommand.Refresh();
        ClickGuideOnlyCommand.Refresh();
    }
}
