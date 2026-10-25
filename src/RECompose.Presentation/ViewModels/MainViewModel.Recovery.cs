// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Planning;
using RECompose.Core.Recovery;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>What the MD chose in the dialog after an unexpected close (REL-31).</summary>
public enum ResumeChoice
{
    ResumeCurrent,
    ResumeNext,
    OpenService,
    DontResume,
}

/// <summary>The question of REL-31: what happened, and what can be resumed.</summary>
public sealed record ResumeQuestion(string Message, string? CurrentLabel, string? NextLabel, bool CanOpenService);

/// <summary>
/// Crash recovery (REL-30…34, REL-41): the journal is rewritten every second while playing and whenever the plan changes; the
/// state file says whether the last run ended cleanly; a launch after an unclean end during playback offers to resume at the
/// section that was playing, with the live changes of that session still in the plan.
/// </summary>
public sealed partial class MainViewModel
{
    private Journal? _recovery;
    private DateTime _lastJournal = DateTime.MinValue;
    private bool _journalFailed;
    private string _journalTransport = "stopped";
    private Journal? _panicJournal;
    private Task _journalWrite = Task.CompletedTask;
    private readonly object _journalLock = new();

    /// <summary>How often the journal is rewritten while playing (REL-30). Tests make it shorter.</summary>
    internal TimeSpan JournalInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Reads what the last run left, and marks this one as running (REL-31).</summary>
    private void ReadRecovery()
    {
        var state = _store.LoadState();
        var journal = _store.LoadJournal();
        _recovery = !state.CleanShutdown && journal is { SongId.Length: > 0 } && (journal.WasPlaying || journal.Perform) ? journal : null;
        _store.SaveState(state with { CleanShutdown = false });
        if (_recovery is not null)
            AppLog.Write("Recovery", $"The last session ended unexpectedly while {_recovery.Transport}: {_recovery.SongTitle}");
    }

    /// <summary>A normal quit: the next start has nothing to resume (REL-31).</summary>
    private void MarkCleanShutdown()
    {
        try
        {
            _store.SaveState(_store.LoadState() with { CleanShutdown = true, Mode = IsPerform ? "perform" : "prepare", Lock = IsServiceLock ? "service" : "rehearsal" });
            _store.DeleteJournal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Recovery", ex);
        }
    }

    /// <summary>Called from the snapshot: once a second while playing, and once more when the transport changes.</summary>
    private void JournalTick(TransportState state)
    {
        var transport = state switch { TransportState.Playing => "playing", TransportState.Paused => "paused", _ => "stopped" };
        var changed = transport != _journalTransport;
        _journalTransport = transport;
        if (_song is null || (state != TransportState.Playing && !changed))
            return;
        if (!changed && DateTime.UtcNow - _lastJournal < JournalInterval)
            return;
        WriteJournal();
    }

    /// <summary>Writes the journal now: on a plan change, a stop, a panic (REL-30, REL-41).</summary>
    internal void WriteJournal(bool panicked = false)
    {
        if (_song is not { } song)
            return;
        _lastJournal = DateTime.UtcNow;
        var live = EnsureLive();
        var journal = Journal.From(live?.Committed ?? song.Plan, _planPosition, _journalTransport, song.Id, song.Title) with
        {
            ServiceId = _loadedServiceId,
            ServiceName = _loadedServiceId is { } id ? _services.FirstOrDefault(s => s.Id == id)?.Name : null,
            EntryId = song.Entry?.Id,
            Perform = IsPerform,
            Lock = IsServiceLock ? "service" : "rehearsal",
            Holding = _holding,
            PadKey = Pads.PlayingKey,
            Panicked = panicked,
        };
        if (panicked)
            _panicJournal = journal;

        // Off the UI thread, newest first: a slow disk must not stall the screen, and nothing optional may stop the music (REL-50).
        lock (_journalLock)
        {
            _journalWrite = _journalWrite.ContinueWith(_ =>
            {
                try
                {
                    _store.SaveJournal(journal);
                    _journalFailed = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (!_journalFailed)
                    {
                        _journalFailed = true;
                        AppLog.Error("Recovery", ex);
                        _ui.Post(() => Banner = "Crash recovery unavailable (disk?). Playback is not affected.");
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for the journal to reach the disk (tests, shutdown).</summary>
    internal Task FlushJournalAsync()
    {
        lock (_journalLock)
            return _journalWrite;
    }

    // ---- Resume after a crash ---------------------------------------------------------

    private async Task OfferResumeAsync()
    {
        if (_recovery is not { } journal)
            return;
        _recovery = null;

        // The engine kept playing through a UI crash (REL-34): nothing to resume.
        if (State == TransportState.Playing && _song?.Id == journal.SongId)
            return;
        if (_store.LoadSong(journal.SongId) is not { } model)
            return;

        var plan = RebuildJournalPlan(journal, model);
        var current = ResumeRules.Current(plan, journal.PositionSec);
        var next = ResumeRules.Next(plan, journal.PositionSec);
        var where = current is null ? string.Empty : $"{current.Label}, {BarText(plan, journal.PositionSec)}";
        var during = string.IsNullOrEmpty(journal.ServiceName) ? string.Empty : $" during {journal.ServiceName}";
        var message = $"RECompose closed unexpectedly{during} while playing {journal.SongTitle}" + (where.Length > 0 ? $" — {where}" : string.Empty) + ".";
        var question = new ResumeQuestion(message, current?.Label, next?.Label, journal.ServiceId is not null);

        switch (await _dialogs.AskResumeAsync(question))
        {
            case ResumeChoice.ResumeCurrent when current is not null:
                await ResumeAsync(journal, current);
                break;
            case ResumeChoice.ResumeNext when next is not null:
                await ResumeAsync(journal, next);
                break;
            case ResumeChoice.OpenService when journal.EntryId is { } entry:
                await LoadEntryAsync(entry);
                break;
        }

        _store.DeleteJournal();
    }

    private static string BarText(SongPlan plan, double positionSec) =>
        plan.At(positionSec).NowDetail.Split(" / ")[0];

    private SongPlan RebuildJournalPlan(Journal journal, RECompose.Core.Library.Song model)
    {
        var sections = RECompose.Core.Sections.SectionList.Describe(model.Sections, model.Grid, model.DurationSec);
        return journal.ToPlan(model.Grid, sections, model.DurationSec);
    }

    /// <summary>Loads the entry, puts the journal's plan back with a bar of click before the section, and plays (REL-32).</summary>
    internal async Task ResumeAsync(Journal journal, ResumeTarget target)
    {
        if (journal.EntryId is { } entryId && _services.Any(s => s.Entry(entryId) is not null))
            await LoadEntryAsync(entryId);
        else
            await LoadSongAsync(journal.SongId);
        if (_song is not { } song)
            return;

        var plan = journal.ToPlan(song.Model.Grid, song.Sections, song.DurationSec);
        if (ResumeRules.WithCountIn(plan, target, 1, song.CurrentPlanOptions) is not { } resumed)
            return;

        song.AdoptLivePlan(resumed.Plan);
        _live = null;
        PlanPositionSec = resumed.StartSec;
        await SendPlanAsync(resumed.StartSec);
        IsPerform = journal.Perform;
        Lock = journal.Lock == "service" ? LockMode.Service : LockMode.Rehearsal;
        if (journal.PadKey is { } pad)
            await Pads.PlayAsync(pad);
        _panicJournal = null;
        OnPropertyChanged(nameof(PanicResumeText));
        OnPropertyChanged(nameof(HasPanicResume));
        if (_engine is { } engine)
            await engine.PlayAsync();
        AppLog.Write("Recovery", $"Resumed {journal.SongTitle} at {target.Label}");
    }

    // ---- After PANIC (REL-41) ---------------------------------------------------------

    public bool HasPanicResume => _panicJournal is not null && State != TransportState.Playing;

    /// <summary>"Stopped by PANIC · Resume at Chorus 2".</summary>
    public string PanicResumeText
    {
        get
        {
            if (_panicJournal is not { } journal || _song is null)
                return string.Empty;
            var plan = journal.ToPlan(_song.Model.Grid, _song.Sections, _song.DurationSec);
            return ResumeRules.Current(plan, journal.PositionSec) is { } current ? $"Stopped by PANIC · Resume at {current.Label}" : "Stopped by PANIC";
        }
    }

    private RelayCommand? _resumeAfterPanic;

    public RelayCommand ResumeAfterPanicCommand => _resumeAfterPanic ??= new RelayCommand(async () =>
    {
        if (_panicJournal is not { } journal || _song is null)
            return;
        var plan = journal.ToPlan(_song.Model.Grid, _song.Sections, _song.DurationSec);
        if (ResumeRules.Current(plan, journal.PositionSec) is { } target)
            await ResumeAsync(journal, target);
    });

    /// <summary>PANIC leaves a journal behind that says where it stopped.</summary>
    private void RememberPanic()
    {
        _journalTransport = "stopped";
        WriteJournal(panicked: true);
        OnPropertyChanged(nameof(PanicResumeText));
        OnPropertyChanged(nameof(HasPanicResume));
    }

    private static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
