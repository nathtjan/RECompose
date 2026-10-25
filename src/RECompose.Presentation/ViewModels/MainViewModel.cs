// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Audio;
using RECompose.Core.Import;
using RECompose.Core.Arrangements;
using RECompose.Core.Commands;
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Music;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Settings;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// The Prepare screen (UI-10…16): library, loaded song, transport and readouts, mixer,
/// audio settings, and the engine connection. Everything here runs on the UI thread;
/// engine events are posted to it.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DataStore _store;
    private readonly EngineHostOptions _engineOptions;
    private readonly ImportService _import;
    private readonly AnalysisService _analysis;
    private readonly IUiDispatcher _ui;
    private readonly IDialogs _dialogs;
    private readonly UndoStack _undo = new();
    private readonly List<SongItemViewModel> _allSongs = [];
    private readonly Dictionary<string, (double Gain, bool Mute, bool Solo)> _committed = [];

    private AppSettings _settings;
    private EngineClient? _engine;
    private SongViewModel? _song;
    private SongItemViewModel? _selected;
    private string _search = string.Empty;
    private string _status = "READY";
    private TransportState _state = TransportState.Stopped;
    private double _planPosition;   // plan seconds: what the engine position means (as heard)
    private double _sourcePosition; // source seconds: where the stems are (as written, sections, review)
    private string _songBar = string.Empty;
    private string _time = string.Empty;
    private string _chartBar = string.Empty;
    private string _audioStatus = "Starting the audio engine…";
    private bool _audioOk;
    private string? _banner;
    private string? _toast;
    private int _toastVersion;
    private int _saveVersion;
    private bool _shuttingDown;
    private bool _restarting;
    private long _lastFrame;
    private int _dropouts;
    private DeviceInfo? _device;
    private int _selectedTab;
    private long? _seekFrame;          // a seek the engine hasn't confirmed yet
    private DateTime _seekSentAt;

    public MainViewModel(DataStore store, EngineHostOptions engineOptions, EngineTools tools, IUiDispatcher ui, IDialogs dialogs,
        SpeechModel? speech = null, IAnalysisTools? analysisTools = null)
    {
        _store = store;
        _engineOptions = engineOptions;
        _import = new ImportService(store, tools);
        _analysis = new AnalysisService(store, analysisTools ?? tools, speech ?? new SpeechModel(store.Root));
        Sections = new SectionsViewModel(this);
        Entry = new EntryViewModel(this);
        Setlist = new SetlistViewModel(this);
        Pads = new PadsViewModel(this);
        _ui = ui;
        _dialogs = dialogs;
        _settings = store.LoadSettings();
        Outputs = new AudioSettingsResolver(() => _settings.Audio);

        ImportCommand = new RelayCommand(ImportAsync);
        PlayPauseCommand = new RelayCommand(PlayPauseAsync, () => _song is not null);
        StopCommand = new RelayCommand(StopAsync, () => _song is not null);
        ToStartCommand = new RelayCommand(ToStartAsync, () => _song is not null);
        OpenSettingsCommand = new RelayCommand(OpenAudioSettingsAsync);
        UndoCommand = new RelayCommand(Undo);
        KeyDownCommand = new RelayCommand(() => StepKey(-1, LoadedEntry?.Id), () => LoadedEntry is not null);
        KeyUpCommand = new RelayCommand(() => StepKey(1, LoadedEntry?.Id), () => LoadedEntry is not null);
        TempoDownCommand = new RelayCommand(() => StepTempo(-1, LoadedEntry?.Id), () => LoadedEntry is not null);
        TempoUpCommand = new RelayCommand(() => StepTempo(1, LoadedEntry?.Id), () => LoadedEntry is not null);
        RedoCommand = new RelayCommand(Redo);
        LoadSelectedCommand = new RelayCommand(() => _selected is { } s ? LoadSongAsync(s.Id) : Task.CompletedTask);
        ApplySongEditsCommand = new RelayCommand(ApplySongEdits, () => _song is not null);
        DismissBannerCommand = new RelayCommand(() => Banner = null);
        ReanalyseClickCommand = new RelayCommand(() => ReanalyseClickAsync(downbeatOnly: false), () => _song is not null);
        DetectDownbeatCommand = new RelayCommand(() => ReanalyseClickAsync(downbeatOnly: true), () => _song is not null);
        RetranscribeCommand = new RelayCommand(RetranscribeAsync, () => _song is not null);
        MarkCommand = new RelayCommand(() => MarkAtPlayhead(), () => _song is not null);
        RelayCommand.Failed = ex =>
        {
            AppLog.Error("Command", ex);
            _ui.Post(() => Banner = ex.Message);
        };
    }

    // ---- Bindable state -------------------------------------------------------------

    public ObservableCollection<SongItemViewModel> Songs { get; } = [];
    public AudioSettingsResolver Outputs { get; }

    public SongItemViewModel? SelectedSong { get => _selected; set => Set(ref _selected, value); }

    public string SearchText
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
                RefreshLibrary();
        }
    }

    public bool LibraryEmpty => _allSongs.Count == 0;

    public SongViewModel? Song
    {
        get => _song;
        private set
        {
            if (Set(ref _song, value))
            {
                OnPropertyChanged(nameof(HasSong));
                foreach (var command in new[] { PlayPauseCommand, StopCommand, ToStartCommand, ApplySongEditsCommand, ReanalyseClickCommand, DetectDownbeatCommand, RetranscribeCommand, MarkCommand })
                    command.Refresh();
                RefreshEntryCommands();
                RefreshMixPresets();
                SaveMixAsNewCommand.Refresh();
                RenameMixCommand.Refresh();
                Sections.Refresh();
                Entry.Refresh();
                Setlist.Refresh();
                OnPropertyChanged(nameof(HasSections));
                foreach (var item in _allSongs)
                    item.IsLoaded = value is not null && item.Id == value.Id;
            }
        }
    }

    public bool HasSong => _song is not null;

    /// <summary>READY · PLAYING · LOADING (UI-13; HOLD and TRANSITION arrive with M4/M5).</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(IsLoadingStatus));
        }
    }

    public bool IsLoadingStatus => _status == "LOADING";

    public TransportState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(PlayLabel));
                Setlist.UpdatePlaying();
            }
        }
    }

    public bool IsPlaying => _state == TransportState.Playing;
    public string PlayLabel => _holding ? "GO" : IsPlaying ? "Pause" : "Play";

    /// <summary>The position being heard, in plan seconds: the arrangement's own clock (PB-40).</summary>
    public double PlanPositionSec
    {
        get => _planPosition;
        private set
        {
            if (Set(ref _planPosition, value))
            {
                UpdateSourcePosition();
                OnPropertyChanged(nameof(TimelinePosition));
                UpdateReadouts();
            }
        }
    }

    private bool _timelineAsHeard;

    /// <summary>
    /// The timeline shows the arrangement on its own clock (as heard) rather than the written song (UI-15). It follows the
    /// variant that plays and can be switched by hand.
    /// </summary>
    public bool TimelineAsHeard
    {
        get => _timelineAsHeard;
        set
        {
            if (Set(ref _timelineAsHeard, value))
            {
                OnPropertyChanged(nameof(TimelinePosition));
                OnPropertyChanged(nameof(TimelineModeLabel));
                OnPropertyChanged(nameof(TimelineLaneLabel));
            }
        }
    }

    /// <summary>The playhead on the timeline's clock.</summary>
    public double TimelinePosition => _timelineAsHeard ? _planPosition : _sourcePosition;

    public string TimelineModeLabel => _timelineAsHeard ? "AS HEARD" : "AS WRITTEN";
    public string TimelineLaneLabel => _timelineAsHeard ? "ARRANGEMENT" : "SECTIONS";

    /// <summary>
    /// The source second being heard: where the playhead is on the as-written timeline, and the time that
    /// sections, marks and review work in. Over a click, a pad or a hold it stays at the last source time.
    /// </summary>
    public double PositionSec => _sourcePosition;

    private void UpdateSourcePosition()
    {
        if (_song is null)
        {
            if (Set(ref _sourcePosition, 0.0, nameof(PositionSec)))
                OnPropertyChanged(nameof(TimelinePosition));
            return;
        }

        if (_song.Plan.SourceAt(_planPosition) is { } source)
        {
            if (Set(ref _sourcePosition, source, nameof(PositionSec)))
                OnPropertyChanged(nameof(TimelinePosition));
        }
    }

    /// <summary>"bar 32.2 / 124" (PB-32).</summary>
    public string SongBarText { get => _songBar; private set { if (Set(ref _songBar, value)) OnPropertyChanged(nameof(SongBarValue)); } }

    /// <summary>"1:18 / −3:51" (PB-33).</summary>
    public string TimeText { get => _time; private set => Set(ref _time, value); }

    /// <summary>"chart bar 32.2" (PB-34, Prepare only).</summary>
    public string ChartBarText { get => _chartBar; private set => Set(ref _chartBar, value); }

    /// <summary>The device and driver: "Focusrite USB ASIO · 4 out · 48 kHz".</summary>
    public string AudioStatus { get => _audioStatus; private set => Set(ref _audioStatus, value); }

    public bool AudioOk { get => _audioOk; private set => Set(ref _audioOk, value); }

    /// <summary>A persistent message at the top of the content (UI-70).</summary>
    public string? Banner
    {
        get => _banner;
        set
        {
            if (Set(ref _banner, value))
                OnPropertyChanged(nameof(HasBanner));
        }
    }

    public bool HasBanner => _banner is not null;

    /// <summary>A one-line confirmation at the bottom, gone after 3 s (UI-70).</summary>
    public string? Toast
    {
        get => _toast;
        private set
        {
            if (Set(ref _toast, value))
                OnPropertyChanged(nameof(HasToast));
        }
    }

    public bool HasToast => _toast is not null;

    public bool FollowPlayhead
    {
        get => _settings.FollowPlayhead;
        set
        {
            if (_settings.FollowPlayhead != value)
            {
                _settings = _settings with { FollowPlayhead = value };
                _store.SaveSettings(_settings);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>0 = SECTIONS, 1 = MIXER, 2 = SONG (UI-16 order).</summary>
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    // The tabs under the timeline, in order (UI-30): ENTRY comes first.
    public const int EntryTab = 0;
    public const int SectionsTab = 1;
    public const int MixerTab = 2;
    public const int SongTab = 3;

    private string _nowPass = string.Empty, _nowNote = string.Empty, _nextNote = string.Empty;

    /// <summary>"pass 1 of 2" or "until cued" on the NOW card (ARR-23).</summary>
    public string NowPassText { get => _nowPass; private set => Set(ref _nowPass, value); }

    /// <summary>The note on the section playing, shown on NOW (ARR-37).</summary>
    public string NowNoteText { get => _nowNote; private set => Set(ref _nowNote, value); }

    /// <summary>The note on the next entry, shown on NEXT (ARR-37).</summary>
    public string NextNoteText { get => _nextNote; private set => Set(ref _nextNote, value); }

    /// <summary>The length of the plan the engine reports, in frames: how the app and the engine agree on the arrangement.</summary>
    public long EnginePlanFrames { get => _enginePlanFrames; private set => Set(ref _enginePlanFrames, value); }

    private long _enginePlanFrames;

    /// <summary>The ENTRY tab: variants and the order of the active one (ARR-1…13).</summary>
    public EntryViewModel Entry { get; }

    /// <summary>Plays another variant, or the song as written (null). The playhead stays on the same moment of the song.</summary>
    public void SelectVariant(string? id)
    {
        _song?.SelectVariant(id);
        Entry.Refresh();
    }

    /// <summary>
    /// Replaces the song's variants (and optionally the default and the one being played) as one undo step and
    /// saves at once (ARR-10, DM-90).
    /// </summary>
    /// <remarks>
    /// With a service entry loaded, <paramref name="entryChoice"/> points the entry at what it should now play (a new
    /// variant, say) in the same undo step; without one the entry keeps what it plays.
    /// </remarks>
    internal void CommitVariants(string description, List<Arrangement> variants, string? activeId, bool keepDefault, string? defaultId = null,
        ArrangementChoice? entryChoice = null)
    {
        if (_song is null)
            return;
        var model = _song.Model;
        var afterDefault = keepDefault ? model.DefaultArrangementId : defaultId;
        var songAfter = model with { Arrangements = variants, DefaultArrangementId = afterDefault, UpdatedAt = DateTimeOffset.UtcNow };
        var servicesAfter = entryChoice is not null && LoadedEntry is { } entry && LoadedService is { } service
            ? [ServiceEdit.Update(service, entry.Id, e => e with { Arrangement = entryChoice })]
            : new List<Service>();
        Commit(description, songAfter, servicesAfter, (activeId, true));
        AppLog.Write("Arrangements", description);
        Entry.ApplyPendingSelection();
    }

    public AppSettings Settings => _settings;

    public RelayCommand ImportCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToStartCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand LoadSelectedCommand { get; }
    public RelayCommand ApplySongEditsCommand { get; }
    public RelayCommand DismissBannerCommand { get; }
    public RelayCommand ReanalyseClickCommand { get; }
    public RelayCommand DetectDownbeatCommand { get; }
    public RelayCommand RetranscribeCommand { get; }
    public RelayCommand MarkCommand { get; }

    /// <summary>KEY −/+ and TEMPO −/+ in the song header (UI-13, PB-60): the loaded service entry.</summary>
    public RelayCommand KeyDownCommand { get; }
    public RelayCommand KeyUpCommand { get; }
    public RelayCommand TempoDownCommand { get; }
    public RelayCommand TempoUpCommand { get; }

    /// <summary>True when the loaded song plays a service entry: KEY and TEMPO apply to it.</summary>
    public bool HasLoadedEntry => LoadedEntry is not null;

    /// <summary>"A (−1)" for the loaded entry (UI-13).</summary>
    public string EntryKeyText => _song is { Entry: { } e } song ? ServiceText.KeyText(song.Model, e.KeyOffset) : string.Empty;

    /// <summary>"100%".</summary>
    public string EntryTempoText => _song?.Entry is { } e ? $"{e.TempoPercent.ToString(CultureInfo.InvariantCulture)}%" : string.Empty;

    /// <summary>PB-62: shifts beyond ±5 semitones get the warning style and a tooltip.</summary>
    public bool EntryKeyWarning => _song?.Entry is { KeyOffset: > 5 or < -5 };

    /// <summary>
    /// PB-5, PB-61: the badge shown while the entry plays in another key or tempo through live processing. The
    /// render cache (M6) adds "rendering 43 %" and then removes the badge when rendered stems play.
    /// </summary>
    public string? LiveKeyBadge => _song is { Entry: { } e } song && (e.KeyOffset != 0 || e.TempoPercent != 100 || song.Plan.Items.Any(i => i.Transpose != 0))
        ? "Live key"
        : null;

    public bool HasLiveKeyBadge => LiveKeyBadge is not null;

    private void RefreshEntryCommands()
    {
        foreach (var command in new[] { KeyDownCommand, KeyUpCommand, TempoDownCommand, TempoUpCommand })
            command.Refresh();
        Pads.ShowSongKey(_song is { } loaded ? ServiceText.EntryKey(loaded.Model, loaded.Entry?.KeyOffset ?? 0)?.PitchClass : null);
        foreach (var name in new[] { nameof(HasLoadedEntry), nameof(EntryKeyText), nameof(EntryTempoText), nameof(EntryKeyWarning), nameof(LiveKeyBadge), nameof(HasLiveKeyBadge) })
            OnPropertyChanged(name);
    }

    // ---- Start-up and the engine connection (REL-34) --------------------------------

    public async Task InitializeAsync()
    {
        foreach (var summary in _store.LoadLibrary().Songs.OrderBy(s => s.Title, NaturalComparer.Instance))
            _allSongs.Add(new SongItemViewModel(summary));
        RefreshLibrary();
        LoadServices();
        LoadPads();
        await ConnectEngineAsync(afterCrash: false);
    }

    private async Task ConnectEngineAsync(bool afterCrash)
    {
        EngineSession session;
        try
        {
            session = await EngineHost.ConnectOrStartAsync(_engineOptions);
        }
        catch (EngineUnavailableException ex)
        {
            AppLog.Error("Engine", ex);
            AudioStatus = "No audio engine";
            AudioOk = false;
            Banner = ex.Message;
            return;
        }

        AppLog.Write("Engine", $"{(session.Attached ? "Reattached to" : "Started")} engine {session.DisplayVersion}, process {session.ProcessId}");

        var engine = new EngineClient(session);
        engine.SnapshotReceived += s => _ui.Post(() => ApplySnapshot(s));
        engine.DeviceChanged += d => _ui.Post(() => ShowDevice(d));
        engine.DeviceError += m => _ui.Post(() => Banner = $"The audio device reported a problem: {m}");
        engine.FileMissing += (id, problem) => _ui.Post(() => MarkMissing(id, problem));
        engine.SongReloaded += song => _ui.Post(() => { if (_song is not null && _song.Id == song.SongId) _song.Loaded = song; });
        engine.SongChanged += id => _ui.Post(() => _ = OnSongChangedAsync(id));
        engine.SongEnded += id => _ui.Post(() => _ = OnSongEndedAsync(id));
        ClearNext(); // a new engine has nothing in its next slot
        engine.Disconnected += () => _ui.Post(() => _ = OnEngineLostAsync());
        _engine = engine;

        var (loaded, device, snapshot) = await engine.GetStateAsync();
        if (snapshot.PadKey is null)
            _ = SendPadSetAsync(); // a fresh engine has no pads yet; one that kept sounding is left alone (REL-34)
        else
            ShowEnginePad(snapshot.PadKey);

        if (device is null)
            await OpenDeviceAsync();
        else
            ShowDevice(device);

        try
        {
            await RestoreSongAsync(session, loaded, snapshot, afterCrash);
        }
        catch (Exception ex)
        {
            // A song that can't be opened must not take the app down with it: start empty.
            AppLog.Error("Library", ex);
            Song = null;
            Status = "READY";
            _settings = _settings with { LastSongId = null };
            _store.SaveSettings(_settings);
            Banner = $"The last song couldn't be opened ({ex.Message}). Double-click it in the library to try again.";
        }
    }

    private async Task RestoreSongAsync(EngineSession session, LoadedSong? loaded, EngineSnapshot snapshot, bool afterCrash)
    {
        if (session.Attached && loaded is not null && _store.LoadSong(loaded.SongId) is { } model)
        {
            // The app restarted while the engine kept the song: pick it up as it is, with the entry it was playing.
            var (entry, serviceId) = LastEntry(model.Id);
            BindSong(model, loaded, entry, serviceId);
            ApplySnapshot(snapshot);
            await SendPlanAsync(null); // the engine may hold another variant's plan: send this one, keep the frame
            if (snapshot.State == TransportState.Playing)
                ShowToast($"Reconnected. {model.Title} kept playing.");
        }
        else if (afterCrash && _song is not null)
        {
            var frame = _lastFrame;
            await LoadSongAsync(_song.Id, force: true, startFrame: frame, entry: _song.Entry, serviceId: _loadedServiceId);
            Banner = null;
            ShowToast("The audio engine was restarted. The song is ready at the same position.");
        }
        else if (!session.Attached && _settings.LastSongId is { } last && _allSongs.Any(s => s.Id == last))
        {
            SelectedSong = _allSongs.First(s => s.Id == last);
            var (entry, serviceId) = LastEntry(last);
            if (entry is not null && serviceId == _service?.Id)
                _selectedEntryId = entry.Id;
            await LoadSongAsync(last, entry: entry, serviceId: serviceId);
        }
    }

    /// <summary>The service entry that was loaded last, when it still plays <paramref name="songId"/>.</summary>
    private (ServiceEntry? Entry, string? ServiceId) LastEntry(string songId)
    {
        if (_settings.LastEntryId is not { } id)
            return (null, null);
        foreach (var service in _services)
        {
            if (service.Entry(id) is { } entry && entry.SongId == songId)
                return (entry, service.Id);
        }

        return (null, null);
    }

    private async Task OnEngineLostAsync()
    {
        if (_shuttingDown || _restarting)
            return;
        AppLog.Write("Errors", "Audio engine connection lost; restarting it");
        _restarting = true;
        _engine = null;
        State = TransportState.Stopped;
        AudioOk = false;
        AudioStatus = "Audio engine stopped";
        Banner = "The audio engine stopped. Restarting it.";
        try
        {
            await Task.Delay(300);
            await ConnectEngineAsync(afterCrash: true);
        }
        finally
        {
            _restarting = false;
        }
    }

    private async Task OpenDeviceAsync()
    {
        if (_engine is null)
            return;
        var a = _settings.Audio;
        try
        {
            ShowDevice(await _engine.SetDeviceAsync(a.DeviceType, a.DeviceName, a.SampleRate, a.BufferSize));
            return;
        }
        catch (EngineErrorException) when (a.DeviceType is not null || a.DeviceName is not null)
        {
            // The saved device isn't here; try the system default.
        }
        catch (EngineErrorException ex)
        {
            ShowNoDevice(ex.Message);
            return;
        }

        try
        {
            ShowDevice(await _engine.SetDeviceAsync(null, null, 0, a.BufferSize));
            Banner = $"{a.DeviceName ?? "The chosen audio device"} isn't available. Playing on the default device.";
        }
        catch (EngineErrorException ex)
        {
            ShowNoDevice(ex.Message);
        }
    }

    private void ShowNoDevice(string message)
    {
        AppLog.Write("Audio", $"No device: {message}");
        _device = null;
        AudioOk = false;
        AudioStatus = "No audio device";
        Banner = $"No audio device could be opened ({message}). Choose one in Settings › Audio.";
    }

    private void ShowDevice(DeviceInfo? device)
    {
        var previous = _device;
        _device = device;
        if (device is null)
        {
            AudioOk = false;
            AudioStatus = "No audio device";
            return;
        }

        if (previous?.StatusLabel != device.StatusLabel)
            AppLog.Write("Audio", $"Device {device.StatusLabel}, buffer {device.BufferSize}, latency {device.OutputLatency} frames");
        AudioOk = true;
        AudioStatus = device.StatusLabel + (_dropouts > 0 ? $" · {_dropouts} dropouts" : string.Empty);
    }

    // ---- Library and import (SL-50…56) -------------------------------------------

    private void RefreshLibrary()
    {
        Songs.Clear();
        foreach (var song in _allSongs.Where(s => s.Matches(_search)))
            Songs.Add(song);
        OnPropertyChanged(nameof(LibraryEmpty));
    }

    private async Task ImportAsync()
    {
        if (await _dialogs.PickFolderAsync() is { } folder)
            await ImportFolderAsync(folder);
    }

    /// <summary>Imports a stems folder: scan, review, save, compute peaks (SL-50…55).</summary>
    public async Task ImportFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            ShowToast("Drop a folder of stems to import a song.");
            return;
        }

        var audioFiles = ImportService.FindAudioFiles(folder);
        if (audioFiles.Count == 0)
        {
            ShowToast($"No audio files in {Path.GetFileName(folder)}.");
            return;
        }

        if (TryImportPadSet(folder, audioFiles))
            return; // SL-47: twelve key-named files are a pad set, not a song

        ShowToast($"Reading {Path.GetFileName(folder)}…");
        var scan = await _import.ScanAsync(folder, _allSongs.Select(s => s.Summary));

        if (scan.Duplicate is { } duplicate)
        {
            switch (await _dialogs.AskDuplicateAsync(duplicate.Title))
            {
                case DuplicateChoice.OpenExisting:
                    SelectedSong = _allSongs.FirstOrDefault(s => s.Id == duplicate.Id);
                    await LoadSongAsync(duplicate.Id);
                    return;
                case DuplicateChoice.Cancel:
                    return;
            }
        }

        var review = new ImportReviewViewModel(scan, _device?.OutputChannels.Count ?? 2);
        while (true)
        {
            if (!await _dialogs.ReviewImportAsync(review))
                return;
            if (review.TryGetValues(out var bpm, out var key, out var beats, out var unit))
            {
                var song = _import.CreateSong(scan, review.Title, bpm, key, beats, unit, review.Choices());
                var item = new SongItemViewModel(SongSummary.From(song));
                _allSongs.Add(item);
                _allSongs.Sort((a, b) => NaturalComparer.Instance.Compare(a.Title, b.Title));
                RefreshLibrary();
                SelectedSong = item;
                ShowToast($"Imported {song.Title} · {song.Tracks.Count} tracks");
                AppLog.Write("Library", $"Imported {song.Title} from {folder}: {song.Tracks.Count} tracks");
                _ = AnalyseAfterImportAsync(song);
                return;
            }
        }
    }

    private async Task ComputePeaksAsync(Song song)
    {
        try
        {
            await _import.ComputePeaksAsync(song, trackId => _ui.Post(() => LoadPeaks(song.Id, trackId)));
        }
        catch (Exception ex) when (ex is IOException or EngineUnavailableException or InvalidOperationException)
        {
            _ui.Post(() => ShowToast($"Waveforms for {song.Title} couldn't be computed: {ex.Message}"));
        }
    }

    private void LoadPeaks(string songId, string trackId)
    {
        if (_song?.Id != songId || _song.Tracks.FirstOrDefault(t => t.Id == trackId) is not { } track)
            return;
        var path = _store.PeaksPath(songId, trackId);
        _ = Task.Run(() => PeaksFile.Load(path)).ContinueWith(t => _ui.Post(() => track.Peaks = t.Result), TaskScheduler.Default);
    }

    // ---- Loading (PB-10) ------------------------------------------------------------

    /// <summary>
    /// Loads a song (PB-10): from the library it plays its default variant; with <paramref name="entry"/> it plays that
    /// service entry with its arrangement, key, tempo, count-in and mix preset.
    /// </summary>
    public async Task LoadSongAsync(string id, bool force = false, long startFrame = 0, ServiceEntry? entry = null, string? serviceId = null)
    {
        if (!force && _song?.Id == id && entry is null && _song.Entry is null)
            return;
        if (_store.LoadSong(id) is not { } model)
        {
            ShowToast("This song's file is missing from the library folder.");
            return;
        }

        Status = "LOADING";
        LoadedSong? loaded = null;
        if (_engine is { } engine)
        {
            var mix = model.MixPresets.FirstOrDefault(p => p.Id == entry?.MixPresetId) ?? model.DefaultMix;
            loaded = await engine.LoadSongAsync(model.Id, BuildEngineTracks(model, mix), mix.MasterGainDb, startFrame);
        }

        _awaitingNext = false;
        BindSong(model, loaded, entry, serviceId);
        var resumeSec = startFrame > 0 ? startFrame / (loaded?.SampleRate ?? 48000.0) : 0.0;
        if (resumeSec > 0)
            PlanPositionSec = resumeSec; // after an engine restart the plan clock continues where it was
        await SendPlanAsync(resumeSec);
        AppLog.Write("Transport", entry is null ? $"Loaded {model.Title}" : $"Loaded {model.Title} ({EntryArrangement.Resolve(entry, model).Name}, {ServiceText.KeyText(model, entry.KeyOffset)})");
        _settings = _settings with { LastSongId = id, LastEntryId = entry?.Id };
        _store.SaveSettings(_settings);

        foreach (var missing in _song!.Tracks.Where(t => t.Missing))
            AddToastLine($"File missing: {missing.Name}");
        _ = RefreshNextAsync(); // PB-11: the entry after this one is loaded and ready
    }

    private void BindSong(Song model, LoadedSong? loaded, ServiceEntry? entry = null, string? serviceId = null)
    {
        _loadedServiceId = entry is null ? null : serviceId;
        var vm = new SongViewModel(model, loaded, Outputs, entry);
        foreach (var track in vm.Tracks)
        {
            track.Changed += OnTrackChanged;
            _committed[track.Id] = (track.GainDb, track.Mute, track.Solo);
        }
        vm.Master.Changed += OnTrackChanged;
        _committed["master"] = (vm.Master.GainDb, false, false);

        vm.PlanChanged += OnPlanChanged;
        _live = null;
        TimelineAsHeard = !vm.IsAsWritten;
        Song = vm;
        SelectedSong ??= _allSongs.FirstOrDefault(s => s.Id == model.Id);
        PlanPositionSec = 0;
        UpdateSourcePosition();
        UpdateReadouts();
        if (_engine is null)
            Status = "READY";

        foreach (var track in vm.Tracks)
            LoadPeaks(model.Id, track.Id);
        if (vm.Tracks.Any(t => !File.Exists(_store.PeaksPath(model.Id, t.Id))))
            _ = ComputePeaksAsync(model);
        Sections.ResultText = string.Empty;
        Sections.Refresh();
    }

    private void MarkMissing(string trackId, string problem)
    {
        if (_song?.Tracks.FirstOrDefault(t => t.Id == trackId) is { } track)
        {
            track.Missing = true;
            track.Problem = problem;
        }
    }

    // ---- Transport (PB-20…26) and readouts (PB-30…36) --------------------------------

    private async Task PlayPauseAsync()
    {
        if (_engine is null || _song is null)
            return;
        if (_transition == "waiting" || (_awaitingNext && !IsPlaying))
        {
            // Between two songs Play starts the next one: on the next click downbeat in a pad transition (SL-30).
            var retune = CarryRetuneKey();
            _awaitingNext = false;
            await _engine.StartNextAsync(retune);
            return;
        }

        if (_holding)
            await _engine.GoAsync(); // in a hold the play key is GO (PB-52)
        else if (IsPlaying)
            await _engine.PauseAsync();
        else
            await _engine.PlayAsync();
    }

    private async Task StopAsync()
    {
        if (_engine is not null && _song is not null)
        {
            _awaitingNext = false;
            await _engine.StopAsync();
        }
    }

    /// <summary>Seeks to a source time (click on the as-written ruler, section jumps, PB-26). Does nothing when the arrangement doesn't play that moment.</summary>
    public Task SeekAsync(double sourceSeconds)
    {
        if (_song is null)
            return Task.CompletedTask;
        sourceSeconds = Math.Clamp(sourceSeconds, 0, _song.DurationSec);
        var plan = _song.Plan;
        var planSec = plan.PlanAt(sourceSeconds, _planPosition);
        if (planSec is null && plan.Items.FirstOrDefault(i => i.HasSource) is { } first && sourceSeconds <= first.SrcStart)
            planSec = 0; // before the first section, when the lead-in was dropped
        return planSec is { } t ? SeekPlanAsync(t) : Task.CompletedTask;
    }

    /// <summary>Seeks to a plan time (click on the as-heard ruler, bar steps, PB-26).</summary>
    public async Task SeekPlanAsync(double planSeconds)
    {
        if (_song is null)
            return;
        planSeconds = Math.Clamp(planSeconds, 0, Math.Max(0, _song.Plan.DurationSec));
        var frame = (long)Math.Round(planSeconds * _song.SampleRate);
        if (!IsPlaying)
            PlanPositionSec = planSeconds; // show it at once; the engine confirms
        if (_engine is not null)
        {
            // Until the engine reports this frame, older snapshots must not move the playhead back.
            _seekFrame = IsPlaying ? null : frame;
            _seekSentAt = DateTime.UtcNow;
            await _engine.SeekAsync(frame);
        }
    }

    /// <summary>← and → when stopped: one bar back or forward in the song's own bars (KEY-1).</summary>
    public Task StepBarAsync(int bars)
    {
        if (_song is null || IsPlaying)
            return Task.CompletedTask;
        var plan = _song.Plan;
        return SeekPlanAsync(Readouts.StepBars(plan.SongGrid, _planPosition, bars, plan.DurationSec));
    }

    // ---- The plan on the engine (PB-40) ------------------------------------------------

    private void OnPlanChanged(SongPlan previous, bool sameArrangement)
    {
        if (_song is null)
            return;
        if (!sameArrangement)
            TimelineAsHeard = !_song.IsAsWritten; // the timeline follows the variant that plays
        _live = null; // an edit rebuilds the plan: a pending live change no longer applies
        var position = SongPlan.Remap(previous, _planPosition, _song.Plan, sameArrangement);
        PlanPositionSec = position;
        Sections.Refresh();
        Entry.Refresh();
        RefreshEntryCommands();
        RefreshMixPresets();
        _ = SendPlanAsync(position);
    }

    /// <summary>Sends the current plan to the engine; <paramref name="positionSec"/> null keeps the engine's frame.</summary>
    private async Task SendPlanAsync(double? positionSec, SongPlan? plan = null, double? swapAtSec = null)
    {
        if (_engine is not { } engine || _song is not { } song)
            return;
        try
        {
            if (positionSec is { } position && !IsPlaying)
            {
                _seekFrame = (long)Math.Round(position * song.SampleRate);
                _seekSentAt = DateTime.UtcNow;
            }

            plan ??= song.Plan;
            await engine.SetPlanAsync(plan.Segments(), positionSec, plan.Grid, plan.TempoFactor, holds: plan.Holds(), swapAtSec: swapAtSec);
            await SendTransitionAsync(plan); // the transition point is a time of this plan
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Engine", ex);
        }
    }

    private void ApplySnapshot(EngineSnapshot s)
    {
        ShowEnginePad(s.PadKey); // pads sound whether or not a song is loaded (SL-40)
        if (s.Xruns + s.Starved != _dropouts)
        {
            _dropouts = (int)(s.Xruns + s.Starved);
            ShowDevice(_device);
        }

        if (_song is null || s.SongId != _song.Id)
        {
            State = s.State;
            return;
        }

        State = s.State;
        _lastFrame = s.Frame;
        EnginePlanFrames = s.PlanFrames;
        if (_seekFrame is { } pending)
        {
            if (s.Frame == pending || s.State == TransportState.Playing || DateTime.UtcNow - _seekSentAt > TimeSpan.FromSeconds(2))
                _seekFrame = null;
        }
        var holdChanged = _holding != s.Holding;
        _holding = s.Holding;
        _holdSec = s.HoldSec;
        if (_seekFrame is null && s.SampleRate > 0)
            PlanPositionSec = (s.Holding ? s.Frame : s.HeardFrame) / s.SampleRate; // a hold waits at its frame, not behind it
        if (holdChanged)
            OnHoldChanged();
        TickLive();
        CheckRangeEnd(s.State);

        Status = s.State == TransportState.Playing
            ? (s.Waiting ? "LOADING" : s.Holding ? "HOLD" : "PLAYING")
            : (s.Buffered || !s.DeviceOpen ? "READY" : "LOADING");
        var transition = s.Transition;
        if (transition != _transition)
        {
            _transition = transition;
            UpdateReadouts();
        }

        if (TransitionPending || transition is "segue" or "handing")
            Status = "TRANSITION"; // between two songs (UI-13)

        // Meters fall about 20 dB per second at the 60 Hz playing rate (PB-84).
        const double decay = 0.006;
        foreach (var track in _song.Tracks)
        {
            if (s.Meters.TryGetValue(track.Id, out var peak))
                track.FeedMeter(peak, decay);
        }
        _song.Master.FeedMeter(Math.Max(s.MasterLeft, s.MasterRight), decay);

        if (s.State != TransportState.Playing)
        {
            foreach (var track in _song.Tracks)
                track.ClearMeter();
            _song.Master.ClearMeter();
        }
    }

    private void UpdateReadouts()
    {
        if (_song is null)
        {
            SongBarText = TimeText = ChartBarText = string.Empty;
            NowLabel = NowDetail = NextLabel = NextTag = CountdownText = string.Empty;
            SectionProgress = 0;
            NowPassText = NowNoteText = NextNoteText = string.Empty;
            return;
        }

        var perform = UpdatePerform();
        var r = perform.Now;
        SongBarText = r.SongBar;
        TimeText = r.Time;
        ChartBarText = r.ChartBar;
        NowLabel = r.NowLabel;
        NowDetail = r.NowDetail;
        NextLabel = perform.Next.Label;
        NextTag = perform.Next.Tag;
        CountdownText = perform.Next.Countdown;
        SectionProgress = r.SectionProgress;
        NowPassText = r.NowPass;
        NowNoteText = r.NowNote;
        NextNoteText = perform.Next.Note;

        if (TransitionPending && _nextSong is { } next)
        {
            // LIVE-20 rule 0: between songs the card names the next song.
            NextTag = "NEXT SONG";
            NextLabel = next.Title.ToUpperInvariant();
            CountdownText = "Press Play";
        }
    }

    // ---- Mixer (PB-80…84) with undo and autosave ------------------------------------

    private bool _applyingUndo;

    private void OnTrackChanged(TrackViewModel track, string what)
    {
        if (_song is null)
            return;

        SendTrack(track, what);
        if (_applyingUndo)
            return;

        if (what == "transposes")
        {
            var value = track.Transposes;
            void Toggle(bool v)
            {
                _applyingUndo = true;
                try
                {
                    track.Transposes = v;
                    ScheduleSave();
                }
                finally
                {
                    _applyingUndo = false;
                }
            }

            _undo.Push(new UndoableChange($"{track.Name} {(value ? "follows" : "stays out of")} the key", () => Toggle(!value), () => Toggle(value)));
            ScheduleSave();
            return;
        }

        var before = _committed.TryGetValue(track.Id, out var c) ? c : (0.0, false, false);
        var after = (track.GainDb, track.Mute, track.Solo);
        _committed[track.Id] = after;

        var label = what switch
        {
            "gain" => $"Fader {track.Name} {track.GainText} dB",
            "mute" => track.Mute ? $"Muted {track.Name}" : $"Unmuted {track.Name}",
            _ => track.Solo ? $"Solo {track.Name}" : $"Solo off {track.Name}",
        };

        void Restore((double Gain, bool Mute, bool Solo) v)
        {
            _applyingUndo = true;
            try
            {
                track.Apply(v.Gain, v.Mute, v.Solo);
                _committed[track.Id] = v;
                SendTrack(track, "all");
                ScheduleSave();
            }
            finally
            {
                _applyingUndo = false;
            }
        }

        _undo.Push(new UndoableChange(label, () => Restore(before), () => Restore(after)) { MergeKey = $"{what}:{track.Id}" });
        ScheduleSave();
    }

    private void SendTrack(TrackViewModel track, string what)
    {
        if (_engine is not { } engine)
            return;

        Task request = track.IsMaster
            ? engine.SetMasterAsync(track.EngineGainDb)
            : what switch
            {
                "gain" => engine.SetTrackAsync(track.Id, gainDb: track.EngineGainDb),
                "mute" => engine.SetTrackAsync(track.Id, mute: track.Mute),
                "solo" => engine.SetTrackAsync(track.Id, solo: track.Solo),
                "transposes" => engine.SetTrackAsync(track.Id, transposes: track.Transposes),
                _ => engine.SetTrackAsync(track.Id, gainDb: track.EngineGainDb, mute: track.Mute, solo: track.Solo),
            };
        _ = request.ContinueWith(t => _ui.Post(() => Banner = t.Exception?.InnerException?.Message), TaskContinuationOptions.OnlyOnFaulted);
    }

    private void Undo()
    {
        if (_undo.Undo() is { } change)
            ShowToast($"Undid: {change.Description}");
    }

    private void Redo()
    {
        if (_undo.Redo() is { } change)
            ShowToast($"Redid: {change.Description}");
    }

    /// <summary>Autosave within 500 ms of the last change (DM-90).</summary>
    private void ScheduleSave()
    {
        var version = ++_saveVersion;
        _ui.After(TimeSpan.FromMilliseconds(500), () => _ui.Post(() =>
        {
            if (version == _saveVersion)
                SaveMix();
        }));
    }

    private void SaveMix()
    {
        if (_song is null)
            return;
        // The preset in use takes the fader moves (DM-13): the entry's, or the song's default.
        var model = _song.Model;
        var mix = _song.Mix with
        {
            MasterGainDb = _song.Master.GainDb,
            Tracks = _song.Tracks.ToDictionary(t => t.Id, t => new TrackMix
            {
                GainDb = t.GainDb,
                Mute = t.Mute,
                Solo = t.Solo,
                Transposes = t.Transposes == t.Track.TransposesByDefault ? null : t.Transposes,
            }),
        };
        var presets = model.MixPresets.Any(p => p.Id == mix.Id)
            ? model.MixPresets.Select(p => p.Id == mix.Id ? mix : p).ToList()
            : [mix, .. model.MixPresets];
        model = model with
        {
            MixPresets = presets,
            DefaultMixPresetId = model.DefaultMixPresetId ?? mix.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _song.Model = model;
        _store.SaveSong(model);
    }

    /// <summary>Saves the pending mix at once (on quit).</summary>
    public void Flush()
    {
        _saveVersion++;
        SaveMix();
    }

    // ---- SONG tab (SL-60 subset) ----------------------------------------------------

    private void ApplySongEdits()
    {
        if (_song is null)
            return;

        if (!double.TryParse(_song.BpmText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) || bpm < 20 || bpm > 400)
        {
            Banner = "BPM must be a number between 20 and 400.";
            return;
        }
        if (ImportRules.ParseMeter(_song.MeterText) is not { } meter)
        {
            Banner = "Meter must look like 4/4, 3/4 or 6/8.";
            return;
        }
        string? key = null;
        if (!string.IsNullOrWhiteSpace(_song.KeyText))
        {
            if (!Key.TryParse(_song.KeyText, out var k))
            {
                Banner = $"\"{_song.KeyText}\" isn't a key. Use a name such as B♭, F♯m or E.";
                return;
            }
            key = k.ToString();
        }
        if (!double.TryParse(_song.DownbeatText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var downbeat) || downbeat < 0)
        {
            Banner = "The first downbeat is a time in seconds, such as 0.020.";
            return;
        }

        var old = _song.Model;
        var gridChanged = Math.Abs(old.Bpm - bpm) > 1e-9 || old.BeatsPerBar != meter.BeatsPerBar || Math.Abs(old.FirstDownbeatSec - downbeat) > 1e-9;
        var model = _song.Model with
        {
            TempoSource = gridChanged ? TempoSource.Manual : old.TempoSource,
            MeterAssumed = old.MeterAssumed && !gridChanged,
            Title = string.IsNullOrWhiteSpace(_song.TitleText) ? _song.Model.Title : _song.TitleText.Trim(),
            Bpm = bpm,
            BeatsPerBar = meter.BeatsPerBar,
            BeatUnit = meter.BeatUnit,
            OriginalKey = key,
            FirstDownbeatSec = downbeat,
            TempoAssumed = false,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (gridChanged && model.Sections.Count > 0)
            model = model with { Sections = SectionList.Resnap(model.Sections, model.Grid) };
        _song.Model = model;
        _store.SaveSong(model);
        Sections.Refresh();

        var library = _store.LoadLibrary();
        library.Songs.RemoveAll(s => s.Id == model.Id);
        library.Songs.Add(SongSummary.From(model));
        _store.SaveLibrary(library);
        if (_allSongs.FirstOrDefault(s => s.Id == model.Id) is { } item)
            item.Summary = SongSummary.From(model);
        RefreshLibrary();
        UpdateReadouts();
        Banner = null;
        ShowToast($"Saved {model.Title}");
    }

    // ---- Settings › Audio (SET-10) ----------------------------------------------------

    private async Task OpenAudioSettingsAsync()
    {
        if (_engine is null)
        {
            Banner = "The audio engine isn't running, so audio settings can't be changed.";
            return;
        }

        var (types, current) = await _engine.ListDevicesAsync();
        var vm = new AudioSettingsViewModel(types, current, _settings.Audio);
        if (!await _dialogs.EditAudioSettingsAsync(vm))
            return;

        var previousOutputs = (_settings.Audio.MainChannel, _settings.Audio.MainWidth, _settings.Audio.CueChannel, _settings.Audio.CueWidth);
        _settings = _settings with { Audio = vm.ToSettings(_settings.Audio) };
        _store.SaveSettings(_settings);
        Banner = null;
        await OpenDeviceAsync();

        // Re-route every track when Main or Cue moved (demo step 6).
        var a = _settings.Audio;
        if (_song is not null && previousOutputs != (a.MainChannel, a.MainWidth, a.CueChannel, a.CueWidth) && _engine is { } engine)
        {
            foreach (var track in _song.Tracks)
            {
                await engine.SetTrackAsync(track.Id, output: a.Resolve(track.Track.Output));
                track.OutputLabel = Outputs.Label(track.Track.Output);
            }
            await engine.SetTrackAsync(EngineTrack.ClickTrackId, output: a.Resolve("cue"));
            await engine.SetPadAsync(output: a.ResolvePad());
            ShowToast($"Main on {OutputRef.Direct(a.MainChannel, a.MainWidth).Label}, Cue on {OutputRef.Direct(a.CueChannel, a.CueWidth).Label}");
        }
    }

    // ---- Feedback (UI-70) -----------------------------------------------------------

    /// <summary>True while the toast offers Undo ("Marked Chorus at chart bar 11 · Undo").</summary>
    public bool ToastCanUndo { get => _toastCanUndo; private set => Set(ref _toastCanUndo, value); }

    private bool _toastCanUndo;

    public void ShowToast(string text) => ShowToast(text, canUndo: false);

    public void ShowToast(string text, bool canUndo)
    {
        Toast = text;
        ToastCanUndo = canUndo;
        var version = ++_toastVersion;
        _ui.After(TimeSpan.FromSeconds(canUndo ? (IsServiceLock ? 2 : 4) : 3), () => _ui.Post(() =>
        {
            if (version == _toastVersion)
            {
                Toast = null;
                ToastCanUndo = false;
            }
        }));
    }

    private void AddToastLine(string text) => ShowToast(_toast is null ? text : $"{_toast} · {text}");

    // ---- Shutdown -------------------------------------------------------------------

    /// <summary>On a normal quit the engine stops too. After a crash it keeps playing (REL-34).</summary>
    public async ValueTask DisposeAsync()
    {
        Flush();
        await ShutdownEngineAsync();
    }

    /// <summary>Stops the engine process. Touches no bindable state, so any thread may call it.</summary>
    public async Task ShutdownEngineAsync()
    {
        _shuttingDown = true;
        if (_engine is { } engine)
        {
            try
            {
                await engine.Session.ShutdownAsync();
            }
            catch (Exception ex) when (ex is EngineDisconnectedException or IOException or ObjectDisposedException)
            {
            }
        }
    }
}
