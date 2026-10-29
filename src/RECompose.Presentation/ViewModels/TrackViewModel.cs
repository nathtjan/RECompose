// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Audio;
using RECompose.Core.Library;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>A lane and a mixer strip (PB-80): name, output, fader, mute, solo, meter, waveform.</summary>
public sealed class TrackViewModel : ObservableObject
{
    /// <summary>The fader's bottom stop; at or below it the strip is −∞ (silent).</summary>
    public const double MinimumDb = -60;
    public const double MaximumDb = 6;

    private double _gainDb;
    private bool _mute;
    private bool _solo;
    private double _meter;
    private PeaksFile? _peaks;
    private string _outputLabel;
    private bool _missing;
    private string _problem = string.Empty;

    public TrackViewModel(Track track, string outputLabel)
    {
        Track = track;
        _outputLabel = outputLabel;
    }

    private TrackViewModel(string name)
    {
        Track = new Track { Id = "master", Name = name, File = string.Empty, Group = string.Empty };
        _outputLabel = string.Empty;
        IsMaster = true;
    }

    public static TrackViewModel CreateMaster() => new("MASTER");

    /// <summary>Raised when the user changes gain, mute or solo ("gain", "mute", "solo").</summary>
    public event Action<TrackViewModel, string>? Changed;

    public Track Track { get; private set; }
    public string Id => Track.Id;
    public string Name => Track.Name;
    public string Group => Track.Group;
    public TrackRole Role => Track.Role;
    public bool IsMaster { get; }
    public bool IsClickOrGuide => Track.IsClickOrGuide;

    public string RoleLabel => Role switch
    {
        TrackRole.Click => "Click",
        TrackRole.Guide => "Guide",
        TrackRole.SpokenGuide => "Spoken guide",
        _ => Group,
    };

    public string OutputLabel
    {
        get => _outputLabel;
        set => Set(ref _outputLabel, value);
    }

    public double GainDb
    {
        get => _gainDb;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 1), MinimumDb, MaximumDb);
            if (Set(ref _gainDb, clamped))
            {
                OnPropertyChanged(nameof(GainText));
                Changed?.Invoke(this, "gain");
            }
        }
    }

    /// <summary>"0.0", "−6.5", "+3.0", "−∞" with a real minus sign.</summary>
    public string GainText => _gainDb <= MinimumDb
        ? "−∞"
        : _gainDb.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture);

    /// <summary>The gain to send to the engine: −100 dB (silence) at the bottom stop.</summary>
    public double EngineGainDb => _gainDb <= MinimumDb ? -100 : _gainDb;

    public bool Mute
    {
        get => _mute;
        set
        {
            if (Set(ref _mute, value))
                Changed?.Invoke(this, "mute");
        }
    }

    public bool Solo
    {
        get => _solo;
        set
        {
            if (Set(ref _solo, value))
                Changed?.Invoke(this, "solo");
        }
    }

    private bool _transposes;

    /// <summary>The track plays in the entry key (PB-80 "#", DM-12): music tracks by default, never click or guide.</summary>
    public bool Transposes
    {
        get => _transposes;
        set
        {
            if (Set(ref _transposes, value))
                Changed?.Invoke(this, "transposes");
        }
    }

    /// <summary>Sets the "#" state from a mix preset without raising <see cref="Changed"/>.</summary>
    public void ApplyTransposes(bool value) => Set(ref _transposes, value, nameof(Transposes));

    /// <summary>Post-fader level, 0…1 on a −60…+6 dB scale (PB-84).</summary>
    public double Meter
    {
        get => _meter;
        private set => Set(ref _meter, value);
    }

    public PeaksFile? Peaks
    {
        get => _peaks;
        set => Set(ref _peaks, value);
    }

    public bool Missing
    {
        get => _missing;
        set => Set(ref _missing, value);
    }

    /// <summary>"File missing" or "Can't read this file" (REL-50).</summary>
    public string Problem
    {
        get => _problem;
        set => Set(ref _problem, value);
    }

    /// <summary>Sets values from the engine or an undo without raising <see cref="Changed"/>.</summary>
    public void Apply(double? gainDb = null, bool? mute = null, bool? solo = null)
    {
        if (gainDb is { } g && Set(ref _gainDb, Math.Clamp(g, MinimumDb, MaximumDb), nameof(GainDb)))
            OnPropertyChanged(nameof(GainText));
        if (mute is { } m)
            Set(ref _mute, m, nameof(Mute));
        if (solo is { } s)
            Set(ref _solo, s, nameof(Solo));
    }

    /// <summary>Takes a new peak (linear) and lets the meter fall back by <paramref name="decay"/>.</summary>
    public void FeedMeter(float peak, double decay)
    {
        var level = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) - MinimumDb) / (MaximumDb - MinimumDb), 0, 1);
        Meter = Math.Max(level, Meter - decay);
    }

    public void ClearMeter() => Meter = 0;
}
