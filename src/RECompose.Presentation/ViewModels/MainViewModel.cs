// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Audio;
using RECompose.Core.Commands;
using RECompose.Core.Import;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
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
    private double _position;
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
        _ui = ui;
        _dialogs = dialogs;
        _settings = store.LoadSettings();
        Outputs = new AudioSettingsResolver(() => _settings.Audio);

        ImportCommand = new RelayCommand(ImportAsync);
        PlayPauseCommand = new RelayCommand(PlayPauseAsync, () => _song is not null);
        StopCommand = new RelayCommand(StopAsync, () => _song is not null);
        ToStartCommand = new RelayCommand(() => SeekAsync(0), () => _song is not null);
        OpenSettingsCommand = new RelayCommand(OpenAudioSettingsAsync);
        UndoCommand = new RelayCommand(Undo);
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
                Sections.Refresh();
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
            }
        }
    }

    public bool IsPlaying => _state == TransportState.Playing;
    public string PlayLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>The position being heard, in source seconds.</summary>
    public double PositionSec
    {
        get => _position;
        private set
        {
            if (Set(ref _position, value))
                UpdateReadouts();
        }
    }

    /// <summary>"bar 32.2 / 124" (PB-32).</summary>
    public string SongBarText { get => _songBar; private set => Set(ref _songBar, value); }

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

    // ---- Start-up and the engine connection (REL-34) --------------------------------

    public async Task InitializeAsync()
    {
        foreach (var summary in _store.LoadLibrary().Songs.OrderBy(s => s.Title, NaturalComparer.Instance))
            _allSongs.Add(new SongItemViewModel(summary));
        RefreshLibrary();
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
        engine.Disconnected += () => _ui.Post(() => _ = OnEngineLostAsync());
        _engine = engine;

        var (loaded, device, snapshot) = await engine.GetStateAsync();

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
            // The app restarted while the engine kept the song: pick it up as it is.
            BindSong(model, loaded);
            ApplySnapshot(snapshot);
            if (snapshot.State == TransportState.Playing)
                ShowToast($"Reconnected. {model.Title} kept playing.");
        }
        else if (afterCrash && _song is not null)
        {
            var frame = _lastFrame;
            await LoadSongAsync(_song.Id, force: true, startFrame: frame);
            Banner = null;
            ShowToast("The audio engine was restarted. The song is ready at the same position.");
        }
        else if (!session.Attached && _settings.LastSongId is { } last && _allSongs.Any(s => s.Id == last))
        {
            SelectedSong = _allSongs.First(s => s.Id == last);
            await LoadSongAsync(last);
        }
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

        if (ImportService.FindAudioFiles(folder).Count == 0)
        {
            ShowToast($"No audio files in {Path.GetFileName(folder)}.");
            return;
        }

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

    public async Task LoadSongAsync(string id, bool force = false, long startFrame = 0)
    {
        if (!force && _song?.Id == id)
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
            var mix = model.DefaultMix;
            var tracks = model.Tracks.Where(t => !t.Hidden).Select(t =>
            {
                var (channel, width) = _settings.Audio.Resolve(t.Output);
                var m = mix.For(t.Id);
                return new EngineTrack(t.Id, t.File, channel, width, m.GainDb <= TrackViewModel.MinimumDb ? -100 : m.GainDb, m.Mute, m.Solo, t.IsClickOrGuide);
            });
            loaded = await engine.LoadSongAsync(model.Id, tracks, mix.MasterGainDb, startFrame);
        }

        BindSong(model, loaded);
        AppLog.Write("Transport", $"Loaded {model.Title}");
        _settings = _settings with { LastSongId = id };
        _store.SaveSettings(_settings);

        foreach (var missing in _song!.Tracks.Where(t => t.Missing))
            AddToastLine($"File missing: {missing.Name}");
    }

    private void BindSong(Song model, LoadedSong? loaded)
    {
        var vm = new SongViewModel(model, loaded, Outputs);
        foreach (var track in vm.Tracks)
        {
            track.Changed += OnTrackChanged;
            _committed[track.Id] = (track.GainDb, track.Mute, track.Solo);
        }
        vm.Master.Changed += OnTrackChanged;
        _committed["master"] = (vm.Master.GainDb, false, false);

        Song = vm;
        SelectedSong ??= _allSongs.FirstOrDefault(s => s.Id == model.Id);
        PositionSec = 0;
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
        if (IsPlaying)
            await _engine.PauseAsync();
        else
            await _engine.PlayAsync();
    }

    private async Task StopAsync()
    {
        if (_engine is not null && _song is not null)
            await _engine.StopAsync();
    }

    /// <summary>Seeks to a source time (click on the ruler, PB-26).</summary>
    public async Task SeekAsync(double seconds)
    {
        if (_song is null)
            return;
        seconds = Math.Clamp(seconds, 0, _song.DurationSec);
        var frame = (long)Math.Round(seconds * _song.SampleRate);
        if (!IsPlaying)
            PositionSec = seconds; // show it at once; the engine confirms
        if (_engine is not null)
        {
            // Until the engine reports this frame, older snapshots must not move the playhead back.
            _seekFrame = IsPlaying ? null : frame;
            _seekSentAt = DateTime.UtcNow;
            await _engine.SeekAsync(frame);
        }
    }

    /// <summary>← and → when stopped: one bar back or forward (KEY-1).</summary>
    public Task StepBarAsync(int bars)
    {
        if (_song is null || IsPlaying)
            return Task.CompletedTask;
        return SeekAsync(Readouts.StepBars(_song.Plan.SongGrid, PositionSec, bars, _song.DurationSec));
    }

    private void ApplySnapshot(EngineSnapshot s)
    {
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
        if (_seekFrame is { } pending)
        {
            if (s.Frame == pending || s.State == TransportState.Playing || DateTime.UtcNow - _seekSentAt > TimeSpan.FromSeconds(2))
                _seekFrame = null;
        }
        if (_seekFrame is null && s.SampleRate > 0)
            PositionSec = s.HeardFrame / s.SampleRate;
        CheckRangeEnd(s.State);

        Status = s.State == TransportState.Playing
            ? (s.Waiting ? "LOADING" : "PLAYING")
            : (s.Buffered || !s.DeviceOpen ? "READY" : "LOADING");

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
            return;
        }

        var r = _song.Plan.At(PositionSec);
        SongBarText = r.SongBar;
        TimeText = r.Time;
        ChartBarText = r.ChartBar;
        NowLabel = r.NowLabel;
        NowDetail = r.NowDetail;
        NextLabel = r.NextLabel ?? string.Empty;
        NextTag = r.NextTag;
        CountdownText = r.Countdown;
        SectionProgress = r.SectionProgress;
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
        var model = _song.Model;
        var mix = model.DefaultMix with
        {
            MasterGainDb = _song.Master.GainDb,
            Tracks = _song.Tracks.ToDictionary(t => t.Id, t => new TrackMix { GainDb = t.GainDb, Mute = t.Mute, Solo = t.Solo }),
        };
        model = model with
        {
            MixPresets = [mix, .. model.MixPresets.Where(p => p.Id != mix.Id)],
            DefaultMixPresetId = mix.Id,
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
        _ui.After(TimeSpan.FromSeconds(canUndo ? 4 : 3), () => _ui.Post(() =>
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
