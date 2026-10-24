// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Sections;
using RECompose.Core.Timing;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>A choice in the Type select (SEC-43).</summary>
public sealed record SectionTypeChoice(SectionType Type, string Name)
{
    public static IReadOnlyList<SectionTypeChoice> All { get; } =
        [.. Enum.GetValues<SectionType>().Select(t => new SectionTypeChoice(t, SectionTypes.DisplayName(t)))];

    public override string ToString() => Name;
}

/// <summary>One row of the sections table (SEC-51).</summary>
public sealed class SectionRowViewModel : ObservableObject
{
    private readonly SectionsViewModel _owner;
    private string _name;
    private string _startText;
    private SectionTypeChoice _type;
    private bool _flash;

    internal SectionRowViewModel(SectionsViewModel owner, SectionInfo info, GuideCall? call)
    {
        _owner = owner;
        Info = info;
        Call = call;
        _name = info.Section.Name;
        _startText = info.StartSec.ToString("0.000", CultureInfo.InvariantCulture);
        _type = SectionTypeChoice.All.First(c => c.Type == info.Type);
        PlayCallCommand = new RelayCommand(() => owner.PlayCallAsync(this), () => Call is not null && owner.CanPlayRanges);
        ToPlayheadCommand = new RelayCommand(() => owner.SetStartToPlayhead(this));
        DeleteCommand = new RelayCommand(() => owner.Delete(this));
    }

    public SectionInfo Info { get; }
    public GuideCall? Call { get; }
    public string Id => Info.Id;
    public string Label => Info.Label;
    public string Badge => Info.Badge ?? string.Empty;
    public bool HasBadge => Info.Badge is not null;
    public string ColorToken => Info.ColorToken;
    public string ChartBarText => Info.ChartBar.ToString(CultureInfo.InvariantCulture);
    public string LengthText => Info.LengthBars.ToString(CultureInfo.InvariantCulture);
    public bool NeedsReview => Info.Section.NeedsReview;
    public bool HasCall => Call is not null;
    public string CallTip => Call is null ? "No guide call for this section" : $"Play the guide call: \"{Call.Text}\"";

    /// <summary>The editable name; committed with <see cref="CommitName"/> (Enter or leaving the field).</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>The editable start in seconds; committed with <see cref="CommitStart"/>, snapped to the nearest bar.</summary>
    public string StartText { get => _startText; set => Set(ref _startText, value); }

    public IReadOnlyList<SectionTypeChoice> Types => SectionTypeChoice.All;

    public SectionTypeChoice Type
    {
        get => _type;
        set
        {
            if (value is not null && Set(ref _type, value) && value.Type != Info.Type)
                _owner.SetType(this, value.Type);
        }
    }

    /// <summary>Briefly true when M is pressed where this section already starts (SEC-52).</summary>
    public bool Flash { get => _flash; set => Set(ref _flash, value); }

    public RelayCommand PlayCallCommand { get; }
    public RelayCommand ToPlayheadCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public void CommitName()
    {
        if (_name.Trim() != Info.Section.Name)
            _owner.Rename(this, _name);
    }

    public void CommitStart()
    {
        if (double.TryParse(_startText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            _owner.SetStart(this, seconds);
        else
            StartText = Info.StartSec.ToString("0.000", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The SECTIONS tab (UI-16, SEC-10…53): Detect from guide with its result box, the sections
/// table, the review flow, + At playhead. Edits go through <see cref="MainViewModel"/> so each
/// is one undo step and is saved at once.
/// </summary>
public sealed class SectionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private SectionRowViewModel? _selected;
    private string _result = string.Empty;
    private string? _busyText;
    private int _reviewIndex = -1;
    private List<string> _reviewIds = [];

    internal SectionsViewModel(MainViewModel main)
    {
        _main = main;
        DetectCommand = new RelayCommand(() => _main.DetectSectionsAsync(), () => CanDetect && !IsBusy);
        ReviewCommand = new RelayCommand(StartReviewAsync, () => Rows.Any(r => r.NeedsReview) && CanPlayRanges);
        AddAtPlayheadCommand = new RelayCommand(() => AddAtPlayhead(), () => _main.Song is not null);
        AcceptCommand = new RelayCommand(Accept, () => IsReviewing);
        EarlierCommand = new RelayCommand(() => NudgeAsync(-1), () => IsReviewing);
        LaterCommand = new RelayCommand(() => NudgeAsync(1), () => IsReviewing);
        ReviewDeleteCommand = new RelayCommand(ReviewDeleteAsync, () => IsReviewing);
        NextCommand = new RelayCommand(NextAsync, () => IsReviewing);
        StopReviewCommand = new RelayCommand(StopReview, () => IsReviewing);
        ReplayCommand = new RelayCommand(() => ReviewItem is { } r ? _main.PlayAroundAsync(r.Info.StartSec) : Task.CompletedTask, () => IsReviewing);
    }

    /// <summary>Raised to focus a row's name field (+ At playhead, double-click a block, R in review).</summary>
    public event Action<string>? FocusNameRequested;

    /// <summary>Raised to open a row's Type select (T in review).</summary>
    public event Action<string>? FocusTypeRequested;

    public ObservableCollection<SectionRowViewModel> Rows { get; } = [];

    public SectionRowViewModel? Selected { get => _selected; set => Set(ref _selected, value); }

    /// <summary>The SEC-16 result box.</summary>
    public string ResultText
    {
        get => _result;
        internal set
        {
            if (Set(ref _result, value))
                OnPropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => _result.Length > 0;

    /// <summary>"Analyzing… Listening to the guide 40 %" while Detect waits for the transcript (SEC-11).</summary>
    public string? BusyText
    {
        get => _busyText;
        internal set
        {
            if (Set(ref _busyText, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                DetectCommand.Refresh();
            }
        }
    }

    public bool IsBusy => _busyText is not null;

    public bool HasGuide => _main.Song?.Model is { } m && Services.AnalysisService.GuideTrack(m) is not null;
    public bool CanDetect => HasGuide;

    /// <summary>UI-80: why Detect is unavailable.</summary>
    public string DetectHint => HasGuide
        ? "Detect from guide listens to the guide track's spoken calls."
        : "No guide track, so sections can't be detected. Play the song and press M at each section.";

    public bool IsEmpty => Rows.Count == 0;
    public int ReviewCount => Rows.Count(r => r.NeedsReview);
    public string ReviewLabel => ReviewCount > 0 ? $"Review ({ReviewCount.ToString(CultureInfo.InvariantCulture)})" : "Review";

    // ---- Rows -------------------------------------------------------------------------

    internal void Refresh()
    {
        var song = _main.Song;
        var selectedId = _selected?.Id;
        Rows.Clear();
        if (song is not null)
        {
            var calls = song.Model.GuideAnalysis?.Calls ?? [];
            foreach (var info in song.Sections)
            {
                var call = calls.FirstOrDefault(c => c.Id == info.Section.CallId);
                Rows.Add(new SectionRowViewModel(this, info, call));
            }
        }

        Selected = Rows.FirstOrDefault(r => r.Id == selectedId);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasGuide));
        OnPropertyChanged(nameof(CanDetect));
        OnPropertyChanged(nameof(DetectHint));
        OnPropertyChanged(nameof(ReviewCount));
        OnPropertyChanged(nameof(ReviewLabel));
        OnPropertyChanged(nameof(CanPlayRanges));
        OnPropertyChanged(nameof(PlayRangesBlockedReason));
        DetectCommand.Refresh();
        ReviewCommand.Refresh();
        AddAtPlayheadCommand.Refresh();

        if (IsReviewing)
        {
            _reviewIds.RemoveAll(id => Rows.All(r => r.Id != id));
            OnReviewChanged();
        }
    }

    /// <summary>Review and the call button play stretches of the written song, so they wait for As written.</summary>
    public bool CanPlayRanges => _main.Song?.IsAsWritten ?? true;

    public string? PlayRangesBlockedReason => CanPlayRanges ? null : "Switch to As written to review sections";

    public void Select(string id) => Selected = Rows.FirstOrDefault(r => r.Id == id);

    internal void Flash(string id)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is not { } row)
            return;
        Selected = row;
        row.Flash = true;
        _main.After(TimeSpan.FromMilliseconds(700), () => row.Flash = false);
    }

    private TempoGrid Grid => _main.Song!.Grid;
    private double Duration => _main.Song!.DurationSec;
    private List<Section> Current => _main.Song?.Model.Sections ?? [];

    internal void Rename(SectionRowViewModel row, string name)
    {
        if (name.Trim().Length == 0)
        {
            row.Name = row.Info.Section.Name;
            return;
        }
        // A row from before the last refresh may commit again as its field loses focus.
        if (Current.FirstOrDefault(s => s.Id == row.Id) is not { } section || section.Name == name.Trim())
            return;
        _main.CommitSections(SectionList.Rename(Current, row.Id, name), $"Renamed {row.Label} to {name.Trim()}");
    }

    internal void SetType(SectionRowViewModel row, SectionType type)
    {
        if (Current.FirstOrDefault(s => s.Id == row.Id) is not { } section || section.Type == type)
            return;
        _main.CommitSections(SectionList.SetType(Current, row.Id, type), $"{row.Label} is now a {SectionTypes.DisplayName(type)} section");
    }

    internal void SetStart(SectionRowViewModel row, double seconds)
    {
        var moved = SectionList.SetStart(Current, row.Id, seconds, Grid, Duration);
        if (SameStarts(moved, Current))
        {
            row.StartText = row.Info.StartSec.ToString("0.000", CultureInfo.InvariantCulture);
            return;
        }
        _main.CommitSections(moved, $"Moved {row.Label}");
    }

    internal void SetStartToPlayhead(SectionRowViewModel row)
    {
        var moved = SectionList.SetStart(Current, row.Id, _main.PositionSec, Grid, Duration);
        if (SameStarts(moved, Current))
        {
            _main.ShowToast($"{row.Label} can't start there: it would pass a neighbouring section.");
            return;
        }
        _main.CommitSections(moved, $"Moved {row.Label} to the playhead");
    }

    internal void Delete(SectionRowViewModel row) =>
        _main.CommitSections(SectionList.Delete(Current, row.Id), $"Deleted {row.Label}");

    /// <summary>The lane's boundary drag (SEC-50). Alt snaps to beats.</summary>
    public void MoveStart(string id, double seconds, bool snapToBeat)
    {
        if (_main.Song is null)
            return;
        var label = Rows.FirstOrDefault(r => r.Id == id)?.Label ?? "section";
        var moved = SectionList.Move(Current, id, seconds, Grid, Duration, snapToBeat);
        if (!SameStarts(moved, Current))
            _main.CommitSections(moved, $"Moved {label}");
    }

    public void SetTypeOf(string id, SectionType type)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is { } row)
            SetType(row, type);
    }

    public void DeleteById(string id)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is { } row)
            Delete(row);
    }

    /// <summary>Split at the playhead (SEC-50, S in the as-written view).</summary>
    public void SplitAtPlayhead()
    {
        if (_main.Song is null)
            return;
        var (sections, added) = SectionList.Split(Current, _main.PositionSec, Grid, Duration);
        if (added is null)
        {
            _main.ShowToast("Move the playhead inside a section to split it.");
            return;
        }
        _main.CommitSections(sections, $"Split {added.Name} at chart bar {Readouts.ChartBar(Grid, added.StartSec).Bar.ToString(CultureInfo.InvariantCulture)}");
    }

    public void MergeWithNext(string id)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        var index = row is null ? -1 : Rows.IndexOf(row);
        if (row is null || index + 1 >= Rows.Count)
            return;
        _main.CommitSections(SectionList.MergeWithNext(Current, id), $"Merged {Rows[index + 1].Label} into {row.Label}",
            merged: (Rows[index + 1].Id, id));
    }

    public void RequestRename(string id)
    {
        Select(id);
        FocusNameRequested?.Invoke(id);
    }

    /// <summary>SEC-53: + At playhead marks like M, then focuses the name.</summary>
    public void AddAtPlayhead()
    {
        if (_main.MarkAtPlayhead() is { } id)
            RequestRename(id);
    }

    internal Task PlayCallAsync(SectionRowViewModel row) =>
        row.Call is { } call ? _main.PlayRangeAsync(Math.Max(0, call.Start - 0.2), call.End + 0.3, null) : Task.CompletedTask;

    private static bool SameStarts(IReadOnlyList<Section> a, IReadOnlyList<Section> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && Math.Abs(p.First.StartSec - p.Second.StartSec) < 1e-6);

    // ---- Review flow (SEC-30, SEC-31) -----------------------------------------------------

    public bool IsReviewing => _reviewIndex >= 0;

    public SectionRowViewModel? ReviewItem =>
        IsReviewing && _reviewIndex < _reviewIds.Count ? Rows.FirstOrDefault(r => r.Id == _reviewIds[_reviewIndex]) : null;

    /// <summary>"Review 1 of 2 · Verse 3".</summary>
    public string ReviewTitle => ReviewItem is { } r
        ? $"Review {(_reviewIndex + 1).ToString(CultureInfo.InvariantCulture)} of {_reviewIds.Count.ToString(CultureInfo.InvariantCulture)} · {r.Label}"
        : string.Empty;

    /// <summary>"heard 'verse three' at 1:28.1 · chart bar 27".</summary>
    public string ReviewHeard
    {
        get
        {
            if (ReviewItem is not { } r)
                return string.Empty;
            var bar = $"chart bar {r.ChartBarText}";
            if (r.Call is not { } call)
                return $"No guide call for this section · {bar}";
            var seconds = call.Start;
            var time = $"{(int)(seconds / 60)}:{(seconds % 60).ToString("00.0", CultureInfo.InvariantCulture)}";
            return $"heard '{call.Text.Trim().TrimEnd('.', ',', '!', '?').ToLowerInvariant()}' at {time} · {bar}";
        }
    }

    private async Task StartReviewAsync()
    {
        _reviewIds = Rows.Where(r => r.NeedsReview).Select(r => r.Id).ToList();
        if (_reviewIds.Count == 0)
            return;
        _reviewIndex = 0;
        OnReviewChanged();
        await PlayCurrentAsync();
    }

    private Task PlayCurrentAsync()
    {
        if (ReviewItem is not { } row)
        {
            StopReview();
            return Task.CompletedTask;
        }
        Selected = row;
        return _main.PlayAroundAsync(row.Info.StartSec);
    }

    private void Accept()
    {
        if (ReviewItem is not { } row)
            return;
        _main.CommitSections(SectionList.Accept(Current, row.Id), $"Accepted {row.Label}");
        _ = AdvanceAsync();
    }

    private async Task NudgeAsync(int bars)
    {
        if (ReviewItem is not { } row)
            return;
        var moved = SectionList.Nudge(Current, row.Id, bars, Grid, Duration);
        if (SameStarts(moved, Current))
        {
            _main.ShowToast(bars < 0 ? $"{row.Label} can't move earlier." : $"{row.Label} can't move later.");
            return;
        }
        _main.CommitSections(moved, $"Moved {row.Label} {(bars < 0 ? "a bar earlier" : "a bar later")}");
        await PlayCurrentAsync();
    }

    private async Task ReviewDeleteAsync()
    {
        if (ReviewItem is not { } row)
            return;
        _reviewIds.Remove(row.Id);
        _reviewIndex--;
        Delete(row);
        await AdvanceAsync();
    }

    private Task NextAsync() => AdvanceAsync();

    private async Task AdvanceAsync()
    {
        _reviewIndex++;
        if (_reviewIndex >= _reviewIds.Count)
        {
            StopReview();
            _main.ShowToast(ReviewCount == 0 ? "Review done. No sections need review." : $"Review done. {ReviewCount} still need review.");
            return;
        }
        OnReviewChanged();
        await PlayCurrentAsync();
    }

    public void StopReview()
    {
        if (!IsReviewing)
            return;
        _reviewIndex = -1;
        _reviewIds = [];
        _main.CancelRangePlayback();
        OnReviewChanged();
    }

    /// <summary>R and T in the review (SEC-31).</summary>
    public void ReviewRename()
    {
        if (ReviewItem is { } r)
            RequestRename(r.Id);
    }

    public void ReviewChangeType()
    {
        if (ReviewItem is { } r)
        {
            Select(r.Id);
            FocusTypeRequested?.Invoke(r.Id);
        }
    }

    public void ReviewAccept() => AcceptCommand.Execute(null);
    public void ReviewEarlier() => EarlierCommand.Execute(null);
    public void ReviewLater() => LaterCommand.Execute(null);
    public void ReviewDelete() => ReviewDeleteCommand.Execute(null);
    public void ReviewNext() => NextCommand.Execute(null);

    private void OnReviewChanged()
    {
        OnPropertyChanged(nameof(IsReviewing));
        OnPropertyChanged(nameof(ReviewItem));
        OnPropertyChanged(nameof(ReviewTitle));
        OnPropertyChanged(nameof(ReviewHeard));
        foreach (var c in new[] { AcceptCommand, EarlierCommand, LaterCommand, ReviewDeleteCommand, NextCommand, StopReviewCommand, ReplayCommand })
            c.Refresh();
    }

    public RelayCommand DetectCommand { get; }
    public RelayCommand ReviewCommand { get; }
    public RelayCommand AddAtPlayheadCommand { get; }
    public RelayCommand AcceptCommand { get; }
    public RelayCommand EarlierCommand { get; }
    public RelayCommand LaterCommand { get; }
    public RelayCommand ReviewDeleteCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand StopReviewCommand { get; }
    public RelayCommand ReplayCommand { get; }
}
