// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Settings;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>One of the twelve pad keys (UI-12): its name, whether it sounds and whether it is the song's key.</summary>
public sealed class PadKeyViewModel(int pitchClass) : ObservableObject
{
    private bool _isPlaying;
    private bool _isSongKey;

    public int PitchClass { get; } = pitchClass;

    /// <summary>"C", "D♭" … "B" with real accidentals.</summary>
    public string Label { get; } = new Key(pitchClass).ToDisplayString();

    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    /// <summary>The marker for the loaded song's key (UI-12).</summary>
    public bool IsSongKey { get => _isSongKey; set => Set(ref _isSongKey, value); }

    /// <summary>"D♭, playing, the song's key".</summary>
    public string AccessibleName => string.Join(", ", new[] { Label, IsPlaying ? "playing" : null, IsSongKey ? "the song's key" : null }.OfType<string>());
}

public sealed record PadFadeChoice(double Seconds)
{
    public static readonly IReadOnlyList<PadFadeChoice> All = [new(1), new(2), new(4), new(8)];

    public string Name => $"{Seconds.ToString(CultureInfo.InvariantCulture)} s";

    public override string ToString() => Name;
}

/// <summary>
/// The compact pads of the sidebar (UI-12, SL-40…46): the set, the twelve keys, play/stop, Follow song, volume
/// and fade time. Pads play on their own, so nothing here depends on the song transport.
/// </summary>
public sealed class PadsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading;
    private int? _playing;
    private int? _lastKey;
    private int? _songKey;

    internal PadsViewModel(MainViewModel main)
    {
        _main = main;
        Keys = new ObservableCollection<PadKeyViewModel>(Enumerable.Range(0, 12).Select(pc => new PadKeyViewModel(pc)));
        KeyCommand = new RelayCommand(o => o is int pc ? PressAsync(pc) : Task.CompletedTask, () => HasSet);
        PlayStopCommand = new RelayCommand(PlayStopAsync, () => HasSet);
    }

    public ObservableCollection<PadKeyViewModel> Keys { get; } = [];

    /// <summary>C D♭ D E♭ E F, then F♯ G A♭ A B♭ B: the two rows of six.</summary>
    public IEnumerable<PadKeyViewModel> FirstRow => Keys.Take(6);
    public IEnumerable<PadKeyViewModel> SecondRow => Keys.Skip(6);

    public ObservableCollection<PadSetItem> Sets => _main.PadSets;

    public PadSetItem? CurrentSet
    {
        get => _main.PadSets.FirstOrDefault(s => s.Id == _main.CurrentPadSetId);
        set
        {
            if (!_loading && value is not null && value.Id != _main.CurrentPadSetId)
                _ = _main.SelectPadSetAsync(value.Id);
        }
    }

    public bool HasSet => _main.PadSets.Count > 0;

    public RelayCommand KeyCommand { get; }
    public RelayCommand PlayStopCommand { get; }

    public bool IsPlaying => _playing is not null;
    public string PlayStopLabel => IsPlaying ? "Stop" : "Play";

    /// <summary>The pad follows the loaded entry's key, key lifts included (SL-44).</summary>
    public bool Follow
    {
        get => Settings.Follow;
        set
        {
            if (_loading || value == Settings.Follow)
                return;
            _main.SavePadSettings(Settings with { Follow = value });
            _ = _main.SendToEngineAsync(e => e.SetPadAsync(follow: value));
            if (value && _playing is not null && _songKey is { } key && key != _playing)
                _ = PlayAsync(key);
            OnPropertyChanged();
        }
    }

    /// <summary>Pad volume in dB, −40 to 0 (SL-45, default −6).</summary>
    public double VolumeDb
    {
        get => Settings.GainDb;
        set
        {
            var db = Math.Clamp(Math.Round(value, 1), -40, 0);
            if (_loading || Math.Abs(db - Settings.GainDb) < 1e-9)
                return;
            _main.SavePadSettings(Settings with { GainDb = db });
            _ = _main.SendToEngineAsync(e => e.SetPadAsync(gainDb: db <= -40 ? -100 : db));
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
        }
    }

    public string VolumeText => Settings.GainDb <= -40 ? "−∞" : Settings.GainDb.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture);

    public IReadOnlyList<PadFadeChoice> FadeChoices => PadFadeChoice.All;

    public PadFadeChoice Fade
    {
        get => PadFadeChoice.All.FirstOrDefault(c => Math.Abs(c.Seconds - Settings.FadeSec) < 1e-9) ?? PadFadeChoice.All[2];
        set
        {
            if (_loading || value is null || Math.Abs(value.Seconds - Settings.FadeSec) < 1e-9)
                return;
            _main.SavePadSettings(Settings with { FadeSec = value.Seconds });
            _ = _main.SendToEngineAsync(e => e.SetPadAsync(fadeSec: value.Seconds));
            OnPropertyChanged();
        }
    }

    private PadSettings Settings => _main.Settings.Pads ?? new PadSettings();

    // ---- Keys (SL-42, SL-43) -------------------------------------------------------------------------

    /// <summary>A key: stopped → start it; another key sounds → crossfade; the sounding key → fade out (SL-42).</summary>
    public Task PressAsync(int pitchClass) => _playing == pitchClass ? StopAsync() : PlayAsync(pitchClass);

    /// <summary>Play/stop (SL-43): the last key, or the song's key when Follow song is on.</summary>
    private Task PlayStopAsync()
    {
        if (IsPlaying)
            return StopAsync();
        var key = Follow && _songKey is { } song ? song : _lastKey ?? _songKey ?? 0;
        return PlayAsync(key);
    }

    private Task PlayAsync(int pitchClass)
    {
        _lastKey = pitchClass;
        ShowPlaying(pitchClass);
        return _main.SendToEngineAsync(e => e.PlayPadAsync(pitchClass));
    }

    private Task StopAsync()
    {
        ShowPlaying(null);
        return _main.SendToEngineAsync(e => e.PlayPadAsync(null));
    }

    // ---- From the engine and the loaded song -------------------------------------------------------------

    /// <summary>The key the engine says sounds (Follow song and pad bars change it there).</summary>
    internal void ShowPlaying(int? pitchClass)
    {
        if (_playing == pitchClass)
            return;
        _playing = pitchClass;
        foreach (var key in Keys)
            key.IsPlaying = key.PitchClass == pitchClass;
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayStopLabel));
    }

    /// <summary>The loaded entry's key changed: mark it, and a sounding pad follows it when Follow song is on (SL-44).</summary>
    internal void ShowSongKey(int? pitchClass)
    {
        if (_songKey == pitchClass)
            return;
        _songKey = pitchClass;
        foreach (var key in Keys)
            key.IsSongKey = key.PitchClass == pitchClass;
        if (Follow && _playing is not null && pitchClass is { } pc && pc != _playing)
            _ = PlayAsync(pc);
    }

    internal void Refresh()
    {
        _loading = true;
        try
        {
            foreach (var name in new[]
                     {
                         nameof(Sets), nameof(CurrentSet), nameof(HasSet), nameof(Follow), nameof(VolumeDb), nameof(VolumeText), nameof(Fade),
                     })
                OnPropertyChanged(name);
        }
        finally
        {
            _loading = false;
        }

        KeyCommand.Refresh();
        PlayStopCommand.Refresh();
    }
}
