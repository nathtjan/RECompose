// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Music;
using RECompose.Core.Services;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

public sealed record TransitionTypeChoice(TransitionType Type, string Name)
{
    public static readonly IReadOnlyList<TransitionTypeChoice> All =
    [
        new(TransitionType.Stop, "Stop"),
        new(TransitionType.Segue, "Segue"),
        new(TransitionType.Crossfade, "Crossfade"),
        new(TransitionType.Pad, "Pad"),
        new(TransitionType.CarryOver, "Carry over"),
    ];

    public override string ToString() => Name;
}

/// <summary>A pad key for a transition: a key, or null for the next song's key.</summary>
public sealed record PadKeyChoice(string? Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The transition popover (SL-21): "After {This}" / "Into {Next}", the type, its fields, and the explanation that
/// follows every change. <see cref="Result"/> is what Save stores; Clear goes back to Stop.
/// </summary>
public sealed class TransitionViewModel : ObservableObject
{
    private readonly string _thisTitle;
    private readonly string _nextTitle;
    private readonly Key? _nextKey;
    private readonly double? _nextBpm;
    private Transition _value;

    public TransitionViewModel(Transition transition, string thisTitle, string nextTitle, Key? nextKey, double? nextBpm)
    {
        _value = transition.Clamped();
        _thisTitle = thisTitle;
        _nextTitle = nextTitle;
        _nextKey = nextKey;
        _nextBpm = nextBpm;
        var next = nextKey is { } k ? $"Next song's key ({k.ToDisplayString()})" : "Next song's key";
        PadKeys = [new PadKeyChoice(null, next), .. Enumerable.Range(0, 12).Select(pc => new Key(pc)).Select(k => new PadKeyChoice(k.ToString(), k.ToDisplayString()))];
    }

    public string AfterText => $"After {_thisTitle}";
    public string IntoText => $"Into {_nextTitle}";

    public IReadOnlyList<TransitionTypeChoice> Types => TransitionTypeChoice.All;
    public IReadOnlyList<PadKeyChoice> PadKeys { get; }

    public TransitionTypeChoice Type
    {
        get => TransitionTypeChoice.All.First(c => c.Type == _value.Type);
        set
        {
            if (value is not null)
                Change(_value with { Type = value.Type });
        }
    }

    public bool IsSegue => _value.Type == TransitionType.Segue;
    public bool IsCrossfade => _value.Type == TransitionType.Crossfade;
    public bool IsPad => _value.Type == TransitionType.Pad;
    public bool IsCarryOver => _value.Type == TransitionType.CarryOver;

    /// <summary>Segue gap, 0–8 beats.</summary>
    public int GapBeats { get => _value.SegueGapBeats; set => Change(_value with { SegueGapBeats = value }); }

    /// <summary>Crossfade length, 1–30 s.</summary>
    public int CrossfadeSeconds { get => (int)Math.Round(_value.CrossfadeSec); set => Change(_value with { CrossfadeSec = value }); }

    public PadKeyChoice PadKey
    {
        get => PadKeys.FirstOrDefault(p => p.Key is not null && Key.TryParse(_value.PadKey, out var k) && p.Key == k.ToString()) ?? PadKeys[0];
        set
        {
            if (value is not null)
                Change(_value with { PadKey = value.Key });
        }
    }

    public bool ClickDuring { get => _value.ClickDuring; set => Change(_value with { ClickDuring = value }); }

    /// <summary>The click tempo; empty uses the next song's BPM.</summary>
    public string ClickBpmText
    {
        get => _value.ClickBpm is { } bpm ? Core.Timing.Format.Bpm(bpm) : string.Empty;
        set
        {
            double? bpm = double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : null;
            Change(_value with { ClickBpm = bpm });
        }
    }

    /// <summary>"72 (the next song's BPM)" as the placeholder of the BPM field.</summary>
    public string ClickBpmPlaceholder => _nextBpm is { } bpm ? $"{Core.Timing.Format.Bpm(bpm)} (next song)" : "BPM";

    public bool AutoStartNext { get => _value.AutoStartNext; set => Change(_value with { AutoStartNext = value }); }
    public int AutoStartBars { get => _value.AutoStartAfterBars; set => Change(_value with { AutoStartAfterBars = value }); }
    public bool FadePadOut { get => _value.FadePadOutOnNextStart; set => Change(_value with { FadePadOutOnNextStart = value }); }
    public bool RetuneToNextKey { get => _value.RetuneToNextKey; set => Change(_value with { RetuneToNextKey = value }); }

    /// <summary>What will happen, in the band's words (SL-20).</summary>
    public string Explanation => ServiceText.Explanation(_value, _thisTitle, _nextTitle, _nextKey);

    /// <summary>The chip this transition will show.</summary>
    public string Chip => ServiceText.Chip(_value, _nextKey, _nextBpm);

    public Transition Result => _value;

    /// <summary>Back to Stop (SL-21 Clear).</summary>
    public void Clear() => Change(Transition.Stop);

    private void Change(Transition value)
    {
        value = value.Clamped();
        if (value == _value)
            return;
        _value = value;
        foreach (var name in new[]
                 {
                     nameof(Type), nameof(IsSegue), nameof(IsCrossfade), nameof(IsPad), nameof(IsCarryOver), nameof(GapBeats), nameof(CrossfadeSeconds),
                     nameof(PadKey), nameof(ClickDuring), nameof(ClickBpmText), nameof(AutoStartNext), nameof(AutoStartBars), nameof(FadePadOut),
                     nameof(RetuneToNextKey), nameof(Explanation), nameof(Chip), nameof(Result),
                 })
            OnPropertyChanged(name);
    }
}
