// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;

namespace RECompose.Core.Services;

/// <summary>Which arrangement a service entry plays (DM-41).</summary>
public enum ArrangementChoiceType
{
    /// <summary>A named variant of the song.</summary>
    Variant,

    /// <summary>A copy that belongs to this service only (ARR-12).</summary>
    Custom,

    /// <summary>Every written section in order, no options (PB-41 step 1).</summary>
    AsWritten,
}

/// <summary>
/// The arrangement of a service entry (DM-41): a variant by id, a custom arrangement stored in
/// the entry, or as written.
/// </summary>
public sealed record ArrangementChoice
{
    public ArrangementChoiceType Type { get; init; } = ArrangementChoiceType.AsWritten;

    /// <summary>Type = Variant: the song's arrangement id.</summary>
    public string? ArrangementId { get; init; }

    /// <summary>Type = Custom: the arrangement itself.</summary>
    public Arrangement? Arrangement { get; init; }

    public static ArrangementChoice AsWritten { get; } = new();

    public static ArrangementChoice OfVariant(string arrangementId) => new() { Type = ArrangementChoiceType.Variant, ArrangementId = arrangementId };

    public static ArrangementChoice OfCustom(Arrangement arrangement) => new() { Type = ArrangementChoiceType.Custom, Arrangement = arrangement };
}

/// <summary>How an entry starts (SL-36).</summary>
public enum CountInMode
{
    /// <summary>The stems' own lead-in before the first section (the default).</summary>
    Track,

    /// <summary>1 or 2 bars of generated click before the first section, instead of the lead-in.</summary>
    Generated,

    /// <summary>Start on the first section's downbeat.</summary>
    None,
}

public sealed record CountIn
{
    public CountInMode Mode { get; init; } = CountInMode.Track;

    /// <summary>1 or 2 (generated count-ins only).</summary>
    public int Bars { get; init; } = 1;
}

/// <summary>What happens between an entry and the next one (DM-42, SL-20).</summary>
public enum TransitionType
{
    Stop,
    Segue,
    Crossfade,
    Pad,
    CarryOver,
}

/// <summary>The transition after an entry (DM-42). Fields that don't apply to <see cref="Type"/> are kept, so switching type and back loses nothing.</summary>
public sealed record Transition
{
    public const int MaxSegueGapBeats = 8;
    public const double MinCrossfadeSec = 1;
    public const double MaxCrossfadeSec = 30;

    public TransitionType Type { get; init; } = TransitionType.Stop;

    /// <summary>Segue: beats of silence between the songs, 0–8.</summary>
    public int SegueGapBeats { get; init; }

    /// <summary>Crossfade: seconds, 1–30.</summary>
    public double CrossfadeSec { get; init; } = 6;

    /// <summary>Pad: the pad key; null = the next song's key.</summary>
    public string? PadKey { get; init; }

    /// <summary>Pad: run the click during the transition.</summary>
    public bool ClickDuring { get; init; } = true;

    /// <summary>Pad: the click tempo; null = the next song's BPM.</summary>
    public double? ClickBpm { get; init; }

    /// <summary>Pad: start the next song by itself after <see cref="AutoStartAfterBars"/>.</summary>
    public bool AutoStartNext { get; init; }

    public int AutoStartAfterBars { get; init; } = 4;

    /// <summary>Pad: fade the pad out when the next song starts.</summary>
    public bool FadePadOutOnNextStart { get; init; } = true;

    /// <summary>Carry over: retune the playing pad to the next song's key.</summary>
    public bool RetuneToNextKey { get; init; } = true;

    public static Transition Stop { get; } = new();

    /// <summary>
    /// The transition with every value in range. A number missing from a file reads as 0 rather than its default
    /// (source-generated JSON sets every init property), so 0 where 0 isn't allowed means the default.
    /// </summary>
    public Transition Clamped() => this with
    {
        SegueGapBeats = Math.Clamp(SegueGapBeats, 0, MaxSegueGapBeats),
        CrossfadeSec = CrossfadeSec > 0 && double.IsFinite(CrossfadeSec) ? Math.Clamp(CrossfadeSec, MinCrossfadeSec, MaxCrossfadeSec) : 6,
        ClickBpm = ClickBpm is { } bpm && double.IsFinite(bpm) && bpm > 0 ? Math.Clamp(bpm, 20, 300) : null,
        AutoStartAfterBars = AutoStartAfterBars > 0 ? Math.Min(AutoStartAfterBars, 32) : 4,
    };
}

/// <summary>One song in a service with its own arrangement, key, tempo, mix, count-in and transition (DM-41).</summary>
public sealed record ServiceEntry
{
    public const int MaxKeyOffset = 12;
    public const int MinTempoPercent = 50;
    public const int MaxTempoPercent = 150;

    public required string Id { get; init; }
    public required string SongId { get; init; }
    public ArrangementChoice Arrangement { get; init; } = ArrangementChoice.AsWritten;

    /// <summary>Semitones from the song's original key, −12…+12 (PB-60, PB-62).</summary>
    public int KeyOffset { get; init; }

    /// <summary>50…150 (PB-60).</summary>
    public int TempoPercent { get; init; } = 100;

    /// <summary>The mix preset; null or an unknown id = the song's default (DM-13).</summary>
    public string? MixPresetId { get; init; }

    public CountIn CountIn { get; init; } = new();
    public Transition TransitionAfter { get; init; } = Transition.Stop;

    /// <summary>Shown in the setlist rail and the Perform view (DM-41).</summary>
    public string Notes { get; init; } = string.Empty;

    public double TempoFactor => TempoPercent / 100.0;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// The entry with every value inside its range and every field present (old or damaged files). A tempo of 0
    /// is a missing value (see <see cref="Transition.Clamped"/>) and means 100 %.
    /// </summary>
    public ServiceEntry Clamped()
    {
        var choice = Arrangement ?? ArrangementChoice.AsWritten;
        if (choice.Type == ArrangementChoiceType.Custom)
            choice = choice.Arrangement is null ? ArrangementChoice.AsWritten : choice with { Arrangement = choice.Arrangement.Normalized() };
        else if (choice.Type == ArrangementChoiceType.Variant && string.IsNullOrEmpty(choice.ArrangementId))
            choice = ArrangementChoice.AsWritten;

        var countIn = CountIn ?? new CountIn();
        return this with
        {
            Arrangement = choice,
            KeyOffset = Math.Clamp(KeyOffset, -MaxKeyOffset, MaxKeyOffset),
            TempoPercent = TempoPercent == 0 ? 100 : Math.Clamp(TempoPercent, MinTempoPercent, MaxTempoPercent),
            CountIn = countIn with { Bars = Math.Clamp(countIn.Bars, 1, 2) },
            TransitionAfter = (TransitionAfter ?? Transition.Stop).Clamped(),
            Notes = Notes ?? string.Empty,
        };
    }
}

/// <summary>The stored result of the last service check (REL-1, REL-2). Written from M6.</summary>
public sealed record ServiceCheckResult
{
    public DateTimeOffset At { get; init; }

    /// <summary>"ready", "warnings" or "notReady".</summary>
    public string Result { get; init; } = "notReady";

    public List<string> Items { get; init; } = [];

    /// <summary>What the check looked at (<see cref="ServiceCheck.Signature"/>): a different one later means "Changed since check".</summary>
    public string Signature { get; init; } = string.Empty;
}

/// <summary>One worship service's setlist (DM-40, SL-1).</summary>
public sealed record Service
{
    public int SchemaVersion { get; init; } = 2;
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Local date and time of the service ("2026-10-26T09:00"), or null.</summary>
    public DateTime? Date { get; init; }

    public List<ServiceEntry> Entries { get; init; } = [];

    /// <summary>After a Stop transition, cue the next entry (SL-12).</summary>
    public bool AutoAdvance { get; init; } = true;

    public ServiceCheckResult? LastCheck { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>The service with every list present and every entry clamped.</summary>
    public Service Normalized() => this with
    {
        Entries = (Entries ?? []).Where(e => e is not null && !string.IsNullOrEmpty(e.SongId)).Select(e => e.Clamped()).ToList(),
    };

    public ServiceEntry? Entry(string? entryId) => entryId is null ? null : Entries.FirstOrDefault(e => e.Id == entryId);

    public int IndexOf(string? entryId) => entryId is null ? -1 : Entries.FindIndex(e => e.Id == entryId);
}
