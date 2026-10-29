// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Logging;
using RECompose.Core.Planning;
using RECompose.Core.Services;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>The LOG tab (AUTO-50) and the service report (AUTO-60).</summary>
public sealed partial class MainViewModel
{
    private const int MaxLogEntries = 5000;
    private readonly List<LogEntry> _log = [];  // oldest first
    private DateTime _logClearedAt = DateTime.MinValue;
    private string _logChip = LogChips.All;
    private string _logSearch = string.Empty;
    private IReadOnlyList<LogEntry> _shownLog = [];
    private bool _logHooked;
    private RelayCommand? _clearLog, _exportLog, _exportReport;

    // The service as it went: a song per load from a service, the items heard and the log while it was loaded.
    private readonly List<ReportSong> _reportSongs = [];
    private DateTime _reportStarted;
    private string? _reportItemId;

    public IReadOnlyList<string> LogChipChoices => LogChips.Chips;

    public string LogChip
    {
        get => _logChip;
        set
        {
            if (Set(ref _logChip, value ?? LogChips.All))
                FilterLog();
        }
    }

    public string LogSearch
    {
        get => _logSearch;
        set
        {
            if (Set(ref _logSearch, value ?? string.Empty))
                FilterLog();
        }
    }

    /// <summary>Newest first (AUTO-50).</summary>
    public IReadOnlyList<LogEntry> ShownLog { get => _shownLog; private set => Set(ref _shownLog, value); }

    /// <summary>Clear view: empties the tab; the files keep everything.</summary>
    public RelayCommand ClearLogCommand => _clearLog ??= new RelayCommand(() =>
    {
        _logClearedAt = DateTime.Now;
        FilterLog();
    });

    public RelayCommand ExportLogCommand => _exportLog ??= new RelayCommand(ExportLogAsync);

    /// <summary>Service ⋯ › Export report (AUTO-60).</summary>
    public RelayCommand ExportReportCommand => _exportReport ??= new RelayCommand(() => SaveReport(onDemand: true));

    private void HookLog()
    {
        if (_logHooked)
            return;
        _logHooked = true;
        AppLog.Added += OnLogAdded;
    }

    private void UnhookLog()
    {
        AppLog.Added -= OnLogAdded;
        _logHooked = false;
    }

    // The logging thread only queues: it may hold another view model's dispatcher, so it never waits for this one.
    private void OnLogAdded(LogEntry entry)
    {
        _logQueue.Enqueue(entry);
        _ = Task.Run(() => _ui.Post(DrainLog));
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _logQueue = new();

    private void DrainLog()
    {
        var shown = new List<LogEntry>();
        while (_logQueue.TryDequeue(out var entry))
        {
            _log.Add(entry);
            if (_reportSongs.Count > 0)
                _reportSongs[^1].Entries.Add(entry);
            if (LogChips.Shows(entry, _logChip, _logSearch) && entry.Time >= _logClearedAt)
                shown.Add(entry);
        }

        if (_log.Count > MaxLogEntries)
            _log.RemoveRange(0, _log.Count - MaxLogEntries);
        if (shown.Count > 0)
            ShownLog = [.. Enumerable.Reverse(shown), .. _shownLog.Take(Math.Max(0, MaxLogEntries - shown.Count))];
    }

    private void FilterLog() =>
        ShownLog = _log.Where(e => e.Time >= _logClearedAt && LogChips.Shows(e, _logChip, _logSearch)).Reverse().ToList();

    private async Task ExportLogAsync()
    {
        var name = $"RECompose log {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}";
        if (await _dialogs.PickSaveFileAsync("Export log", name + ".txt", ".txt") is not { } path)
            return;
        try
        {
            var csv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(path, LogChips.Export(_shownLog.Reverse(), csv));
            ShowToast($"Exported {_shownLog.Count} log lines.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner = $"The log could not be exported ({ex.Message}).";
        }
    }

    // ---- The service report (AUTO-60) -----------------------------------------------------------------

    /// <summary>A service entry was loaded: the report gets a song with its planned order.</summary>
    private void ReportLoaded()
    {
        if (_song is not { Entry: { } entry } song)
            return;
        if (_reportSongs.Count == 0)
            _reportStarted = DateTime.Now;
        _reportItemId = null;
        _reportSongs.Add(new ReportSong
        {
            Title = song.Title,
            Key = ServiceText.KeyText(song.Model, entry.KeyOffset),
            Tempo = $"{RECompose.Core.Timing.Format.Bpm(song.Model.Bpm * song.Plan.TempoFactor)} BPM",
            Arrangement = EntryArrangement.Resolve(entry, song.Model).Name,
            Started = DateTime.Now,
            Planned = song.Plan.DisplayItems.Where(i => i.Kind == PlanItemKind.Section).Select(i => i.RangeLabel).ToList(),
        });
    }

    /// <summary>While playing: each item the playhead enters is a line of "as played".</summary>
    private void ReportTick()
    {
        if (_reportSongs.Count == 0 || _song is null || !IsPlaying || _reportSongs[^1].Title != _song.Title)
            return;
        var plan = _live?.Committed ?? _song.Plan;
        if (plan.IsEmpty)
            return;
        var item = plan.Items[plan.ItemIndexAt(_planPosition)];
        if (item.Id == _reportItemId || item.Kind is not (PlanItemKind.Section or PlanItemKind.Song))
            return;
        _reportItemId = item.Id;
        _reportSongs[^1].Played.Add((DateTime.Now, item.Kind == PlanItemKind.Song ? "Whole song" : item.RangeLabel));
    }

    /// <summary>Leaving Perform after a song played, or on demand: writes reports/&lt;service&gt; &lt;date&gt;.md.</summary>
    private void SaveReport(bool onDemand)
    {
        var played = _reportSongs.Where(s => s.Played.Count > 0).ToList();
        if (played.Count == 0)
        {
            if (onDemand)
                ShowToast("Nothing has played yet, so there is no report.");
            return;
        }

        var name = _service?.Name ?? "Service";
        try
        {
            var folder = Path.Combine(_store.Root, "reports");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, string.Concat($"{name} {_reportStarted.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}".Split(Path.GetInvalidFileNameChars())) + ".md");
            File.WriteAllText(file, ServiceReport.Build(name, _reportStarted, DateTime.Now, played));
            LastReportPath = file;
            ShowToast($"Service report saved: {Path.GetFileName(file)}");
            AppLog.Write("Services", $"Report saved to {file}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner = $"The service report could not be saved ({ex.Message}).";
        }
    }

    /// <summary>The report written last, for tests and "Show report".</summary>
    public string? LastReportPath { get; private set; }
}
