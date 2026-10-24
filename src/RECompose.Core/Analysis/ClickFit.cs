// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Core.Analysis;

/// <summary>An onset in the click track: its time, peak level and brightness (spectral centroid), from <c>recompose-engine onsets</c>.</summary>
public sealed record ClickOnset(double Time, double LevelDb, double CentroidHz);

/// <summary>The tempo grid found in a click track (SEC-1).</summary>
public sealed record ClickGrid(double Bpm, int BeatsPerBar, double FirstDownbeatSec, bool MeterAssumed, int OnsetsUsed, int PickupBeats);

/// <summary>
/// SEC-1 steps 1–3: fit <c>t_k = t_0 + k·P</c> to the click onsets with outlier rejection,
/// find the accents (louder and/or brighter onsets) to get the meter, and take the first
/// accent as the first downbeat.
/// </summary>
public static class ClickFit
{
    private const double MinPeriod = 60.0 / 300;
    private const double MaxPeriod = 60.0 / 30;

    /// <summary>Returns null when there are too few regular onsets to call it a click.</summary>
    public static ClickGrid? Fit(IReadOnlyList<ClickOnset> onsets)
    {
        var sorted = onsets.OrderBy(o => o.Time).ToList();
        if (sorted.Count < 4)
            return null;

        // Step 1a: a first period from the most common interval (the median of plausible ones).
        var intervals = new List<double>();
        for (var i = 1; i < sorted.Count; i++)
        {
            var d = sorted[i].Time - sorted[i - 1].Time;
            if (d is >= MinPeriod and <= MaxPeriod)
                intervals.Add(d);
        }
        if (intervals.Count < 3)
            return null;
        intervals.Sort();
        var period = Median(intervals);
        var close = intervals.Where(d => Math.Abs(d - period) < 0.1 * period).ToList();
        if (close.Count >= 3)
            period = close.Average();

        // Step 1b: beat numbers from local intervals, so a small period error never accumulates.
        // Onsets that fall between beats (a count-in voice, a stray hit) are skipped.
        // The chain starts at the first onset that is followed by a beat a period later.
        var startIndex = 0;
        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            if (sorted.Skip(i + 1).Take(4).Any(o => Math.Abs((o.Time - sorted[i].Time) / period - 1) < 0.1))
            {
                startIndex = i;
                break;
            }
        }

        var beats = new List<(int K, ClickOnset Onset)> { (0, sorted[startIndex]) };
        var last = sorted[startIndex];
        var k = 0;
        for (var i = startIndex + 1; i < sorted.Count; i++)
        {
            var ratio = (sorted[i].Time - last.Time) / period;
            var steps = (int)Math.Round(ratio);
            if (steps < 1 || Math.Abs(ratio - steps) > 0.15)
                continue;
            k += steps;
            beats.Add((k, sorted[i]));
            last = sorted[i];
        }

        // Step 1c: least squares on (k, t), dropping residuals over 15 % of a beat, three passes.
        var used = beats;
        double t0 = 0, p = period;
        for (var pass = 0; pass < 3; pass++)
        {
            (t0, p) = LeastSquares(used);
            var kept = used.Where(b => Math.Abs(b.Onset.Time - (t0 + b.K * p)) <= 0.15 * p).ToList();
            if (kept.Count == used.Count || kept.Count < 4)
                break;
            used = kept;
        }

        if (used.Count < 4 || p is < MinPeriod or > MaxPeriod)
            return null;

        // Step 2: accents by level and brightness.
        var (accented, contrast) = Accents(used.Select(b => b.Onset).ToList());
        var bpm = 60.0 / p;
        if (!contrast)
        {
            var first = used.Min(b => b.K);
            return new ClickGrid(bpm, 4, Math.Max(0, t0 + first * p), true, used.Count, 0);
        }

        var accentBeats = used.Where((b, i) => accented[i]).Select(b => b.K).ToList();
        var gaps = new Dictionary<int, int>();
        for (var i = 1; i < accentBeats.Count; i++)
        {
            var gap = accentBeats[i] - accentBeats[i - 1];
            if (gap is >= 2 and <= 12)
                gaps[gap] = gaps.TryGetValue(gap, out var n) ? n + 1 : 1;
        }

        if (gaps.Count == 0)
        {
            var first = used.Min(b => b.K);
            return new ClickGrid(bpm, 4, Math.Max(0, t0 + first * p), true, used.Count, 0);
        }

        var beatsPerBar = gaps.OrderByDescending(g => g.Value).ThenBy(g => g.Key).First().Key;

        // Step 3: the first accent is the first downbeat; earlier clicks are pickup beats.
        var downbeatK = accentBeats[0];
        var pickups = downbeatK - used.Min(b => b.K);
        return new ClickGrid(bpm, beatsPerBar, Math.Max(0, t0 + downbeatK * p), false, used.Count, pickups);
    }

    /// <summary>
    /// Two clusters on z-scored level and brightness. The accent cluster is the louder and
    /// brighter one; it counts only if the clusters are clearly apart and the accents are the
    /// minority (beat 1 of each bar).
    /// </summary>
    private static (bool[] Accented, bool Contrast) Accents(List<ClickOnset> onsets)
    {
        var n = onsets.Count;
        var level = onsets.Select(o => o.LevelDb).ToArray();
        var bright = onsets.Select(o => Math.Log(Math.Max(1, o.CentroidHz))).ToArray();
        var levelSpread = Spread(level);
        var brightSpread = Spread(bright);

        // Below these differences there is nothing to hear: 1 dB, about 5 % in brightness.
        if (levelSpread < 0.5 && brightSpread < 0.025)
            return (new bool[n], false);

        var score = new double[n];
        for (var i = 0; i < n; i++)
            score[i] = Z(level, i, levelSpread, 0.5) + Z(bright, i, brightSpread, 0.025);

        // 1-D two-means on the combined score.
        var lo = score.Min();
        var hi = score.Max();
        if (hi - lo < 1e-9)
            return (new bool[n], false);
        double cLo = lo, cHi = hi;
        var accented = new bool[n];
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var mid = (cLo + cHi) / 2;
            for (var i = 0; i < n; i++)
                accented[i] = score[i] > mid;
            var a = score.Where((_, i) => accented[i]).DefaultIfEmpty(cHi).Average();
            var b = score.Where((_, i) => !accented[i]).DefaultIfEmpty(cLo).Average();
            if (Math.Abs(a - cHi) < 1e-9 && Math.Abs(b - cLo) < 1e-9)
                break;
            cHi = a;
            cLo = b;
        }

        var count = accented.Count(x => x);
        if (count == 0 || count > n / 2)
            return (accented, false);

        // The clusters must be separated by much more than the spread inside them.
        var inner = Math.Sqrt(score.Select((s, i) => Math.Pow(s - (accented[i] ? cHi : cLo), 2)).Average());
        return (accented, cHi - cLo > 4 * Math.Max(inner, 0.05));
    }

    private static double Z(double[] values, int i, double spread, double floor)
    {
        var mean = values.Average();
        return (values[i] - mean) / Math.Max(spread, floor);
    }

    private static double Spread(double[] values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Select(v => (v - mean) * (v - mean)).Average());
    }

    private static (double T0, double P) LeastSquares(List<(int K, ClickOnset Onset)> points)
    {
        double n = points.Count, sk = 0, st = 0, skk = 0, skt = 0;
        foreach (var (k, o) in points)
        {
            sk += k;
            st += o.Time;
            skk += (double)k * k;
            skt += k * o.Time;
        }

        var denominator = n * skk - sk * sk;
        if (Math.Abs(denominator) < 1e-12)
            return (points[0].Onset.Time, 0.5);
        var p = (n * skt - sk * st) / denominator;
        var t0 = (st - p * sk) / n;
        return (t0, p);
    }

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}
