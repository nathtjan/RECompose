// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;

namespace RECompose.Core.Audio;

/// <summary>
/// Waveform peaks written by <c>recompose-engine peaks</c>: per bucket of
/// <see cref="FramesPerBucket"/> source frames, the minimum and maximum over all channels.
/// </summary>
public sealed class PeaksFile
{
    private PeaksFile(int framesPerBucket, double sampleRate, float[] min, float[] max)
    {
        FramesPerBucket = framesPerBucket;
        SampleRate = sampleRate;
        Min = min;
        Max = max;
    }

    public int FramesPerBucket { get; }
    public double SampleRate { get; }
    public float[] Min { get; }
    public float[] Max { get; }
    public int Count => Min.Length;
    public double SecondsPerBucket => SampleRate > 0 ? FramesPerBucket / SampleRate : 0;

    /// <summary>Parses the file format of engine/src/tools.h. Returns null if it isn't one.</summary>
    public static PeaksFile? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || data[0] != 'R' || data[1] != 'E' || data[2] != 'C' || data[3] != 'P')
            return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != 1)
            return null;

        var framesPerBucket = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        var rate = BinaryPrimitives.ReadDoubleLittleEndian(data[16..]);
        if (count < 0 || data.Length < 24 + count * 8L)
            return null;

        var min = new float[count];
        var max = new float[count];
        for (var i = 0; i < count; i++)
        {
            min[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(24 + i * 8)..]);
            max[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(28 + i * 8)..]);
        }

        return new PeaksFile(framesPerBucket, rate, min, max);
    }

    public static PeaksFile? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Min and max over a time range in seconds, for drawing one pixel column.</summary>
    public (float Min, float Max) Range(double fromSec, double toSec)
    {
        if (Count == 0 || SecondsPerBucket <= 0)
            return (0, 0);
        var a = Math.Clamp((int)(fromSec / SecondsPerBucket), 0, Count - 1);
        var b = Math.Clamp((int)Math.Ceiling(toSec / SecondsPerBucket), a + 1, Count);
        float lo = 0, hi = 0;
        for (var i = a; i < b; i++)
        {
            lo = Math.Min(lo, Min[i]);
            hi = Math.Max(hi, Max[i]);
        }

        return (lo, hi);
    }
}
