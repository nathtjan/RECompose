// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Analysis;
using RECompose.Core.Commands;
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Milestone 2 on the Prepare screen: the background analysis after import (SL-54), Detect
/// from guide (SEC-10…17), section edits with undo (SEC-50…53), the review playback
/// (SEC-30) and the NOW and NEXT readouts (PB-30…35).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _jobs = new(1, 1);
    private readonly Dictionary<string, Task<string?>> _transcribing = [];
    private string _nowLabel = string.Empty;
    private string _nowDetail = string.Empty;
    private string _nextLabel = string.Empty;
    private string _nextTag = string.Empty;
    private string _countdown = string.Empty;
    private double _sectionProgress;
    private string? _activity;
    private double? _rangeEnd;      // review and call playback stop here…
    private double? _rangeReturn;   // …and put the playhead back here
    private bool _rangeStarted;

    public SectionsViewModel Sections { get; }

    public bool HasSections => _song?.HasSections ?? false;

    /// <summary>"CHORUS 1" (PB-30), "LEAD-IN" before the first section.</summary>
    public string NowLabel { get => _nowLabel; private set => Set(ref _nowLabel, value); }

    /// <summary>"bar 6 / 14" (PB-31), or "beat −3" in the lead-in.</summary>
    public string NowDetail { get => _nowDetail; private set => Set(ref _nowDetail, value); }

    /// <summary>The next item's label: "VERSE 2", "END OF SONG" (LIVE-20 rule 4).</summary>
    public string NextLabel { get => _nextLabel; private set => Set(ref _nextLabel, value); }

    /// <summary>The NEXT card's source tag: "AS WRITTEN" until arrangements arrive (M3).</summary>
    public string NextTag { get => _nextTag; private set => Set(ref _nextTag, value); }

    /// <summary>"in 8 bars, 2 beats" (PB-35).</summary>
    public string CountdownText { get => _countdown; private set => Set(ref _countdown, value); }

    /// <summary>0…1 through the current section.</summary>
    public double SectionProgress { get => _sectionProgress; private set => Set(ref _sectionProgress, value); }

    /// <summary>The top bar's background activity line (UI-11), or null when idle.</summary>
    public string? BackgroundActivity
    {
        get => _activity;
        private set
        {
            if (Set(ref _activity, value))
                OnPropertyChanged(nameof(HasBackgroundActivity));
        }
    }

    public bool HasBackgroundActivity => _activity is not null;

    internal void After(TimeSpan delay, Action action) => _ui.After(delay, () => _ui.Post(action));

    // ---- Background analysis (SL-54) -------------------------------------------------------

    /// <summary>Peaks, then the click, then the guide, one song at a time at low priority.</summary>
    private async Task AnalyseAfterImportAsync(Song song)
    {
        await ComputePeaksAsync(song);
        await RunJobAsync(song.Id, "Analysing the click", async () =>
        {
            var (result, problem) = await _analysis.AnalyseClickAsync(song);
            _ui.Post(() =>
            {
                if (result is not null)
                    ApplyClick(song.Id, result, automatic: true);
                else if (problem is not null)
                    AppLog.Write("Analysis", $"{song.Title}: {problem}");
            });
        });

        if (AnalysisService.GuideTrack(song) is not null && _analysis.Model.Available)
            await TranscribeGuideAsync(song.Id);
    }

    /// <summary>One background job; progress that arrives after it finished is dropped.</summary>
    private sealed class Job
    {
        public bool Done { get; set; }
    }

    private Task RunJobAsync(string songId, string what, Func<Task> job) => RunJobAsync(songId, what, _ => job());

    private async Task RunJobAsync(string songId, string what, Func<Job, Task> job)
    {
        await _jobs.WaitAsync();
        var state = new Job();
        try
        {
            ShowActivity(songId, what + "…", state);
            await job(state);
        }
        catch (Exception ex)
        {
            // Nothing optional may stop the music or the app (REL-50): log it and say so.
            AppLog.Error("Analysis", ex);
            _ui.Post(() => ShowToast($"{what} failed: {ex.Message}"));
        }
        finally
        {
            _ui.Post(() =>
            {
                state.Done = true;
                SetActivity(songId, null);
            });
            _jobs.Release();
        }
    }

    private void ShowActivity(string songId, string text, Job job) => _ui.Post(() =>
    {
        if (!job.Done)
            SetActivity(songId, text);
    });

    private void SetActivity(string songId, string? text)
    {
        var item = _allSongs.FirstOrDefault(s => s.Id == songId);
        if (item is not null)
            item.Activity = text;
        BackgroundActivity = text is null ? null : $"{item?.Title ?? "Song"}: {char.ToLowerInvariant(text[0])}{text[1..]}";
    }

    /// <summary>Applies the click's grid to the newest copy of the song; automatic runs leave a manual tempo alone.</summary>
    private void ApplyClick(string songId, ClickResult result, bool automatic, bool downbeatOnly = false)
    {
        var applied = false;
        UpdateSong(songId, song =>
        {
            if (automatic && song.TempoSource == TempoSource.Manual)
                return song with { ClickAnalysis = result.Analysis };
            applied = true;
            if (downbeatOnly)
            {
                var moved = song with { FirstDownbeatSec = result.Analysis.FirstDownbeatSec, ClickAnalysis = result.Analysis, UpdatedAt = DateTimeOffset.UtcNow };
                return moved.Sections.Count == 0 ? moved : moved with { Sections = SectionList.Resnap(moved.Sections, moved.Grid) };
            }
            return AnalysisService.ApplyClick(song, result.Analysis);
        });

        AppLog.Write("Analysis", result.LogLine + (applied ? string.Empty : " (not applied: the tempo was set by hand)"));
        if (!automatic)
            ShowToast(downbeatOnly
                ? $"First downbeat at {result.Analysis.FirstDownbeatSec.ToString("0.000", CultureInfo.InvariantCulture)} s"
                : result.LogLine.Split(';')[0]);
    }

    private async Task ReanalyseClickAsync(bool downbeatOnly)
    {
        if (_song is null)
            return;
        var song = _song.Model;
        if (AnalysisService.ClickTrack(song) is null)
        {
            Banner = "This song has no click track. Set BPM, meter and first downbeat here by hand.";
            return;
        }

        await RunJobAsync(song.Id, "Analysing the click", async () =>
        {
            var (result, problem) = await _analysis.AnalyseClickAsync(song);
            _ui.Post(() =>
            {
                if (result is not null)
                    ApplyClick(song.Id, result, automatic: false, downbeatOnly);
                else
                    Banner = problem;
            });
        });
    }

    private async Task RetranscribeAsync()
    {
        if (_song is null)
            return;
        var problem = await TranscribeGuideAsync(_song.Id, force: true);
        if (problem is not null)
            Banner = problem;
        else
            ShowToast("Guide transcribed. Detect from guide uses it now.");
    }

    /// <summary>Transcribes the guide unless a current transcript exists; null or a problem.</summary>
    private Task<string?> TranscribeGuideAsync(string songId, bool force = false)
    {
        lock (_transcribing)
        {
            if (_transcribing.TryGetValue(songId, out var running))
                return running;

            var task = TranscribeCoreAsync(songId, force);
            _transcribing[songId] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_transcribing)
                    _transcribing.Remove(songId);
            }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<string?> TranscribeCoreAsync(string songId, bool force)
    {
        string? problem = null;
        await RunJobAsync(songId, "Listening to the guide", async job =>
        {
            var song = CurrentSong(songId);
            if (song is null || (!force && AnalysisService.TranscriptCurrent(song)))
                return;

            var progress = new Progress<(string Stage, double Fraction)>(p =>
            {
                var text = $"{p.Stage} {Math.Round(p.Fraction * 100).ToString(CultureInfo.InvariantCulture)} %";
                ShowActivity(songId, text, job);
                _ui.Post(() =>
                {
                    if (!job.Done && _song?.Id == songId && Sections.IsBusy)
                        Sections.BusyText = $"Analyzing… {text}";
                });
            });
            var (result, error) = await _analysis.TranscribeAsync(song, progress);
            problem = error;
            if (result is not null)
            {
                _ui.Post(() =>
                {
                    UpdateSong(songId, s => s with { GuideAnalysis = result with { Calls = s.GuideAnalysis?.Calls ?? [] } });
                    AppLog.Write("Analysis", $"{song.Title}: guide transcribed, {result.Words.Count} words in {result.Model}");
                });
            }
            else
            {
                AppLog.Write("Analysis", $"{song.Title}: {error}");
            }
        });
        return problem;
    }

    // ---- Detect from guide (SEC-10…17) -----------------------------------------------------

    public async Task DetectSectionsAsync()
    {
        if (_song is null)
            return;
        var songId = _song.Id;
        if (AnalysisService.GuideTrack(_song.Model) is null)
        {
            Banner = "This song has no guide track, so sections can't be detected. Play it and press M at each section.";
            return;
        }

        // Re-detect when sections were edited asks first (UI-60); edited ones are kept (SEC-17).
        var current = _song.Model.Sections;
        if (current.Any(s => s.Origin != SectionOrigin.Guide || s.Edited)
            && !await _dialogs.ConfirmAsync("Detect sections again?",
                "Sections you marked, added or edited are kept. The other detected sections are replaced.", "Detect again"))
            return;

        if (!AnalysisService.TranscriptCurrent(_song.Model))
        {
            Sections.BusyText = "Analyzing… Listening to the guide";
            string? problem;
            try
            {
                problem = await TranscribeGuideAsync(songId, force: false);
            }
            finally
            {
                Sections.BusyText = null;
            }

            if (_song?.Id != songId)
                return;
            if (problem is not null || !AnalysisService.TranscriptCurrent(_song.Model))
            {
                Sections.ResultText = problem ?? "The guide couldn't be transcribed.";
                return;
            }
        }

        var song = _song.Model;
        var result = _analysis.Detect(song);
        var summary = SectionDetector.Summary(song.Title, result.Sections, song.Grid, result);
        CommitSections(result.Sections, "Detect from guide", toast: null,
            guide: song.GuideAnalysis! with { Calls = result.Calls }, redetect: true);
        Sections.ResultText = summary;
        SelectedTab = SectionsTab;
        AppLog.Write("Sections", $"{song.Title}: {summary}");
    }

    // ---- Section edits with undo and autosave (SEC-50…53) ----------------------------------

    /// <summary>The newest copy of a song: the loaded one, or the one on disk.</summary>
    private Song? CurrentSong(string songId) => _song?.Id == songId ? _song.Model : _store.LoadSong(songId);

    /// <summary>Changes a song on the UI thread, saves it at once and updates the library row.</summary>
    internal void UpdateSong(string songId, Func<Song, Song> change, (string? Id, bool Set) active = default)
    {
        if (CurrentSong(songId) is not { } before)
            return;
        var after = change(before);
        if (ReferenceEquals(after, before) && !active.Set)
            return;

        _store.SaveSong(after);
        var summary = SongSummary.From(after);
        var library = _store.LoadLibrary();
        library.Songs.RemoveAll(s => s.Id == songId);
        library.Songs.Add(summary);
        _store.SaveLibrary(library);
        if (_allSongs.FirstOrDefault(s => s.Id == songId) is { } item)
            item.Summary = summary;

        if (_song?.Id == songId)
        {
            var gridChanged = before.Grid != after.Grid;
            var mixBefore = _song.Mix.Id;
            _song.SetModel(after, active.Id, active.Set);
            if (_song.Mix.Id != mixBefore)
                ApplyMixPreset(); // an undo or a deleted preset changed the preset in use
            RefreshMixPresets();
            if (gridChanged)
                _song.ResetFields();
            Sections.Refresh();
            OnPropertyChanged(nameof(HasSections));
            UpdateReadouts();
        }

        if (_nextEntry?.SongId == songId)
            _ = RefreshNextAsync(); // the preloaded next song changed
    }

    /// <summary>
    /// Replaces the song's sections as one undo step (and stores the guide calls with them). The variants follow
    /// the sections: a merge points the removed section's entries at the one that stays (SEC-54), and a re-detect
    /// maps every entry to the new section with the same label (SEC-17). A deleted section's entries stay and are
    /// flagged "Section removed" until the user chooses a replacement (ARR-50).
    /// </summary>
    internal void CommitSections(List<Section> sections, string description, string? toast = "", GuideAnalysis? guide = null,
        (string Removed, string Kept)? merged = null, bool redetect = false)
    {
        if (_song is null)
            return;
        var songId = _song.Id;
        var before = _song.Model.Sections;
        var beforeGuide = _song.Model.GuideAnalysis;
        var variantsBefore = _song.Model.Arrangements;
        Func<List<Arrangement>, List<Arrangement>>? remap = null;
        if (merged is { } m)
            remap = v => ArrangementEdit.RemapSection(v, m.Removed, m.Kept);
        else if (redetect)
        {
            var oldInfos = _song.Sections;
            var newInfos = SectionList.Describe(sections, _song.Grid, _song.DurationSec);
            remap = v => ArrangementEdit.RemapAfterRedetect(v, oldInfos, newInfos);
        }

        var variantsAfter = remap is not null && variantsBefore.Count > 0 ? remap(variantsBefore) : variantsBefore;

        // Custom arrangements of this song's service entries follow the sections like its variants (SEC-17, SEC-54).
        var servicesAfter = remap is null ? [] : ServicesWithCustomsMapped(songId, remap);
        var servicesBefore = servicesAfter.Select(s => _services.First(x => x.Id == s.Id)).ToList();

        void Apply(List<Section> value, GuideAnalysis? g, List<Arrangement> variants, List<Service> services)
        {
            UpdateSong(songId, s => s with { Sections = value, GuideAnalysis = g ?? s.GuideAnalysis, Arrangements = variants, UpdatedAt = DateTimeOffset.UtcNow });
            foreach (var service in services)
                ApplyService(service);
        }

        Apply(sections, guide, variantsAfter, servicesAfter);
        _undo.Push(new UndoableChange(description, () => Apply(before, beforeGuide, variantsBefore, servicesBefore), () => Apply(sections, guide, variantsAfter, servicesAfter)));
        AppLog.Write("Sections", description);
        if (toast is not null)
            ShowToast(toast.Length == 0 ? description : toast, canUndo: true);
    }

    /// <summary>
    /// M (SEC-52): a section at the playhead on the nearest bar, named from the nearest guide
    /// call. Returns the new section's id, or null when one already starts there (it flashes).
    /// </summary>
    public string? MarkAtPlayhead()
    {
        if (_song is null)
            return null;
        var model = _song.Model;
        var calls = model.GuideAnalysis?.Calls is { Count: > 0 } stored
            ? stored
            : SectionDetector.Calls(model.GuideAnalysis?.Words ?? [], model.Grid, _song.DurationSec, _analysis.MusicSounds(model));

        var result = SectionList.Mark(model.Sections, PositionSec, model.Grid, _song.DurationSec, calls);
        if (result.Existing is { } existing)
        {
            var label = _song.Sections.FirstOrDefault(s => s.Id == existing.Id)?.Label ?? existing.Name;
            Sections.Flash(existing.Id);
            ShowToast($"{label} already starts at chart bar {result.ChartBar.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }

        var added = result.Added!;
        CommitSections(result.Sections, $"Marked {added.Name} at chart bar {result.ChartBar.ToString(CultureInfo.InvariantCulture)}");
        Sections.Select(added.Id);
        return added.Id;
    }

    // ---- Review and call playback (SEC-30, SEC-32) -----------------------------------------

    /// <summary>Plays from 2 bars before to 2 bars after a boundary, then puts the playhead on it (SEC-30).</summary>
    internal Task PlayAroundAsync(double boundary)
    {
        if (_song is null)
            return Task.CompletedTask;
        var bar = _song.Grid.BarLength;
        return PlayRangeAsync(Math.Max(0, boundary - 2 * bar), Math.Min(_song.DurationSec, boundary + 2 * bar), boundary);
    }

    /// <summary>Plays a stretch and stops at its end; then the playhead goes to <paramref name="returnTo"/> (or stays).</summary>
    internal async Task PlayRangeAsync(double from, double to, double? returnTo)
    {
        if (_engine is null || _song is null)
            return;
        if (IsPlaying)
            await _engine.PauseAsync();
        await SeekAsync(from);
        _rangeEnd = to;
        _rangeReturn = returnTo;
        _rangeStarted = false;
        await _engine.PlayAsync();
    }

    internal void CancelRangePlayback()
    {
        _rangeEnd = null;
        _rangeReturn = null;
    }

    private void CheckRangeEnd(TransportState state)
    {
        if (_rangeEnd is not { } end)
            return;
        if (state == TransportState.Playing)
            _rangeStarted = true;
        else if (_rangeStarted)
        {
            CancelRangePlayback(); // the user paused or stopped
            return;
        }

        if (state != TransportState.Playing || PositionSec < end)
            return;

        var back = _rangeReturn;
        CancelRangePlayback();
        _ = StopRangeAsync(back);
    }

    private async Task StopRangeAsync(double? returnTo)
    {
        if (_engine is null)
            return;
        await _engine.PauseAsync();
        if (returnTo is { } t)
            await SeekAsync(t);
    }
}
