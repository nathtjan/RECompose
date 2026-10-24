// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>A library row (SL-56).</summary>
public sealed class SongItemViewModel(SongSummary summary) : ObservableObject
{
    private SongSummary _summary = summary;
    private bool _isLoaded;

    public SongSummary Summary
    {
        get => _summary;
        set
        {
            if (Set(ref _summary, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(MetaLine));
            }
        }
    }

    public string Id => _summary.Id;
    public string Title => _summary.Title;
    public string MetaLine => _summary.MetaLine;

    public bool IsLoaded { get => _isLoaded; set => Set(ref _isLoaded, value); }

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        var s = search.Trim();
        return Title.Contains(s, StringComparison.OrdinalIgnoreCase)
            || (_summary.Artist?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
            || MetaLine.Contains(s, StringComparison.OrdinalIgnoreCase)
            || MetaLine.Replace('♭', 'b').Replace('♯', '#').Contains(s, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The loaded song: header, lanes and strips, and the SONG tab fields (UI-13, SL-60).</summary>
public sealed class SongViewModel : ObservableObject
{
    private Song _model;
    private LoadedSong? _loaded;
    private int _waveformVersion;
    private string _titleText;
    private string _keyText;
    private string _bpmText;
    private string _meterText;
    private string _downbeatText;

    public SongViewModel(Song model, LoadedSong? loaded, AudioSettingsResolver outputs)
    {
        _model = model;
        _loaded = loaded;
        Master = TrackViewModel.CreateMaster();
        Master.Apply(gainDb: loaded?.MasterGainDb ?? model.DefaultMix.MasterGainDb);

        foreach (var track in model.Tracks.Where(t => !t.Hidden))
        {
            var vm = new TrackViewModel(track, outputs.Label(track.Output));
            var mix = model.DefaultMix.For(track.Id);
            var engineTrack = loaded?.Tracks.FirstOrDefault(t => t.Id == track.Id);
            vm.Apply(engineTrack?.GainDb ?? mix.GainDb, engineTrack?.Mute ?? mix.Mute, engineTrack?.Solo ?? mix.Solo);
            if (engineTrack is { Missing: true })
            {
                vm.Missing = true;
                vm.Problem = engineTrack.Problem;
            }
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(TrackViewModel.Peaks) or nameof(TrackViewModel.Mute) or nameof(TrackViewModel.Solo))
                    WaveformVersion++;
            };
            Tracks.Add(vm);
        }

        _titleText = model.Title;
        _keyText = Key.TryParse(model.OriginalKey, out var k) ? k.ToDisplayString() : string.Empty;
        _bpmText = Format.Bpm(model.Bpm);
        _meterText = $"{model.BeatsPerBar}/{model.BeatUnit}";
        _downbeatText = model.FirstDownbeatSec.ToString("0.000", CultureInfo.InvariantCulture);
    }

    public Song Model
    {
        get => _model;
        set
        {
            _model = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(HeaderLine));
            OnPropertyChanged(nameof(Grid));
            OnPropertyChanged(nameof(DurationSec));
            OnPropertyChanged(nameof(SourceFolder));
            WaveformVersion++;
        }
    }

    public LoadedSong? Loaded
    {
        get => _loaded;
        set
        {
            _loaded = value;
            OnPropertyChanged(nameof(DurationSec));
            OnPropertyChanged(nameof(SampleRate));
        }
    }

    public string Id => _model.Id;
    public string Title => _model.Title;
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];
    public TrackViewModel Master { get; }
    public TempoGrid Grid => _model.Grid;
    public string SourceFolder => _model.Source.Path;

    /// <summary>The engine's length when loaded, otherwise the longest file.</summary>
    public double DurationSec => _loaded?.DurationSec is > 0 ? _loaded.DurationSec : _model.DurationSec;

    public double SampleRate => _loaded?.SampleRate ?? 48000;

    /// <summary>"B♭  72 BPM  3/4" (UI-13; key offsets arrive with services in M4).</summary>
    public string HeaderLine
    {
        get
        {
            var key = Key.TryParse(_model.OriginalKey, out var k) ? k.ToDisplayString() : "Key ?";
            var bpm = Format.Bpm(_model.Bpm) + " BPM" + (_model.TempoAssumed ? " (assumed)" : string.Empty);
            return $"{key}   {bpm}   {_model.BeatsPerBar}/{_model.BeatUnit}";
        }
    }

    /// <summary>Bumped whenever lanes need redrawing (peaks arrived, mute or solo changed).</summary>
    public int WaveformVersion
    {
        get => _waveformVersion;
        private set => Set(ref _waveformVersion, value);
    }

    public int MissingCount => Tracks.Count(t => t.Missing);

    // SONG tab fields (SL-60 subset).
    public string TitleText { get => _titleText; set => Set(ref _titleText, value); }
    public string KeyText { get => _keyText; set => Set(ref _keyText, value); }
    public string BpmText { get => _bpmText; set => Set(ref _bpmText, value); }
    public string MeterText { get => _meterText; set => Set(ref _meterText, value); }
    public string DownbeatText { get => _downbeatText; set => Set(ref _downbeatText, value); }

    public void RefreshLanes() => WaveformVersion++;
}

/// <summary>Turns a track's output into the label shown in its lane header ("Main", "Cue", "3–4").</summary>
public sealed class AudioSettingsResolver(Func<Core.Settings.AudioSettings> settings)
{
    public string Label(string output)
    {
        var o = OutputRef.Parse(output);
        if (o.Kind == OutputKind.Direct)
            return o.Label;
        var (channel, width) = settings().Resolve(output);
        return $"{o.Label} {OutputRef.Direct(channel, width).Label}";
    }
}
