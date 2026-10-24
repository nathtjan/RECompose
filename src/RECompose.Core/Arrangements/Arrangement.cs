// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Core.Arrangements;

/// <summary>What an arrangement entry is (DM-30).</summary>
public enum EntryKind
{
    Section,
    Click,
    Pad,
    Hold,
}

/// <summary>What happens at a stop point (ARR-36). At the end of the song or before a hold, anything but Cut rings out.</summary>
public enum EndingMode
{
    Continue,
    RingOut,
    Cut,
}

/// <summary>What the guide announces before an entry (ARR-40, AUTO-30).</summary>
public enum GuideCallMode
{
    Auto,
    Off,
    Custom,
}

/// <summary>How a variant came to be (DM-30).</summary>
public enum ArrangementOrigin
{
    Manual,
    Assistant,
    Copy,
    Default,
}

/// <summary>Part of a section: bars <see cref="FromBar"/> to <see cref="ToBar"/>, 1-based within the section (ARR-31). A null end means the end of the section.</summary>
public sealed record BarRange
{
    public int FromBar { get; init; } = 1;
    public int? ToBar { get; init; }

    public bool IsWhole => FromBar <= 1 && ToBar is null;
}

/// <summary>Repeat ×n or until cued (ARR-32). Each pass is its own plan item.</summary>
public sealed record RepeatSpec
{
    public const int MaxCount = 8;

    public int Count { get; init; } = 1;
    public bool UntilCued { get; init; }
    public int MaxPasses { get; init; } = 16;
}

/// <summary>Tracks and groups that are silent while an entry plays (ARR-34).</summary>
public sealed record MuteSpec
{
    public List<string> TrackIds { get; init; } = [];
    public List<string> Groups { get; init; } = [];

    /// <summary>For repeats: mute only in the first pass.</summary>
    public bool FirstPassOnly { get; init; }

    public bool IsEmpty => TrackIds.Count == 0 && Groups.Count == 0;
}

/// <summary>What a hold does while it waits for GO (ARR-38).</summary>
public sealed record HoldOptions
{
    public bool Pad { get; init; } = true;
    public bool Click { get; init; }
    public int CountInBars { get; init; } = 1;
}

/// <summary>One line of an arrangement (DM-30): a section with options, bars of click or pad, or a hold.</summary>
public sealed record ArrangementEntry
{
    public required string Id { get; init; }
    public EntryKind Kind { get; init; } = EntryKind.Section;

    // Kind = Section
    public string? SectionId { get; init; }
    public BarRange Range { get; init; } = new();
    public RepeatSpec Repeat { get; init; } = new();

    /// <summary>Key lift in semitones on top of the entry key, −6…+6 (ARR-33).</summary>
    public int Transpose { get; init; }

    public MuteSpec Mutes { get; init; } = new();
    public int FadeInBars { get; init; }
    public int FadeOutBars { get; init; }
    public EndingMode Ending { get; init; } = EndingMode.Continue;
    public GuideCallMode GuideCall { get; init; } = GuideCallMode.Auto;
    public string? GuideCallText { get; init; }

    /// <summary>MD note, at most 80 characters (ARR-37). Any kind.</summary>
    public string? Note { get; init; }

    // Kind = Click or Pad (ARR-39)
    public int LengthBars { get; init; } = 4;
    public bool SpokenCount { get; init; } = true;

    /// <summary>Pad key; null follows the entry key.</summary>
    public string? PadKey { get; init; }

    // Kind = Hold (ARR-38)
    public HoldOptions Hold { get; init; } = new();

    public const int MaxNoteLength = 80;
    public const int MaxFadeBars = 4;
    public const int MaxLift = 6;
    public const int MaxGeneratedBars = 32;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static ArrangementEntry ForSection(string sectionId) => new() { Id = NewId(), Kind = EntryKind.Section, SectionId = sectionId };

    public static ArrangementEntry ClickBars(int bars = 4) => new() { Id = NewId(), Kind = EntryKind.Click, LengthBars = bars };

    public static ArrangementEntry PadBars(int bars = 8) => new() { Id = NewId(), Kind = EntryKind.Pad, LengthBars = bars };

    public static ArrangementEntry NewHold() => new() { Id = NewId(), Kind = EntryKind.Hold };

    /// <summary>The entry with every value inside its allowed range and every list present.</summary>
    public ArrangementEntry Clamped()
    {
        // Files from other versions can hold nulls where a record is expected.
        var range = Range ?? new BarRange();
        var repeat = Repeat ?? new RepeatSpec();
        var mutes = Mutes ?? new MuteSpec();
        var hold = Hold ?? new HoldOptions();
        var from = Math.Max(1, range.FromBar);
        int? to = range.ToBar is { } t ? Math.Max(1, t) : null;
        var note = Note is null ? null : Note.Length > MaxNoteLength ? Note[..MaxNoteLength] : Note;
        return this with
        {
            Range = new BarRange { FromBar = from, ToBar = to },
            Repeat = repeat with
            {
                Count = Math.Clamp(repeat.Count, 1, RepeatSpec.MaxCount),
                MaxPasses = Math.Clamp(repeat.MaxPasses, 1, 99),
            },
            Transpose = Math.Clamp(Transpose, -MaxLift, MaxLift),
            FadeInBars = Math.Clamp(FadeInBars, 0, MaxFadeBars),
            FadeOutBars = Math.Clamp(FadeOutBars, 0, MaxFadeBars),
            LengthBars = Math.Clamp(LengthBars, 1, MaxGeneratedBars),
            Note = string.IsNullOrWhiteSpace(note) ? null : note,
            GuideCallText = string.IsNullOrWhiteSpace(GuideCallText) ? null : GuideCallText,
            Mutes = mutes with { TrackIds = mutes.TrackIds ?? [], Groups = mutes.Groups ?? [] },
            Hold = hold with { CountInBars = Math.Clamp(hold.CountInBars, 0, 4) },
        };
    }
}

/// <summary>A named list of entries: a variant (ARR-1, DM-30).</summary>
public sealed record Arrangement
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<ArrangementEntry> Entries { get; init; } = [];
    public ArrangementOrigin Origin { get; init; } = ArrangementOrigin.Manual;

    /// <summary>For a custom arrangement of a service entry: the variant it was copied from (ARR-12, "Revert to …").</summary>
    public string? CopiedFromId { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>The arrangement with every list present (old or damaged files).</summary>
    public Arrangement Normalized() =>
        this with { Entries = (Entries ?? []).Select(e => e.Clamped()).ToList() };
}
