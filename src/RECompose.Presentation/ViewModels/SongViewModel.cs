// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>A library row (SL-56).</summary>
public sealed class SongItemViewModel(SongSummary summary) : ObservableObject
{
    private SongSummary _summary = summary;
    private bool _isLoaded;
    private string? _activity;

    /// <summary>"Analysing the click…", "Listening to the guide 40 %" (SL-54), or null.</summary>
    public string? Activity
    {
        get => _activity;
        set
        {
            if (Set(ref _activity, value))
                OnPropertyChanged(nameof(HasActivity));
        }
    }

    public bool HasActivity => _activity is not null;

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

/// <summary>
/// The loaded song: header, lanes and strips, and the SONG tab fields (UI-13, SL-60). Loaded from a service it plays
/// the service entry (<see cref="Entry"/>): its arrangement, key, tempo, count-in and mix preset. Loaded from the
/// library it plays the variant chosen here, at the original key and tempo.
/// </summary>
public sealed class SongViewModel : ObservableObject
{
    private Song _model;
    private ServiceEntry? _entry;
    private LoadedSong? _loaded;
    private int _waveformVersion;
    private string _titleText;
    private string _keyText;
    private string _bpmText;
    private string _meterText;
    private string _downbeatText;
    private List<SectionInfo> _sections = [];
    private SongPlan _plan = null!;
    private string? _activeVariantId;
    private int _sectionsVersion;

    public SongViewModel(Song model, LoadedSong? loaded, AudioSettingsResolver outputs, ServiceEntry? entry = null)
    {
        _model = model;
        _entry = entry;
        _loaded = loaded;
        Master = TrackViewModel.CreateMaster();
        Master.Apply(gainDb: loaded?.MasterGainDb ?? Mix.MasterGainDb);

        foreach (var track in model.Tracks.Where(t => !t.Hidden))
        {
            var vm = new TrackViewModel(track, outputs.Label(track.Output));
            var mix = Mix.For(track.Id);
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

        _activeVariantId = model.ActiveArrangement?.Id;
        foreach (var vm in Tracks)
            vm.ApplyTransposes(Mix.Transposes(vm.Track));
        RefreshSections(notify: false);
        _titleText = model.Title;
        _keyText = Key.TryParse(model.OriginalKey, out var k) ? k.ToDisplayString() : string.Empty;
        _bpmText = Format.Bpm(model.Bpm);
        _meterText = $"{model.BeatsPerBar}/{model.BeatUnit}";
        _downbeatText = model.FirstDownbeatSec.ToString("0.000", CultureInfo.InvariantCulture);
    }

    public Song Model
    {
        get => _model;
        set => SetModel(value, null, setActive: false);
    }

    /// <summary>
    /// Replaces the model and, when <paramref name="setActive"/>, the variant being played in the same step, so the engine
    /// gets one plan rather than two.
    /// </summary>
    public void SetModel(Song value, string? activeVariantId, bool setActive)
    {
        _model = value;
        var activeBefore = _activeVariantId;
        if (setActive)
            _activeVariantId = activeVariantId;
        if (_activeVariantId is not null && _model.Arrangements.All(a => a.Id != _activeVariantId))
            _activeVariantId = null; // the variant was deleted: back to as written
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HeaderLine));
        OnPropertyChanged(nameof(Grid));
        OnPropertyChanged(nameof(DurationSec));
        OnPropertyChanged(nameof(SourceFolder));
        OnPropertyChanged(nameof(ActiveVariantId));
        OnPropertyChanged(nameof(Variants));
        OnPropertyChanged(nameof(ActiveVariant));
        OnPropertyChanged(nameof(Resolved));
        OnPropertyChanged(nameof(Playing));
        OnPropertyChanged(nameof(Mix));
        RefreshSections(sameArrangement: _activeVariantId == activeBefore);
        WaveformVersion++;
    }

    /// <summary>The written sections with labels, lengths and chart bars (DM-20, SEC-41).</summary>
    public IReadOnlyList<SectionInfo> Sections => _sections;

    public bool HasSections => _sections.Count > 0;

    /// <summary>The plan being played: as written, or the active variant (PB-40, PB-30…35).</summary>
    public SongPlan Plan => _plan;

    /// <summary>The service entry this song plays, or null for a library song (DM-41).</summary>
    public ServiceEntry? Entry => _entry;

    public bool IsEntry => _entry is not null;

    /// <summary>What the entry or the library song plays, resolved against the song (DM-41, M4 decision 1).</summary>
    public ResolvedArrangement Resolved =>
        _entry is { } entry ? EntryArrangement.Resolve(entry, _model)
        : ActiveVariant is { } v ? new ResolvedArrangement(v, ArrangementChoiceType.Variant, v.Name)
        : new ResolvedArrangement(null, ArrangementChoiceType.AsWritten, EntryArrangement.AsWrittenName);

    /// <summary>The variant being played; null plays the song as written, or a custom arrangement (ARR-1).</summary>
    public string? ActiveVariantId =>
        _entry is { } entry
            ? entry.Arrangement.Type == ArrangementChoiceType.Variant && _model.Arrangements.Any(a => a.Id == entry.Arrangement.ArrangementId) ? entry.Arrangement.ArrangementId : null
            : _activeVariantId;

    public Arrangement? ActiveVariant => _model.Arrangements.FirstOrDefault(a => a.Id == ActiveVariantId);

    /// <summary>The entry's custom arrangement for this service only (ARR-12), or null.</summary>
    public Arrangement? Custom => _entry?.Arrangement is { Type: ArrangementChoiceType.Custom, Arrangement: { } custom } ? custom : null;

    /// <summary>The arrangement that plays; null plays the song as written.</summary>
    public Arrangement? Playing => Custom ?? ActiveVariant;

    public bool IsCustom => Custom is not null;

    public bool IsAsWritten => Playing is null;

    /// <summary>The mix preset in use: the entry's, else the song's default (DM-13, PB-81).</summary>
    public MixPreset Mix => _model.MixPresets.FirstOrDefault(p => p.Id == _entry?.MixPresetId) ?? _model.DefaultMix;

    /// <summary>
    /// Replaces the service entry (key, tempo, count-in, arrangement or mix changed) and rebuilds the plan. The plan
    /// counts as the same arrangement when the entry plays the same variant or the same custom arrangement, so the
    /// playhead stays on the same entry and pass (ARR-90).
    /// </summary>
    public void SetEntry(ServiceEntry? entry)
    {
        var same = SameArrangement(_entry, entry);
        _entry = entry;
        foreach (var name in new[] { nameof(Entry), nameof(Resolved), nameof(ActiveVariantId), nameof(ActiveVariant), nameof(Custom), nameof(Playing), nameof(IsCustom), nameof(IsAsWritten), nameof(HeaderLine), nameof(Mix) })
            OnPropertyChanged(name);
        RefreshSections(sameArrangement: same);
    }

    private static bool SameArrangement(ServiceEntry? a, ServiceEntry? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        var x = a.Arrangement;
        var y = b.Arrangement;
        // A custom copy of a variant keeps the variant's entry ids ("this service only" while playing, and Revert).
        if (x.Type == ArrangementChoiceType.Variant && y.Arrangement?.CopiedFromId == x.ArrangementId && y.ArrangementId is null)
            return true;
        if (y.Type == ArrangementChoiceType.Variant && x.Arrangement?.CopiedFromId == y.ArrangementId && x.ArrangementId is null)
            return true;
        return x.Type == y.Type && x.Type switch
        {
            ArrangementChoiceType.Variant => x.ArrangementId == y.ArrangementId,
            ArrangementChoiceType.Custom => x.Arrangement?.Id == y.Arrangement?.Id,
            _ => true,
        };
    }

    public IReadOnlyList<Arrangement> Variants => _model.Arrangements;

    /// <summary>The options the plan is built with: the service entry's key, tempo and count-in, or the song's own.</summary>
    public PlanOptions CurrentPlanOptions
    {
        get
        {
            var options = _entry is { } entry ? PlanOptions.For(entry, _model) : PlanOptions.For(_model);
            return _practiceFactor < 1 ? options with { TempoFactor = options.TempoFactor * _practiceFactor } : options;
        }
    }

    private double _practiceFactor = 1;

    /// <summary>Practice tempo (PB-64): the plan plays slower than the entry tempo; the engine re-streams (a short gap is fine in rehearsal).</summary>
    public void SetPracticeTempo(double factor)
    {
        factor = Math.Clamp(factor, 0.5, 1);
        if (Math.Abs(factor - _practiceFactor) < 1e-9)
            return;
        _practiceFactor = factor;
        RefreshSections(sameArrangement: true);
    }

    /// <summary>A live change has landed (a jump, a vamp, a move on): the plan that plays is now this one (PB-47).</summary>
    public void AdoptLivePlan(SongPlan plan)
    {
        _plan = plan;
        OnPropertyChanged(nameof(Plan));
    }

    /// <summary>Raised after the plan was rebuilt: the previous plan and whether it was the same variant.</summary>
    public event Action<SongPlan, bool>? PlanChanged;

    /// <summary>Switches the variant that plays (null = as written). Raises <see cref="PlanChanged"/>.</summary>
    public void SelectVariant(string? id)
    {
        if (id is not null && _model.Arrangements.All(a => a.Id != id))
            id = null;
        if (id == _activeVariantId || _entry is not null)
            return; // a service entry chooses its arrangement through the service (SetEntry)
        _activeVariantId = id;
        OnPropertyChanged(nameof(ActiveVariantId));
        OnPropertyChanged(nameof(ActiveVariant));
        OnPropertyChanged(nameof(IsAsWritten));
        RefreshSections(sameArrangement: false);
    }

    /// <summary>Bumped when the sections lane needs redrawing.</summary>
    public int SectionsVersion { get => _sectionsVersion; private set => Set(ref _sectionsVersion, value); }

    private void RefreshSections(bool notify = true, bool sameArrangement = true)
    {
        var previous = _plan;
        _sections = SectionList.Describe(_model.Sections, _model.Grid, DurationSec);
        _plan = PlanBuilder.Build(_model.Grid, _sections, DurationSec, Playing, _model.Tracks.Where(t => !t.Hidden).ToList(), CurrentPlanOptions);
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(IsAsWritten));
        SectionsVersion++;
        if (notify && previous is not null)
            PlanChanged?.Invoke(previous, sameArrangement);
    }

    public LoadedSong? Loaded
    {
        get => _loaded;
        set
        {
            _loaded = value;
            OnPropertyChanged(nameof(DurationSec));
            OnPropertyChanged(nameof(SampleRate));
            RefreshSections(notify: false); // the engine derives its plan again at the new rate
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

    /// <summary>
    /// UI-13: "A (−1) · 72 BPM · 3/4 · Short" for a service entry, "B♭   72 BPM   3/4" for a library song.
    /// </summary>
    public string HeaderLine
    {
        get
        {
            if (_entry is { } entry)
                return ServiceText.HeaderLine(_model, entry, Resolved);
            var key = Key.TryParse(_model.OriginalKey, out var k) ? k.ToDisplayString() : "Key ?";
            var bpm = Format.Bpm(_model.Bpm) + " BPM" + (_model.TempoAssumed ? " (assumed)" : string.Empty);
            var meter = $"{_model.BeatsPerBar}/{_model.BeatUnit}" + (_model.MeterAssumed ? " (assumed)" : string.Empty);
            return $"{key}   {bpm}   {meter}";
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

    /// <summary>Puts the SONG tab fields back to the model (after an analysis changed it).</summary>
    public void ResetFields()
    {
        TitleText = _model.Title;
        KeyText = Key.TryParse(_model.OriginalKey, out var k) ? k.ToDisplayString() : string.Empty;
        BpmText = Format.Bpm(_model.Bpm);
        MeterText = $"{_model.BeatsPerBar}/{_model.BeatUnit}";
        DownbeatText = _model.FirstDownbeatSec.ToString("0.000", CultureInfo.InvariantCulture);
    }
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
