// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Planning;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Rehearsal (ARR-70…74, PB-64): loop a section with a bar of click between passes, restart it, play from a tile, hear only the
/// click and the guide, and practise at a slower tempo. All of it is switched off by Service lock (WF-11).
/// </summary>
public sealed partial class MainViewModel
{
    private bool _clickGuideOnly;
    private int _practicePercent = 100;
    private readonly HashSet<string> _silencedForRehearsal = [];
    private RelayCommand? _loop, _restart, _clickGuide;

    public static IReadOnlyList<int> PracticeChoices { get; } = [100, 95, 90, 85, 80, 75, 70, 60, 50];

    public RelayCommand LoopCommand => _loop ??= new RelayCommand(ToggleLoopAsync, () => _song is not null);
    public RelayCommand RestartSectionCommand => _restart ??= new RelayCommand(RestartSectionAsync, () => _song is not null);
    public RelayCommand ClickGuideOnlyCommand => _clickGuide ??= new RelayCommand(() => ClickGuideOnly = !ClickGuideOnly, () => _song is not null);

    /// <summary>The current section loops (ARR-70).</summary>
    public bool IsLooping => _song is not null && _perform is { Next.State: NextState.Repeating } && Song!.Plan.Items.Any(i => i.IsOpen && i.EntryId is { } e && e.StartsWith("live:", StringComparison.Ordinal));

    /// <summary>Only the click and the guide sound, to hear the count (ARR-74). Nothing in the song's mix changes.</summary>
    public bool ClickGuideOnly
    {
        get => _clickGuideOnly;
        set
        {
            if (!Set(ref _clickGuideOnly, value))
                return;
            ApplyClickGuideOnly();
        }
    }

    /// <summary>Practice tempo in percent of the entry tempo (PB-64); 100 = none.</summary>
    public int PracticePercent
    {
        get => _practicePercent;
        set
        {
            value = Math.Clamp(value, 50, 100);
            if (Set(ref _practicePercent, value))
            {
                OnPropertyChanged(nameof(PracticeBadge));
                OnPropertyChanged(nameof(HasPracticeTempo));
                _song?.SetPracticeTempo(value / 100.0);
            }
        }
    }

    public bool HasPracticeTempo => _practicePercent < 100;

    /// <summary>"Practice 85%" while a practice tempo is set (PB-64).</summary>
    public string PracticeBadge => HasPracticeTempo ? $"Practice {_practicePercent.ToString(CultureInfo.InvariantCulture)}%" : string.Empty;

    private void ApplyClickGuideOnly()
    {
        if (_song is null || _engine is not { } engine)
            return;

        foreach (var track in _song.Tracks)
        {
            var silence = _clickGuideOnly && !track.IsClickOrGuide && !track.Mute;
            if (silence)
                _silencedForRehearsal.Add(track.Id);
            else if (!_clickGuideOnly && !_silencedForRehearsal.Contains(track.Id))
                continue;

            // Straight to the engine: the mix itself is not touched, so nothing is saved and nothing to undo.
            _ = engine.SetTrackAsync(track.Id, mute: silence || track.Mute);
        }

        if (!_clickGuideOnly)
            _silencedForRehearsal.Clear();
    }

    /// <summary>The section plays again from its start, at once (ARR-71).</summary>
    public async Task RestartSectionAsync()
    {
        if (EnsureLive() is not { } live)
            return;
        var plan = live.Committed;
        var index = plan.ItemIndexAt(_planPosition);
        if (_holding || plan.Items[index].Kind != PlanItemKind.Section)
            return;

        await LiveAsync(LiveActionKind.JumpToItem, plan.Items[index].Id, source: ActionSource.Manual, timing: JumpTiming.Immediately);
    }

    /// <summary>L: loop the current section with a bar of click between passes, or stop looping after this pass (ARR-70).</summary>
    public async Task ToggleLoopAsync()
    {
        if (IsServiceLock)
        {
            ShowToast("Looping is for Rehearsal.");
            return;
        }

        await LiveAsync(LiveActionKind.Loop);
    }

    /// <summary>A tile's context menu › Play from here (ARR-72): the playhead goes to that item and plays.</summary>
    public async Task PlayFromHereAsync(TileViewModel tile)
    {
        if (_song is null || EnsureLive() is not { } live)
            return;
        var item = live.Committed.Items.FirstOrDefault(i => i.Id == tile.Id);
        if (item is null || item.Length <= 0 && !tile.IsHold)
            return;

        await SeekPlanAsync(item.PlanStart);
        if (_engine is { } engine && !IsPlaying)
            await engine.PlayAsync();
    }

    /// <summary>A new song starts without the previous song's practice tempo or click-only mix.</summary>
    private void ResetRehearsal()
    {
        _silencedForRehearsal.Clear();
        if (_clickGuideOnly)
        {
            _clickGuideOnly = false;
            OnPropertyChanged(nameof(ClickGuideOnly));
        }

        if (_practicePercent != 100)
        {
            _practicePercent = 100;
            OnPropertyChanged(nameof(PracticePercent));
            OnPropertyChanged(nameof(PracticeBadge));
            OnPropertyChanged(nameof(HasPracticeTempo));
        }
    }

    /// <summary>Service lock puts the rehearsal helpers away (WF-11): the loop ends after its pass, the practice tempo and the click-only mix go back.</summary>
    private void OnLockChanged()
    {
        if (!IsServiceLock)
            return;
        if (HasPracticeTempo)
            PracticePercent = 100;
        if (ClickGuideOnly)
            ClickGuideOnly = false;
        if (IsLooping)
            _ = LiveAsync(LiveActionKind.MoveOn);
    }
}
