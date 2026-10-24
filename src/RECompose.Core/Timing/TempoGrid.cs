// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace RECompose.Core.Timing;

/// <summary>
/// A constant-tempo grid (SEC-1). It sits behind <see cref="ITempoGrid"/> so tempo maps
/// can replace it later (SEC-7).
/// </summary>
public interface ITempoGrid
{
    double TimeToBeat(double seconds);
    double BeatToTime(double beat);
    int BeatsPerBar { get; }
}

public readonly record struct TempoGrid(double Bpm, int BeatsPerBar, double FirstDownbeatSec) : ITempoGrid
{
    public double BeatLength => 60.0 / (Bpm > 0 ? Bpm : 120);
    public double BarLength => BeatLength * Math.Max(1, BeatsPerBar);

    /// <summary>Beats since the first downbeat (negative before it).</summary>
    public double TimeToBeat(double seconds) => (seconds - FirstDownbeatSec) / BeatLength;

    public double BeatToTime(double beat) => FirstDownbeatSec + beat * BeatLength;

    public double BarToTime(int bar) => FirstDownbeatSec + (bar - 1) * BarLength;
}

/// <summary>Number formatting shared by the readouts and the UI.</summary>
public static class Format
{
    /// <summary>"72" when within 0.05 of an integer (SEC-1), else "72.5".</summary>
    public static string Bpm(double bpm) =>
        Math.Abs(bpm - Math.Round(bpm)) < 0.05
            ? Math.Round(bpm).ToString("0", CultureInfo.InvariantCulture)
            : bpm.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>"M:SS", truncating to whole seconds.</summary>
    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        var whole = (long)Math.Floor(seconds + 1e-6);
        return $"{whole / 60}:{whole % 60:00}";
    }
}
