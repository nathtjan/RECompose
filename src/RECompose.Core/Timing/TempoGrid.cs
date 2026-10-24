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

    /// <summary>The bar line nearest to <paramref name="seconds"/>, never before 0 (sections start on bar lines, DM-20).</summary>
    public double NearestBarTime(double seconds)
    {
        var index = Math.Round((seconds - FirstDownbeatSec) / BarLength);
        var time = FirstDownbeatSec + index * BarLength;
        while (time < -1e-9)
            time += BarLength;
        return Math.Max(0, time);
    }

    /// <summary>The beat nearest to <paramref name="seconds"/>, never before 0 (Alt-drag for pickups, SEC-50).</summary>
    public double NearestBeatTime(double seconds)
    {
        var time = FirstDownbeatSec + Math.Round((seconds - FirstDownbeatSec) / BeatLength) * BeatLength;
        while (time < -1e-9)
            time += BeatLength;
        return Math.Max(0, time);
    }

    /// <summary>The first bar line after <paramref name="seconds"/>, allowing <paramref name="spillBeats"/> of overrun past a bar line.</summary>
    public double BarLineAfter(double seconds, double spillBeats = 0)
    {
        var position = (seconds - FirstDownbeatSec) / BarLength - spillBeats / Math.Max(1, BeatsPerBar);
        var index = Math.Ceiling(position - 1e-9);
        var time = FirstDownbeatSec + index * BarLength;
        while (time < -1e-9)
            time += BarLength;
        return time;
    }

    /// <summary>The same grid with bar 1 at <paramref name="origin"/> (song bars count from the first section, PB-32).</summary>
    public TempoGrid WithOrigin(double origin) => this with { FirstDownbeatSec = origin };
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
