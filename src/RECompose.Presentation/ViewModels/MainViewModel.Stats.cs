// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Logging;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>The bottom bar in Prepare: the latest log line and the engine's live statistics.</summary>
public sealed partial class MainViewModel
{
    public const int LogTab = 3;

    private long _xruns, _starved, _xrunBase, _starvedBase;
    private string _statsCpu = "CPU –", _statsLatency = "Latency –";
    private LogEntry? _lastLog;
    private RelayCommand? _resetStats, _openLog;

    public string StatsCpu { get => _statsCpu; private set => Set(ref _statsCpu, value); }
    public string StatsLatency { get => _statsLatency; private set => Set(ref _statsLatency, value); }

    /// <summary>Times the device asked for audio before it was ready, since the engine started or the last reset.</summary>
    public long StatsXruns => _xruns - _xrunBase;

    /// <summary>Blocks played as silence because a stem was not read from disk in time, since the engine started or the last reset.</summary>
    public long StatsDisk => _starved - _starvedBase;

    public bool XrunsWarn => StatsXruns > 0;
    public bool DiskWarn => StatsDisk > 0;

    public string LastLogChip => _lastLog?.Chip.ToUpperInvariant() ?? string.Empty;
    public string LastLogText => _lastLog?.Text ?? string.Empty;

    /// <summary>Zeroes the xrun and disk counts, e.g. before a service.</summary>
    public RelayCommand ResetStatsCommand => _resetStats ??= new RelayCommand(() =>
    {
        (_xrunBase, _starvedBase) = (_xruns, _starved);
        RefreshCounts();
    });

    public RelayCommand OpenLogCommand => _openLog ??= new RelayCommand(() => SelectedTab = LogTab);

    internal void ApplyStats(EngineSnapshot s)
    {
        StatsCpu = $"CPU {Math.Round(s.Cpu * 100).ToString("0", CultureInfo.InvariantCulture)} %";
        StatsLatency = s.SampleRate > 0 ? $"Latency {Math.Round(s.Latency * 1000 / s.SampleRate).ToString("0", CultureInfo.InvariantCulture)} ms" : "Latency –";

        // A new device or engine counts from zero again; so does the reset point.
        if (s.Xruns < _xruns) _xrunBase = 0;
        if (s.Starved < _starved) _starvedBase = 0;
        if ((s.Xruns, s.Starved) == (_xruns, _starved))
            return;
        (_xruns, _starved) = (s.Xruns, s.Starved);
        RefreshCounts();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(StatsXruns));
        OnPropertyChanged(nameof(StatsDisk));
        OnPropertyChanged(nameof(XrunsWarn));
        OnPropertyChanged(nameof(DiskWarn));
    }

    private void ShowLastLog(LogEntry entry)
    {
        _lastLog = entry;
        OnPropertyChanged(nameof(LastLogChip));
        OnPropertyChanged(nameof(LastLogText));
    }
}
