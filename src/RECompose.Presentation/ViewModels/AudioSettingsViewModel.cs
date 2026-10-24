// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Library;
using RECompose.Core.Settings;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>Settings › Audio (SET-10): driver, device, rate, buffer, and the Main and Cue outputs (PB-85).</summary>
public sealed class AudioSettingsViewModel : ObservableObject
{
    public const string DeviceRate = "Device rate";

    private readonly IReadOnlyList<DeviceTypeInfo> _types;
    private readonly DeviceInfo? _current;
    private string? _selectedType;
    private string? _selectedDevice;
    private IReadOnlyList<string> _devices = [];
    private string _selectedRate = DeviceRate;
    private string _selectedBuffer = "256";
    private string _mainOutput;
    private string _cueOutput;

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
    }

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
