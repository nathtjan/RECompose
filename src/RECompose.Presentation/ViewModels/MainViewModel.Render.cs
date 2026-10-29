// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Rendering;
using RECompose.Core.Services;
using RECompose.Engine;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// The render cache (REL-10…16, PB-5): every service entry in another key or tempo gets its stems rendered by the engine's
/// <c>render</c> tool, one job at a time in a low-priority process (PB-92, REL-50), into <c>cache/render</c>. A song loads
/// with the renders the cache holds for its stems; the engine plays the one matching each segment's key and the plan's
/// tempo and processes live where there is none (REL-14). When the loaded entry's renders finish while it is stopped it
/// loads again with them; one that finishes during the song applies the next time the entry loads.
/// </summary>
public sealed partial class MainViewModel
{
    private EngineTools? _renderTool;
    private RenderIndex? _renderIndex;
    private readonly List<RenderJob> _renderQueue = [];
    private RenderJob? _rendering;
    private double _renderProgress;
    private bool _renderRunning;
    private bool _renderStopped;
    private CancellationTokenSource _renderCancel = new();
    private HashSet<RenderRecord> _loadedRenders = [];

    /// <summary>Renders start by themselves after a service changes (tests that do not want them switch this off).</summary>
    internal bool AutoRender { get; set; } = true;

    private RenderIndex Renders => _renderIndex ??= LoadRenderIndexSafe();

    private RenderIndex LoadRenderIndexSafe()
    {
        try
        {
            var index = _store.LoadRenderIndex();
            // A render deleted behind our back is a miss, not an error.
            return index with { Renders = [.. index.Renders.Where(r => File.Exists(Path.Combine(_store.RenderFolder, r.File)))] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Error("Render", ex);
            return new RenderIndex();
        }
    }

    private void SaveRenders(RenderIndex index)
    {
        _renderIndex = index;
        try
        {
            _store.SaveRenderIndex(index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Render", ex);
        }
    }

    private static string? Fingerprint(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? RenderKeys.Fingerprint(file, info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static MixPreset MixFor(ServiceEntry entry, Song song) =>
        song.MixPresets.FirstOrDefault(p => p.Id == entry.MixPresetId) ?? song.DefaultMix;

    private static List<RenderJob> JobsFor(ServiceEntry entry, Song song)
    {
        var mix = MixFor(entry, song);
        return RenderPlanner.Jobs(song, EntryPlan.Build(entry, song), mix.Transposes, Fingerprint);
    }

    private RenderNeed RenderNeedFor(ServiceEntry entry, Song song) => RenderPlanner.Need(JobsFor(entry, song), Renders);

    /// <summary>The renders the cache holds for a stem, any key and tempo: the engine picks the one each segment needs.</summary>
    private List<EngineRender> RendersFor(Track track)
    {
        if (Fingerprint(track.File) is not { } print)
            return [];
        return [.. Renders.Renders.Where(r => r.Fingerprint == print)
            .Select(r => new EngineRender(Path.Combine(_store.RenderFolder, r.File), r.Semitones, r.Tempo))];
    }

    /// <summary>Called when the engine loads a song with renders: they count as used for eviction (REL-15).</summary>
    private void NoteRendersLoaded(Song model)
    {
        var prints = model.Tracks.Select(t => Fingerprint(t.File)).OfType<string>().ToHashSet();
        var used = Renders.Renders.Where(r => prints.Contains(r.Fingerprint)).ToList();
        _loadedRenders = [.. used];
        if (used.Count > 0)
            SaveRenders(Renders.Touch(used, DateTimeOffset.UtcNow));
        RefreshRenderBadge();
    }

    /// <summary>The jobs of every entry of the services from today on, soonest first (REL-11).</summary>
    private List<RenderJob> WantedJobs(Service? first = null)
    {
        var today = DateTime.Now.Date;
        var services = _services.Where(s => s.Date is null || s.Date.Value.Date >= today).OrderBy(s => s.Date ?? DateTime.MaxValue).ToList();
        if (first is not null)
            services = [first, .. services.Where(s => s.Id != first.Id)];

        var jobs = new List<RenderJob>();
        var songs = new Dictionary<string, Song?>();
        foreach (var service in services)
        {
            foreach (var entry in service.Entries)
            {
                if (!songs.TryGetValue(entry.SongId, out var song))
                    songs[entry.SongId] = song = _store.LoadSong(entry.SongId);
                if (song is null)
                    continue;
                foreach (var job in JobsFor(entry, song))
                {
                    if (Renders.Find(job.Fingerprint, job.Semitones, job.Tempo) is null && !Renders.HasFailed(job.Fingerprint, job.Semitones, job.Tempo)
                        && !jobs.Any(j => j.FileName == job.FileName))
                        jobs.Add(job);
                }
            }
        }

        return jobs;
    }

    /// <summary>Brings the queue up to date with the services (REL-11). Called after a service changes and at start.</summary>
    internal void QueueRenders(Service? first = null)
    {
        if (!AutoRender || _renderStopped || _shuttingDown)
            return;
        var wanted = WantedJobs(first);
        _renderQueue.Clear();
        _renderQueue.AddRange(wanted.Where(j => _rendering?.FileName != j.FileName));
        RefreshRenderBadge();
        if (!_renderRunning && _renderQueue.Count > 0)
            _ = RunRendersAsync();
    }

    private int _renderSchedule;

    /// <summary>After an edit: once the edits stop for a moment, so a run of key presses is one scan.</summary>
    private void ScheduleRenders(Service service)
    {
        if (!AutoRender)
            return;
        var ticket = ++_renderSchedule;
        After(TimeSpan.FromSeconds(1.5), () =>
        {
            if (ticket == _renderSchedule)
                QueueRenders(_services.FirstOrDefault(s => s.Id == service.Id));
        });
    }

    /// <summary>The check's Render now (REL-1 item 4): failed renders of this service are tried again.</summary>
    private void RenderServiceNow()
    {
        if (_service is not { } service)
            return;
        var prints = WantedFor(service).Select(j => j.Fingerprint).ToHashSet();
        SaveRenders(Renders with { Failures = [.. Renders.Failures.Where(f => !prints.Contains(f.Fingerprint))] });
        _renderStopped = false;
        var auto = AutoRender;
        AutoRender = true;
        QueueRenders(service);
        AutoRender = auto;
        ShowToast(_renderQueue.Count + (_rendering is null ? 0 : 1) is var n && n > 0 ? $"Rendering {n} stem{(n == 1 ? string.Empty : "s")}." : "Every render is ready.");
    }

    private List<RenderJob> WantedFor(Service service)
    {
        var jobs = new List<RenderJob>();
        foreach (var entry in service.Entries)
            if (_store.LoadSong(entry.SongId) is { } song)
                jobs.AddRange(JobsFor(entry, song));
        return jobs;
    }

    private async Task RunRendersAsync()
    {
        _renderRunning = true;
        var ct = _renderCancel.Token;
        try
        {
            while (_renderQueue.Count > 0 && !ct.IsCancellationRequested)
            {
                var job = _renderQueue[0];
                _renderQueue.RemoveAt(0);
                _rendering = job;
                _renderProgress = 0;
                ShowRenderActivity();
                await RenderOneAsync(job, ct);
                _rendering = null;
            }
        }
        finally
        {
            _rendering = null;
            _renderRunning = false;
            if (BackgroundActivity?.StartsWith("Rendering", StringComparison.Ordinal) == true)
                BackgroundActivity = null;
            RefreshRenderBadge();
        }
    }

    private async Task RenderOneAsync(RenderJob job, CancellationToken ct)
    {
        var target = Path.Combine(_store.RenderFolder, job.FileName);
        var progress = new Progress<double>(p => _ui.Post(() =>
        {
            _renderProgress = p;
            ShowRenderActivity();
        }));

        RenderResult result;
        try
        {
            result = await (_renderTool ??= new EngineTools(_engineOptions.ExecutablePath)).RenderAsync(job.SourceFile, target, job.Semitones, job.Tempo, progress, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (EngineUnavailableException ex)
        {
            AppLog.Error("Render", ex);
            _renderStopped = true; // no engine here: live processing plays everything (REL-14)
            _renderQueue.Clear();
            return;
        }

        if (result.Ok && File.Exists(target))
        {
            AppLog.Write("Render", $"Rendered {job.SongTitle} {job.TrackId} {job.Semitones:+0;-0;0} at {job.Tempo:0.###}");
            var now = DateTimeOffset.UtcNow;
            var index = Renders.With(new RenderRecord
            {
                Fingerprint = job.Fingerprint,
                Semitones = job.Semitones,
                Tempo = job.Tempo,
                File = job.FileName,
                SourceFile = job.SourceFile,
                Bytes = new FileInfo(target).Length,
                CreatedAt = now,
                LastUsedAt = now,
            });
            SaveRenders(EvictToBudget(index));
        }
        else
        {
            AppLog.Write("Render", $"Render failed: {job.SongTitle} {job.TrackId}: {result.Problem}");
            SaveRenders(Renders.WithFailure(new RenderFailure(job.Fingerprint, job.Semitones, job.Tempo, result.Problem ?? "Render failed")));
        }

        RefreshRenderBadge();
        _openCheckViewModel?.Run();
        await ApplyFinishedRendersAsync();
    }

    private async Task ApplyFinishedRendersAsync()
    {
        if (_song is not { Entry: { } entry } song || State != TransportState.Stopped || _engine is null || _loadedServiceId is not { } serviceId)
            return;
        var jobs = JobsFor(entry, song.Model);
        if (jobs.Count == 0 || jobs.Any(j => Renders.Find(j.Fingerprint, j.Semitones, j.Tempo) is null)
            || jobs.All(j => _loadedRenders.Any(r => r.Matches(j.Fingerprint, j.Semitones, j.Tempo))))
            return;
        AppLog.Write("Render", $"{song.Title}: renders ready, loading them");
        await LoadSongAsync(song.Id, force: true, entry: entry, serviceId: serviceId);
    }

    /// <summary>LRU under the budget of Settings › Library &amp; cache, keeping what the next seven days need (REL-15).</summary>
    private RenderIndex EvictToBudget(RenderIndex index, long? budgetBytes = null)
    {
        var budget = budgetBytes ?? (long)Math.Max(1, _settings.RenderBudgetGb) * 1024 * 1024 * 1024;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in _services.Where(s => RenderPlanner.IsProtected(s, DateTime.Now)))
            foreach (var job in WantedFor(service))
                keep.Add(job.FileName);

        var (kept, evicted) = index.Evict(budget, keep);
        foreach (var r in evicted)
        {
            try
            {
                File.Delete(Path.Combine(_store.RenderFolder, r.File));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Render", ex);
            }
        }

        if (evicted.Count > 0)
            AppLog.Write("Render", $"Removed {evicted.Count} old renders ({evicted.Sum(r => r.Bytes) / (1024 * 1024)} MB)");
        return kept;
    }

    /// <summary>The check's Clean cache (REL-1 item 11): everything the next seven days do not need goes.</summary>
    private void CleanRenderCache()
    {
        var before = Renders.Bytes;
        SaveRenders(EvictToBudget(Renders, 0));
        ShowToast($"Freed {(before - Renders.Bytes) / (1024 * 1024)} MB of renders.");
    }

    /// <summary>Settings › Library &amp; cache (SET-60): the cache's size.</summary>
    public string RenderCacheText => $"{Renders.Renders.Count} renders · {Renders.Bytes / (1024.0 * 1024 * 1024):0.0} GB of {_settings.RenderBudgetGb} GB";

    private void ShowRenderActivity()
    {
        if (_rendering is not { } job)
            return;
        var track = _store.LoadSong(job.SongId)?.Tracks.FirstOrDefault(t => t.Id == job.TrackId)?.Name ?? job.TrackId;
        var waiting = _renderQueue.Count;
        BackgroundActivity = $"Rendering {job.SongTitle} · {track} · {_renderProgress * 100:0}%" + (waiting > 0 ? $" · {waiting} more" : string.Empty);
        RefreshRenderBadge();
    }

    /// <summary>
    /// PB-61: the loaded entry plays live when a render it needs was not loaded with it. "Live key — rendering 43 %" while
    /// one of its stems is rendering.
    /// </summary>
    private string? RenderBadge()
    {
        if (_song is not { Entry: { } entry } song)
            return null;

        // What the plan playing now needs (live lifts and key changes included), stem by stem.
        var mix = MixFor(entry, song.Model);
        var live = false;
        foreach (var track in song.Model.Tracks.Where(t => !t.Hidden))
        {
            var variants = RenderPlanner.Variants(song.Plan, mix.Transposes(track));
            if (variants.Count == 0)
                continue;
            var print = Fingerprint(track.File);
            if (print is null || variants.Any(v => !_loadedRenders.Any(r => r.Matches(print, v.Semitones, v.Tempo))))
                live = true;
        }

        if (!live)
            return null;
        if (_rendering is { } now && now.SongId == song.Id)
            return $"Live key — rendering {_renderProgress * 100:0}%";
        return "Live key";
    }

    private void RefreshRenderBadge()
    {
        OnPropertyChanged(nameof(LiveKeyBadge));
        OnPropertyChanged(nameof(HasLiveKeyBadge));
        OnPropertyChanged(nameof(RenderCacheText));
    }

    private void StopRenders()
    {
        _renderCancel.Cancel();
        _renderQueue.Clear();
    }

    /// <summary>Tests: waits until the queue is empty.</summary>
    internal async Task WaitForRendersAsync(int timeoutMs = 60000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while ((_renderRunning || _renderQueue.Count > 0) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}
