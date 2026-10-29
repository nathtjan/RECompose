// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Audio and MIDI devices as the reliability features see them (REL-20…24, REL-50): the fallback device, the silent
/// clock, switching back, and the MIDI inputs that come and go. The engine does the work (protocol 10); this shows it
/// and decides when to go back to the main device.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _fallbackPresent;
    private IReadOnlyList<string> _midiInputs = [];
    private IReadOnlyList<string> _midiMissing = [];
    private string _route = "none";
    private bool _mainBack;
    private int _switchBackItem = -1;
    private RelayCommand? _switchBack;

    /// <summary>"main", "fallback", "silent" or "none" (no device chosen).</summary>
    public string AudioRoute
    {
        get => _route;
        private set
        {
            if (Set(ref _route, value))
            {
                OnPropertyChanged(nameof(OnFallback));
                OnPropertyChanged(nameof(OnSilentClock));
                OnPropertyChanged(nameof(AudioLost));
                OnPropertyChanged(nameof(CanSwitchBack));
            }
        }
    }

    public bool OnFallback => _route == "fallback";
    public bool OnSilentClock => _route == "silent";

    /// <summary>Playing without the main device: the banner is red (UI-70).</summary>
    public bool AudioLost => OnFallback || OnSilentClock;

    /// <summary>On the fallback with the main device connected again: the banner offers Switch back (REL-23).</summary>
    public bool CanSwitchBack => _mainBack && OnFallback;

    public RelayCommand SwitchBackCommand => _switchBack ??= new RelayCommand(SwitchBackAsync, () => CanSwitchBack);

    /// <summary>The MIDI indicator of the status row (LIVE-70): hidden with no controller, red while one is missing.</summary>
    public string MidiStatus => _midiMissing.Count > 0 ? $"{_midiMissing[0]} lost"
        : _midiInputs.Count == 1 ? _midiInputs[0]
        : _midiInputs.Count > 1 ? $"{_midiInputs.Count} inputs" : string.Empty;

    public bool HasMidiStatus => _midiInputs.Count > 0 || _midiMissing.Count > 0;
    public bool MidiOk => _midiMissing.Count == 0;

    private string? _batteryWarnedFor;

    /// <summary>
    /// In Perform the computer and the display stay awake (PLAT-60), and running on battery below 20 % warns once per
    /// service (REL-25).
    /// </summary>
    private void OnPerformPower(bool perform)
    {
        if (!perform)
        {
            SystemPower.AllowSleep();
            return;
        }

        if (!SystemPower.PreventSleep())
            AppLog.Write("Power", "Sleep could not be held off on this system");
        var key = _service?.Id ?? string.Empty;
        if (SystemPower.OnBattery == true && SystemPower.BatteryPercent is { } percent && percent < 20 && _batteryWarnedFor != key)
        {
            _batteryWarnedFor = key;
            Banner = $"On battery at {percent} %. Plug in the charger before the service.";
            AppLog.Write("Power", $"Battery warning at {percent} %");
        }
    }

    private void HookDeviceEvents(EngineClient engine)
    {
        engine.DeviceLost += loss => _ui.Post(() => OnDeviceLost(loss));
        engine.DeviceRestored += (name, device) => _ui.Post(() => OnDeviceRestored(name, device));
        engine.MainDeviceBack += name => _ui.Post(() => OnMainDeviceBack(name));
        engine.SilentClockStopped += limit => _ui.Post(() => OnSilentClockStopped(limit));
        engine.Xrun += (count, frame) => AppLog.Write("Audio", $"Xrun {count} at plan frame {frame}");
        engine.MidiInputsChanged += (open, missing) => _ui.Post(() => ShowMidi(open, missing));
    }

    /// <summary>Tells the engine the fallback device of Settings › Audio (REL-20).</summary>
    private async Task SendFallbackAsync()
    {
        if (_engine is not { } engine)
            return;
        try
        {
            var a = _settings.Audio;
            _fallbackPresent = await engine.SetFallbackDeviceAsync(a.FallbackDeviceType, a.FallbackDeviceName);
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Audio", ex);
        }
    }

    private void OnDeviceLost(DeviceLoss loss)
    {
        var lost = loss.Name.Length > 0 ? loss.Name : "The audio device";
        _mainBack = false;
        AppLog.Write("Audio", $"Device lost: {lost}; {(loss.Route == "fallback" ? $"playing on {loss.Fallback}" : "silent clock")}");
        if (loss.Route == "fallback")
        {
            AudioRoute = "fallback";
            ShowDevice(loss.Device);
            Banner = $"{lost} was lost. Playing on {loss.Fallback}.";
        }
        else
        {
            AudioRoute = "silent";
            ShowDevice(null);
            AudioStatus = "No audio device · keeping time";
            Banner = $"{lost} was lost. The song keeps time with no sound; reconnect it within a minute and the sound comes back at the right bar.";
        }

        OnPropertyChanged(nameof(CanSwitchBack));
        SwitchBackCommand.Refresh();
    }

    private void OnDeviceRestored(string name, DeviceInfo? device)
    {
        AppLog.Write("Audio", $"Main device back: {name}");
        _mainBack = false;
        AudioRoute = "main";
        if (device is not null)
            ShowDevice(device);
        Banner = null;
        ShowToast($"{(name.Length > 0 ? name : "The audio device")} is back.");
        SwitchBackCommand.Refresh();
    }

    private void OnMainDeviceBack(string name)
    {
        _mainBack = true;
        _switchBackItem = CurrentItemIndex();
        if (OnFallback)
            Banner = $"{name} is connected again. Still playing on the fallback device.";
        OnPropertyChanged(nameof(CanSwitchBack));
        SwitchBackCommand.Refresh();

        // Stopped, nothing to interrupt: switch back now. Playing: at the next section boundary (REL-23).
        if (_settings.Audio.AutoSwitchBack && State != TransportState.Playing)
            _ = SwitchBackAsync();
    }

    private void OnSilentClockStopped(double limitSec)
    {
        AppLog.Write("Audio", $"No device for {limitSec:0} s: paused");
        var time = limitSec >= 60 ? "a minute" : $"{limitSec:0} s";
        Banner = $"No audio device for {time}. Paused at {(NowLabel.Length > 0 ? NowLabel : "the current position")}. Connect a device and press Play.";
    }

    private async Task SwitchBackAsync()
    {
        if (_engine is not { } engine || !CanSwitchBack)
            return;
        try
        {
            ShowDevice(await engine.SwitchToMainAsync()); // deviceRestored follows and clears the banner
        }
        catch (EngineErrorException ex)
        {
            ShowToast(ex.Message);
        }
        catch (Exception ex) when (ex is EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Audio", ex);
        }
    }

    /// <summary>From the playback tick: on the fallback with the main device back, switch at the next section boundary (REL-23).</summary>
    private void CheckSwitchBack()
    {
        if (!CanSwitchBack || !_settings.Audio.AutoSwitchBack)
            return;
        var index = CurrentItemIndex();
        if (index != _switchBackItem)
        {
            _switchBackItem = index;
            _ = SwitchBackAsync();
        }
    }

    private int CurrentItemIndex() => _song is { } song ? song.Plan.ItemIndexAt(PlanPositionSec) : -1;

    private void ShowMidi(IReadOnlyList<string> open, IReadOnlyList<string> missing)
    {
        var lost = missing.Except(_midiMissing).ToList();
        var back = _midiMissing.Except(missing).Where(open.Contains).ToList();
        _midiInputs = open;
        _midiMissing = missing;
        foreach (var name in lost)
            AppLog.Write("Controls", $"MIDI input lost: {name}");
        foreach (var name in back)
        {
            AppLog.Write("Controls", $"MIDI input back: {name}");
            ShowToast($"{name} is connected again.");
        }

        OnPropertyChanged(nameof(MidiStatus));
        OnPropertyChanged(nameof(HasMidiStatus));
        OnPropertyChanged(nameof(MidiOk));
    }

    /// <summary>The route as the engine reports it, in case an event was missed (an app restart while on the fallback).</summary>
    private void ApplyRoute(EngineSnapshot s)
    {
        if (s.Route != _route)
            AudioRoute = s.Route;
        if (s.MainBack != _mainBack)
        {
            _mainBack = s.MainBack;
            OnPropertyChanged(nameof(CanSwitchBack));
            SwitchBackCommand.Refresh();
        }
    }
}
