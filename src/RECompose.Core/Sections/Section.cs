// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Core.Sections;

/// <summary>Where a section came from (DM-20).</summary>
public enum SectionOrigin
{
    Guide,
    Marked,
    Manual,
    Assistant,
}

/// <summary>
/// One written section (DM-20). Its start is always on a bar line. Index, end, chart bar,
/// length and label are derived (<see cref="SectionList"/>).
/// </summary>
public sealed record Section
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Derived from the name unless <see cref="TypeOverridden"/> (SEC-43).</summary>
    public SectionType Type { get; init; } = SectionType.Other;
    public bool TypeOverridden { get; init; }

    public double StartSec { get; init; }
    public SectionOrigin Origin { get; init; } = SectionOrigin.Manual;

    /// <summary>From guide detection (SEC-15).</summary>
    public double? Confidence { get; init; }
    public bool NeedsReview { get; init; }

    /// <summary>True once the user renamed, retyped or moved a detected section; re-detect keeps it (SEC-17).</summary>
    public bool Edited { get; init; }

    /// <summary>The guide call this section was detected from (its written call, AUTO-30).</summary>
    public string? CallId { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>A section with its type derived from the name.</summary>
    public static Section Create(string name, double startSec, SectionOrigin origin) => new()
    {
        Id = NewId(),
        Name = name,
        Type = SectionTypes.FromName(name),
        StartSec = startSec,
        Origin = origin,
    };
}

/// <summary>One recognised word of the guide track, in source seconds.</summary>
public sealed record GuideWord(string Text, double Start, double End, double Confidence);

/// <summary>A spoken call in the guide (SEC-13): an utterance with a vocabulary term.</summary>
public sealed record GuideCall
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public double Confidence { get; init; }

    /// <summary>The section name the call announces ("Verse 2", "Chorus"), or null for counts and directives.</summary>
    public string? SectionName { get; init; }

    /// <summary>Where that section would start (on a bar line), when it is a section call.</summary>
    public double? SectionStartSec { get; init; }

    /// <summary>The section detection created from this call.</summary>
    public string? UsedForSectionId { get; init; }
}

/// <summary>The guide's transcript and calls (DM-10 guideAnalysis, SEC-11).</summary>
public sealed record GuideAnalysis
{
    public required string TrackId { get; init; }

    /// <summary>Size and modification time of the guide file when it was transcribed; a change means transcribe again.</summary>
    public string FileStamp { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public DateTimeOffset TranscribedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<GuideWord> Words { get; init; } = [];

    /// <summary>The calls found by the last Detect.</summary>
    public List<GuideCall> Calls { get; init; } = [];
}

/// <summary>The tempo grid found in the click track (DM-10 clickAnalysis, SEC-1).</summary>
public sealed record ClickAnalysis
{
    public required string TrackId { get; init; }
    public double Bpm { get; init; }
    public int BeatsPerBar { get; init; } = 4;
    public double FirstDownbeatSec { get; init; }

    /// <summary>No accent contrast, so 4 beats per bar was assumed (SEC-1 step 2).</summary>
    public bool MeterAssumed { get; init; }
    public int Onsets { get; init; }
    public int OnsetsUsed { get; init; }
    public DateTimeOffset AnalysedAt { get; init; } = DateTimeOffset.UtcNow;
}
