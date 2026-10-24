// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Core.Timing;

/// <summary>A bar.beat position: "32.2".</summary>
public readonly record struct BarBeat(int Bar, int Beat)
{
    public override string ToString() => $"{Bar}.{Beat}";
}

/// <summary>
/// The readouts of PB-31…34 for a song played as written (milestone 1: the plan is the
/// whole file, so song time equals source time and S0 is the first downbeat).
/// </summary>
public static class Readouts
{
    // PB-32: tol = 0.002 s / beatLen, so a position 2 ms before a beat already counts as that beat.
    private const double ToleranceSec = 0.002;

    /// <summary>PB-32 song bar: beatIdx = floor((t − S0)/beatLen + tol); bar = floorDiv + 1, beat = floorMod + 1.</summary>
    public static BarBeat SongBar(TempoGrid grid, double seconds)
    {
        var beatIndex = (long)Math.Floor(grid.TimeToBeat(seconds) + ToleranceSec / grid.BeatLength);
        var bpb = Math.Max(1, grid.BeatsPerBar);
        var bar = FloorDiv(beatIndex, bpb) + 1;
        var beat = FloorMod(beatIndex, bpb) + 1;
        return new BarBeat((int)bar, (int)beat);
    }

    /// <summary>Total song bars from S0 to the end (PB-32): 124 for fixture A.</summary>
    public static int TotalBars(TempoGrid grid, double durationSec) =>
        Math.Max(1, (int)Math.Floor((durationSec - grid.FirstDownbeatSec) / grid.BarLength + ToleranceSec / grid.BarLength));

    /// <summary>PB-34 chart bar. As written in M1 it equals the song bar.</summary>
    public static BarBeat ChartBar(TempoGrid grid, double sourceSeconds) => SongBar(grid, sourceSeconds);

    /// <summary>PB-33 elapsed "1:18" and remaining "−3:51" (plan start 0, plan end = the end of the file).</summary>
    public static (string Elapsed, string Remaining) Times(double seconds, double durationSec, double tempoFactor = 1.0)
    {
        tempoFactor = tempoFactor > 0 ? tempoFactor : 1.0;
        var elapsed = Format.Clock(seconds / tempoFactor);
        var remaining = "−" + Format.Clock(Math.Max(0, durationSec - seconds) / tempoFactor);
        return (elapsed, remaining);
    }

    /// <summary>Time of the bar line <paramref name="bars"/> bars from the one at or before <paramref name="seconds"/> (←/→ when stopped).</summary>
    public static double StepBars(TempoGrid grid, double seconds, int bars, double durationSec)
    {
        var current = SongBar(grid, seconds).Bar;
        var atBarLine = Math.Abs(grid.BarToTime(current) - seconds) < 0.001;
        var target = bars < 0 && !atBarLine ? current + bars + 1 : current + bars;
        var time = grid.BarToTime(target);
        return Math.Clamp(time, 0, Math.Max(0, durationSec));
    }

    private static long FloorDiv(long a, long b) => (long)Math.Floor((double)a / b);

    private static long FloorMod(long a, long b) => ((a % b) + b) % b;
}
