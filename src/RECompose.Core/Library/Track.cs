// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Core.Library;

/// <summary>What a stem is for (DM-12).</summary>
public enum TrackRole
{
    Click,
    Guide,
    SpokenGuide,
    Music,
}

/// <summary>
/// Where a track plays: a logical output (Main, Cue) or a device channel (DM-12, PB-85).
/// Channels are 1-based in text ("3-4", "3") and 0-based in <see cref="Channel"/>.
/// </summary>
public readonly record struct OutputRef(OutputKind Kind, int Channel = 0, int Width = 2)
{
    public static OutputRef Main => new(OutputKind.Main);
    public static OutputRef Cue => new(OutputKind.Cue);

    public static OutputRef Direct(int channel, int width) => new(OutputKind.Direct, channel, width == 1 ? 1 : 2);

    /// <summary>"main", "cue", "3-4" or "3".</summary>
    public override string ToString() => Kind switch
    {
        OutputKind.Main => "main",
        OutputKind.Cue => "cue",
        _ => Width == 1 ? $"{Channel + 1}" : $"{Channel + 1}-{Channel + 2}",
    };

    /// <summary>The label used in lane headers and selects: "Main", "Cue", "3–4", "3".</summary>
    public string Label => Kind switch
    {
        OutputKind.Main => "Main",
        OutputKind.Cue => "Cue",
        _ => Width == 1 ? $"{Channel + 1}" : $"{Channel + 1}–{Channel + 2}",
    };

    public static OutputRef Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (t is "" or "main")
            return Main;
        if (t == "cue")
            return Cue;

        var parts = t.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is 1 or 2 && int.TryParse(parts[0], out var first) && first >= 1)
            return Direct(first - 1, parts.Length == 2 ? 2 : 1);

        return Main;
    }
}

public enum OutputKind
{
    Main,
    Cue,
    Direct,
}

/// <summary>One audio file of a song (DM-12).</summary>
public sealed record Track
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Absolute path of the audio file.</summary>
    public required string File { get; init; }

    public TrackRole Role { get; init; } = TrackRole.Music;
    public string Group { get; init; } = TrackGroups.Other;
    public int Channels { get; init; } = 2;
    public double SampleRate { get; init; }
    public long Frames { get; init; }
    public string Output { get; init; } = "main";
    public bool Hidden { get; init; }
    public int Order { get; init; }

    /// <summary>Click, guide and spoken guide are never transposed (DM-12) and stay audible under solo (PB-82).</summary>
    public bool IsClickOrGuide => Role is TrackRole.Click or TrackRole.Guide or TrackRole.SpokenGuide;

    /// <summary>DM-12: music tracks follow the entry key, click and guide never do.</summary>
    public bool TransposesByDefault => !IsClickOrGuide;

    public double DurationSec => SampleRate > 0 ? Frames / SampleRate : 0;
}

/// <summary>The group names and their lane order (SL-52).</summary>
public static class TrackGroups
{
    public const string Click = "Click";
    public const string Guide = "Guide";
    public const string Drums = "Drums";
    public const string Percussion = "Percussion";
    public const string Bass = "Bass";
    public const string Keys = "Keys";
    public const string ElectricGuitar = "Electric Gtr";
    public const string AcousticGuitar = "Acoustic Gtr";
    public const string Vocals = "Vocals";
    public const string Strings = "Strings / Orch";
    public const string Other = "Other";

    public static IReadOnlyList<string> LaneOrder { get; } =
        [Click, Guide, Drums, Percussion, Bass, Keys, ElectricGuitar, AcousticGuitar, Vocals, Strings, Other];

    public static IReadOnlyList<string> Music { get; } =
        [Drums, Percussion, Bass, Keys, ElectricGuitar, AcousticGuitar, Vocals, Strings, Other];
}
