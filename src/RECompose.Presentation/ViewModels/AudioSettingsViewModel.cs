// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Library;
using RECompose.Core.Settings;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Settings › Audio (SET-10): driver, device, rate, buffer, the Main and Cue outputs (PB-85), and the fallback device
/// that takes over when the main one is lost, with switching back by itself (REL-20, REL-23).
/// </summary>
public sealed class AudioSettingsViewModel : ObservableObject
{
    public const string DeviceRate = "Device rate";
    public const string NoFallback = "None";

    private readonly IReadOnlyList<DeviceTypeInfo> _types;
    private readonly DeviceInfo? _current;
    private string? _selectedType;
    private string? _selectedDevice;
    private IReadOnlyList<string> _devices = [];
    private string _selectedRate = DeviceRate;
    private string _selectedBuffer = "256";
    private string _mainOutput;
    private string _cueOutput;
    private readonly Dictionary<string, (string Type, string Name)> _fallbacks = [];
    private string _selectedFallback = NoFallback;
    private bool _autoSwitchBack;

    public AudioSettingsViewModel(IReadOnlyList<DeviceTypeInfo> types, DeviceInfo? current, AudioSettings settings)
    {
        _types = types;
        _current = current;
        DeviceTypes = types.Select(t => t.Name).ToList();

        _selectedType = settings.DeviceType is { } t && DeviceTypes.Contains(t) ? t : current?.Type ?? DeviceTypes.FirstOrDefault();
        UpdateDevices();
        _selectedDevice = settings.DeviceName is { } d && _devices.Contains(d) ? d : current?.Name ?? _devices.FirstOrDefault();

        SampleRates = [DeviceRate, .. (current?.SampleRates.Count > 0 ? current.SampleRates : [44100, 48000, 88200, 96000]).Select(r => r.ToString("0", CultureInfo.InvariantCulture))];
        _selectedRate = settings.SampleRate > 0 ? settings.SampleRate.ToString("0", CultureInfo.InvariantCulture) : DeviceRate;
        BufferSizes = (current?.BufferSizes.Count > 0 ? current.BufferSizes : [64, 128, 256, 512, 1024]).Select(b => b.ToString(CultureInfo.InvariantCulture)).ToList();
        _selectedBuffer = settings.BufferSize.ToString(CultureInfo.InvariantCulture);

        OutputOptions = ChannelOptions(current?.OutputChannels.Count ?? 2);
        _mainOutput = OutputRef.Direct(settings.MainChannel, settings.MainWidth).Label;
        _cueOutput = OutputRef.Direct(settings.CueChannel, settings.CueWidth).Label;
        MdOptions = [VoiceOff, .. OutputOptions];
        _mdOutput = settings.MdChannel is { } md ? OutputRef.Direct(md, settings.MdWidth).Label : VoiceOff;

        foreach (var type in types)
            foreach (var device in type.Devices)
                _fallbacks[FallbackLabel(type.Name, device)] = (type.Name, device);
        FallbackOptions = [NoFallback, .. _fallbacks.Keys];
        if (settings.FallbackDeviceName is { } fallback)
        {
            var label = FallbackLabel(settings.FallbackDeviceType ?? string.Empty, fallback);
            if (!_fallbacks.ContainsKey(label))
                FallbackOptions = [.. FallbackOptions, label]; // not connected now: still the choice
            _fallbacks.TryAdd(label, (settings.FallbackDeviceType ?? string.Empty, fallback));
            _selectedFallback = label;
        }

        _autoSwitchBack = settings.AutoSwitchBack;

        VoiceInputs = [VoiceOff, .. (current?.InputChannels ?? []).Select((name, i) => VoiceLabel(i, name))];
        _voiceInput = settings.VoiceChannel >= 0 && settings.VoiceChannel + 1 < VoiceInputs.Count ? VoiceInputs[settings.VoiceChannel + 1]
            : settings.VoiceChannel >= 0 ? VoiceLabel(settings.VoiceChannel, null)
            : VoiceOff;
        if (!VoiceInputs.Contains(_voiceInput))
            VoiceInputs = [.. VoiceInputs, _voiceInput]; // not on this device now: still the choice
        _voiceChannels = VoiceInputs.Skip(1).Select((label, i) => (label, i)).ToDictionary(x => x.label, x => x.label == _voiceInput ? settings.VoiceChannel : x.i);
        VoicePushToTalk = settings.VoicePushToTalk;
        VoiceConfirm = settings.VoiceConfirm;
        VoiceGate = settings.VoiceGate;
        VoiceBareNames = settings.VoiceBareNames;
    }

    // ---- Voice (VC-1) ---------------------------------------------------------------------------

    public const string VoiceOff = "Off";

    private static string VoiceLabel(int channel, string? name) =>
        $"Input {(channel + 1).ToString(CultureInfo.InvariantCulture)}{(string.IsNullOrWhiteSpace(name) ? string.Empty : $" · {name.Trim()}")}";

    private readonly Dictionary<string, int> _voiceChannels;
    private string _voiceInput;
    private string _voiceHeard = "Say \"let's go to the bridge\" to try it.";
    private string _voiceWouldDo = string.Empty;
    private double _voiceLevel;

    /// <summary>"Off", then the open device's inputs ("Input 1 · Mic 1").</summary>
    public IReadOnlyList<string> VoiceInputs { get; }

    public string VoiceInput { get => _voiceInput; set => Set(ref _voiceInput, value ?? VoiceOff); }

    public int VoiceChannel => _voiceChannels.TryGetValue(VoiceInput, out var channel) ? channel : -1;

    public bool VoicePushToTalk { get; set; }
    public bool VoiceConfirm { get; set; }
    public bool VoiceGate { get; set; }
    public bool VoiceBareNames { get; set; }

    /// <summary>Test panel: the last transcript and what it would have done, without touching playback.</summary>
    public string VoiceHeard { get => _voiceHeard; private set => Set(ref _voiceHeard, value); }

    public string VoiceWouldDo { get => _voiceWouldDo; private set => Set(ref _voiceWouldDo, value); }

    /// <summary>The mic's level, 0 to 1.</summary>
    public double VoiceLevel { get => _voiceLevel; private set => Set(ref _voiceLevel, value); }

    internal void ShowHeard(string heard, string wouldDo)
    {
        VoiceHeard = heard;
        VoiceWouldDo = wouldDo;
    }

    internal void ShowLevel(double level) => VoiceLevel = level;

    private static string FallbackLabel(string type, string device) => type.Length > 0 ? $"{device} ({type})" : device;

    public IReadOnlyList<string> DeviceTypes { get; }
    public IReadOnlyList<string> SampleRates { get; }
    public IReadOnlyList<string> BufferSizes { get; }
    public IReadOnlyList<string> OutputOptions { get; }

    public string CurrentDevice => _current?.StatusLabel ?? "No audio device is open.";

    public string? SelectedType
    {
        get => _selectedType;
        set
        {
            if (Set(ref _selectedType, value))
            {
                UpdateDevices();
                SelectedDevice = Devices.FirstOrDefault();
            }
        }
    }

    public IReadOnlyList<string> Devices
    {
        get => _devices;
        private set => Set(ref _devices, value);
    }

    public string? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public string SelectedRate { get => _selectedRate; set => Set(ref _selectedRate, value); }
    public string SelectedBuffer { get => _selectedBuffer; set => Set(ref _selectedBuffer, value); }

    /// <summary>Main output (music, PB-85), e.g. "1–2".</summary>
    public string MainOutput { get => _mainOutput; set => Set(ref _mainOutput, value); }

    /// <summary>Cue output (click and guide), e.g. "3–4" for in-ears.</summary>
    public string CueOutput { get => _cueOutput; set => Set(ref _cueOutput, value); }

    public bool CueSharesMain => _cueOutput == _mainOutput;

    private string _mdOutput;

    /// <summary>"Off", then the outputs: where the MD hears spoken confirmations (VC-50).</summary>
    public IReadOnlyList<string> MdOptions { get; }

    public string MdOutput
    {
        get => _mdOutput;
        set
        {
            if (Set(ref _mdOutput, value ?? VoiceOff))
                OnPropertyChanged(nameof(MdWarning));
        }
    }

    /// <summary>VC-50: the MD output shares channels with the Cue output.</summary>
    public string MdWarning => _mdOutput != VoiceOff && _mdOutput == _cueOutput ? "The whole band will hear confirmations." : string.Empty;

    // ---- Library & cache (SET-60, REL-15) -------------------------------------------------------------

    public IReadOnlyList<string> CacheBudgets { get; } = ["10 GB", "20 GB", "30 GB", "50 GB", "100 GB"];

    public string SelectedCacheBudget { get; set; } = "30 GB";

    public int CacheBudgetGb => int.TryParse(SelectedCacheBudget.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gb) ? gb : 30;

    /// <summary>"12 renders · 3.4 GB of 30 GB".</summary>
    public string CacheText { get => _cacheText; set => Set(ref _cacheText, value); }

    private string _cacheText = string.Empty;

    /// <summary>Frees the renders the next seven days do not need.</summary>
    public RelayCommand? CleanCacheCommand { get; init; }

    /// <summary>"None", then every device as "Name (Driver)".</summary>
    public IReadOnlyList<string> FallbackOptions { get; }

    public string SelectedFallback { get => _selectedFallback; set => Set(ref _selectedFallback, value ?? NoFallback); }

    /// <summary>Go back to the main device by itself: at once when stopped, at the next section boundary when playing.</summary>
    public bool AutoSwitchBack { get => _autoSwitchBack; set => Set(ref _autoSwitchBack, value); }

    public AudioSettings ToSettings(AudioSettings previous)
    {
        var main = OutputRef.Parse(OutputChoices.ToOutput(MainOutput));
        var cue = OutputRef.Parse(OutputChoices.ToOutput(CueOutput));
        return previous with
        {
            DeviceType = SelectedType,
            DeviceName = SelectedDevice,
            SampleRate = double.TryParse(SelectedRate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0,
            BufferSize = int.TryParse(SelectedBuffer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 256,
            MainChannel = main.Kind == OutputKind.Direct ? main.Channel : 0,
            MainWidth = main.Kind == OutputKind.Direct ? main.Width : 2,
            CueChannel = cue.Kind == OutputKind.Direct ? cue.Channel : 0,
            CueWidth = cue.Kind == OutputKind.Direct ? cue.Width : 2,
            FallbackDeviceType = _fallbacks.TryGetValue(SelectedFallback, out var f) && f.Type.Length > 0 ? f.Type : null,
            FallbackDeviceName = _fallbacks.TryGetValue(SelectedFallback, out var g) ? g.Name : null,
            AutoSwitchBack = AutoSwitchBack,
            VoiceChannel = VoiceChannel,
            MdChannel = MdOutput == VoiceOff ? null : OutputRef.Parse(OutputChoices.ToOutput(MdOutput)).Channel,
            MdWidth = MdOutput == VoiceOff ? 2 : OutputRef.Parse(OutputChoices.ToOutput(MdOutput)).Width,
            VoicePushToTalk = VoicePushToTalk,
            VoiceConfirm = VoiceConfirm,
            VoiceGate = VoiceGate,
            VoiceBareNames = VoiceBareNames,
        };
    }

    /// <summary>Pairs "1–2", "3–4"… then single channels "1", "2"…</summary>
    public static IReadOnlyList<string> ChannelOptions(int channels)
    {
        var count = Math.Clamp(channels, 2, 32);
        var list = new List<string>();
        for (var c = 0; c + 1 < count; c += 2)
            list.Add(OutputRef.Direct(c, 2).Label);
        for (var c = 0; c < count; c++)
            list.Add(OutputRef.Direct(c, 1).Label);
        return list;
    }

    private void UpdateDevices() =>
        Devices = _types.FirstOrDefault(t => t.Name == _selectedType)?.Devices ?? [];
}
