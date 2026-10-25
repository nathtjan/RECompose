// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Recovery;

/// <summary>
/// A plan item as plain data (DM-80 planSnapshot): everything the engine and the readouts need, so a plan changed by live
/// actions can be written to the journal and read back after a crash without replaying anything.
/// </summary>
public sealed record PlanItemData
{
    public string Id { get; init; } = string.Empty;
    public PlanItemKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    public string? EntryId { get; init; }
    public int EntryIndex { get; init; } = -1;

    /// <summary>The section the item plays, by id; null for generated items.</summary>
    public string? SectionId { get; init; }

    public int Pass { get; init; } = 1;
    public int Passes { get; init; } = 1;
    public bool HasSource { get; init; }
    public double SrcStart { get; init; }
    public double SrcEnd { get; init; }
    public double PlanStart { get; init; }
    public double PlanEnd { get; init; }
    public int LengthBars { get; init; }
    public int FromBar { get; init; } = 1;
    public int ToBar { get; init; }
    public bool IsPartial { get; init; }
    public int Transpose { get; init; }
    public int KeyTotal { get; init; }
    public int? SongPitchClass { get; init; }
    public int? PadPitchClass { get; init; }
    public List<string> MutedTrackIds { get; init; } = [];
    public List<string> MutedGroups { get; init; } = [];
    public double FadeInSec { get; init; }
    public double FadeOutSec { get; init; }
    public double NominalFadeOutSec { get; init; }
    public string? Note { get; init; }
    public Arrangements.EndingMode Ending { get; init; }
    public bool HoldPad { get; init; }
    public bool HoldClick { get; init; }
    public bool AfterHold { get; init; }
    public bool IsLive { get; init; }

    public static PlanItemData From(PlanItem item) => new()
    {
        Id = item.Id,
        Kind = item.Kind,
        Label = item.Label,
        EntryId = item.EntryId,
        EntryIndex = item.EntryIndex,
        SectionId = item.Section?.Id,
        Pass = item.Pass,
        Passes = item.Passes,
        HasSource = item.HasSource,
        SrcStart = item.SrcStart,
        SrcEnd = item.SrcEnd,
        PlanStart = item.PlanStart,
        PlanEnd = item.PlanEnd,
        LengthBars = item.LengthBars,
        FromBar = item.FromBar,
        ToBar = item.ToBar,
        IsPartial = item.IsPartial,
        Transpose = item.Transpose,
        KeyTotal = item.KeyTotal,
        SongPitchClass = item.SongPitchClass,
        PadPitchClass = item.PadPitchClass,
        MutedTrackIds = [.. item.MutedTrackIds],
        MutedGroups = [.. item.MutedGroups],
        FadeInSec = item.FadeInSec,
        FadeOutSec = item.FadeOutSec,
        NominalFadeOutSec = item.NominalFadeOutSec,
        Note = item.Note,
        Ending = item.Ending,
        HoldPad = item.HoldPad,
        HoldClick = item.HoldClick,
        AfterHold = item.AfterHold,
        IsLive = item.IsLive,
    };

    public PlanItem ToItem(IReadOnlyList<SectionInfo> sections) => new()
    {
        Id = Id,
        Kind = Kind,
        Label = Label,
        EntryId = EntryId,
        EntryIndex = EntryIndex,
        Section = SectionId is null ? null : sections.FirstOrDefault(s => s.Id == SectionId),
        Pass = Pass,
        Passes = Passes,
        HasSource = HasSource,
        SrcStart = SrcStart,
        SrcEnd = SrcEnd,
        PlanStart = PlanStart,
        PlanEnd = PlanEnd,
        LengthBars = LengthBars,
        FromBar = FromBar,
        ToBar = ToBar,
        IsPartial = IsPartial,
        Transpose = Transpose,
        KeyTotal = KeyTotal,
        SongPitchClass = SongPitchClass,
        PadPitchClass = PadPitchClass,
        MutedTrackIds = MutedTrackIds,
        MutedGroups = MutedGroups,
        FadeInSec = FadeInSec,
        FadeOutSec = FadeOutSec,
        NominalFadeOutSec = NominalFadeOutSec,
        Note = Note,
        Ending = Ending,
        HoldPad = HoldPad,
        HoldClick = HoldClick,
        AfterHold = AfterHold,
        IsLive = IsLive,
    };
}

/// <summary>journal.json (DM-80): rewritten every second while playing and on every plan change (REL-30).</summary>
public sealed record Journal
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
    public string? ServiceId { get; init; }
    public string? ServiceName { get; init; }
    public string? EntryId { get; init; }
    public string SongId { get; init; } = string.Empty;
    public string SongTitle { get; init; } = string.Empty;

    /// <summary>The plan position being heard, plan seconds.</summary>
    public double PositionSec { get; init; }

    public string Transport { get; init; } = "stopped";
    public bool Perform { get; init; }
    public string Lock { get; init; } = "rehearsal";
    public bool Holding { get; init; }

    /// <summary>The pitch class a pad plays, or null (DM-80 padState).</summary>
    public int? PadKey { get; init; }

    /// <summary>The song's tempo factor: the plan's times are in source seconds.</summary>
    public double TempoFactor { get; init; } = 1;

    public bool IsAsWritten { get; init; }
    public List<PlanItemData> Items { get; init; } = [];

    /// <summary>Set by PANIC: the music was stopped on purpose (REL-41).</summary>
    public bool Panicked { get; init; }

    /// <summary>The song was playing (or on hold) when this was written.</summary>
    public bool WasPlaying => Transport == "playing";

    public SongPlan ToPlan(TempoGrid chartGrid, IReadOnlyList<SectionInfo> sections, double sourceDurationSec) =>
        new(chartGrid, sections, [.. Items.Select(i => i.ToItem(sections))], sourceDurationSec, IsAsWritten, TempoFactor);

    public static Journal From(SongPlan plan, double positionSec, string transport, string songId, string songTitle) => new()
    {
        SongId = songId,
        SongTitle = songTitle,
        PositionSec = positionSec,
        Transport = transport,
        TempoFactor = plan.TempoFactor,
        IsAsWritten = plan.IsAsWritten,
        Items = [.. plan.Items.Select(PlanItemData.From)],
    };
}

/// <summary>state.json (DM-80): what the app needs on the next launch, and whether the last run ended cleanly (REL-31).</summary>
public sealed record AppState
{
    /// <summary>Written false at start-up and true on a normal quit.</summary>
    public bool CleanShutdown { get; init; } = true;

    public string Mode { get; init; } = "prepare";
    public string Lock { get; init; } = "rehearsal";
}

/// <summary>Where the music can resume after a crash or a PANIC (REL-31, REL-32, REL-41).</summary>
public sealed record ResumeTarget(string Label, string ItemId, double PlanSec, string SectionName);

public static class ResumeRules
{
    /// <summary>The section playing at the journal's position: "Resume at Chorus 2".</summary>
    public static ResumeTarget? Current(SongPlan plan, double positionSec)
    {
        if (plan.IsEmpty)
            return null;
        var index = plan.ItemIndexAt(positionSec);

        // A ring-out or a hold belongs to the section before it; a count-in or click bars resume at the section they lead to.
        for (var i = index; i >= 0; i--)
        {
            if (plan.Items[i].Kind == PlanItemKind.Section)
                return Target(plan.Items[i]);
        }

        return plan.Items.FirstOrDefault(i => i.Kind == PlanItemKind.Section) is { } first ? Target(first) : null;
    }

    /// <summary>The section after the one that was playing: "Resume at the next section (Verse 3)".</summary>
    public static ResumeTarget? Next(SongPlan plan, double positionSec)
    {
        if (Current(plan, positionSec) is not { } current)
            return null;
        var index = plan.Items.Select((item, i) => (item, i)).First(x => x.item.Id == current.ItemId).i;

        // Another pass of the same open repeat is not "the next section".
        var from = plan.Items[index];
        for (var i = index + 1; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            if (item.Kind == PlanItemKind.Section && !(from.IsOpen && item.IsOpen && item.EntryId == from.EntryId))
                return Target(item);
        }

        return null;
    }

    private static ResumeTarget Target(PlanItem item) => new($"{item.Label}", item.Id, item.PlanStart, item.Label);

    /// <summary>
    /// The plan to resume with: the same items with a bar of click in front of <paramref name="target"/> (REL-32). Returns
    /// the plan and the plan second to start playing from, which is the start of that bar.
    /// </summary>
    public static (SongPlan Plan, double StartSec)? WithCountIn(SongPlan plan, ResumeTarget target, int bars, PlanOptions options)
    {
        var index = plan.Items.Select((item, i) => (item, i)).FirstOrDefault(x => x.item.Id == target.ItemId, (null!, -1)).Item2;
        if (index < 0)
            return null;

        var items = plan.Items.Take(index).ToList();
        var count = new PlanItem
        {
            Id = $"resume-count-in~{index}",
            Kind = PlanItemKind.CountIn,
            Label = "Count-in",
            PlanStart = 0,
            PlanEnd = Math.Max(1, bars) * plan.SongGrid.BarLength,
            LengthBars = Math.Max(1, bars),
            ToBar = Math.Max(1, bars),
            KeyTotal = plan.Items[index].KeyTotal,
            SongPitchClass = plan.Items[index].SongPitchClass,
            IsLive = true,
        };
        items.Add(count);
        items.AddRange(plan.Items.Skip(index));

        var laid = PlanBuilder.Relayout(items, options, plan.ChartGrid, plan.SourceDurationSec);
        var resumed = new SongPlan(plan.ChartGrid, plan.Sections, laid, plan.SourceDurationSec, false, plan.TempoFactor);
        var start = laid.First(i => i.Id == count.Id).PlanStart;
        return (resumed, start);
    }
}
