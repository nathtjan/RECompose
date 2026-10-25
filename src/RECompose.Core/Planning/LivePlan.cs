// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Sections;

namespace RECompose.Core.Planning;

/// <summary>What a live action does (PB-44, PB-47).</summary>
public enum LiveActionKind
{
    JumpToItem,
    JumpToSection,

    /// <summary>Release a hold, end an open repeat after this pass, or skip to what follows.</summary>
    MoveOn,

    /// <summary>Repeat the current section until moved on.</summary>
    Vamp,
    RepeatOnce,

    /// <summary>Go to the ending: the next Ending or Outro, the song's Ending, or the ring-out.</summary>
    End,
}

/// <summary>When an action takes effect (PB-44).</summary>
public enum JumpTiming
{
    EndOfSection,
    NextBar,
    NextBeat,
    Immediately,
}

public enum ActionSource
{
    Manual,
    Keys,
    Pedal,
    Voice,
}

public sealed record LiveAction(LiveActionKind Kind, JumpTiming Timing = JumpTiming.EndOfSection, string? ItemId = null, string? SectionId = null, ActionSource Source = ActionSource.Manual);

public enum LiveOutcome
{
    /// <summary>The action is waiting for its execute point (PB-45).</summary>
    Queued,

    /// <summary>The plan already does this; nothing was queued.</summary>
    NoChange,

    /// <summary>The target is what plays next anyway.</summary>
    AlreadyNext,

    /// <summary>The song is on hold and GO releases it; the plan does not change.</summary>
    ReleaseHold,

    Rejected,
}

/// <summary>The action waiting for its execute point, and the plan that takes over there.</summary>
public sealed record PendingAction(LiveAction Action, double ExecuteAt, SongPlan Plan, string Target, string? After);

public sealed record LiveResult(LiveOutcome Outcome, PendingAction? Pending = null, string Message = "");

public sealed record LiveOptions
{
    public PlanOptions PlanOptions { get; init; } = new();

    /// <summary>Passes a vamp is built with (PB-51).</summary>
    public int MaxPasses { get; init; } = 16;

    /// <summary>After a jump to a section the plan does not hold, the written sections that follow it play on (PB-47).</summary>
    public bool ContinueAsWritten { get; init; }
}

/// <summary>
/// Live changes as plan rewrites (PB-44…47, PB-50…52). An action is computed against the committed plan, waits as the one
/// pending action, and becomes the plan when the playhead reaches its execute point. The engine swaps to the new plan at
/// that frame, so playing never stops.
/// </summary>
public sealed class LivePlan
{
    /// <summary>An execute point closer than this to now moves to the next one (PB-44).</summary>
    public const double MinLeadSec = 0.03;

    /// <summary>Immediately: a short run-up so the swap is not on the current frame.</summary>
    public const double ImmediateLeadSec = 0.05;

    private readonly LiveOptions _options;
    private int _serial;

    public LivePlan(SongPlan basePlan, LiveOptions? options = null)
    {
        Committed = basePlan;
        _options = options ?? new LiveOptions();
    }

    /// <summary>The plan that plays now.</summary>
    public SongPlan Committed { get; private set; }

    public PendingAction? Pending { get; private set; }

    /// <summary>The plan the screen shows: the pending plan once an action is queued (PB-45).</summary>
    public SongPlan Plan => Pending?.Plan ?? Committed;

    /// <summary>The plan changed by something other than an action (the arrangement was edited): pending is dropped.</summary>
    public void Replace(SongPlan plan)
    {
        Committed = plan;
        Pending = null;
    }

    /// <summary>The pending action is withdrawn; the committed plan stays (PB-45).</summary>
    public bool Cancel()
    {
        var had = Pending is not null;
        Pending = null;
        return had;
    }

    /// <summary>Commits the pending plan once the playhead has passed its execute point. True when the plan changed.</summary>
    public bool Tick(double nowSec)
    {
        if (Pending is null || nowSec < Pending.ExecuteAt)
            return false;
        Committed = Pending.Plan;
        Pending = null;
        return true;
    }

    public LiveResult Apply(LiveAction action, double nowSec, bool holding = false)
    {
        // One pending action at a time: the new one is computed from the committed plan and replaces the old (PB-45).
        var plan = Committed;
        if (plan.Items.Count == 0)
            return new LiveResult(LiveOutcome.Rejected, null, "Nothing is loaded.");

        if (action.Kind == LiveActionKind.MoveOn && holding)
            return new LiveResult(LiveOutcome.ReleaseHold, null, "Go.");

        var anchor = Anchor(plan, action, nowSec, holding);
        if (anchor is null)
            return new LiveResult(LiveOutcome.Rejected, null, "The song is ringing out.");

        var result = action.Kind switch
        {
            LiveActionKind.JumpToItem => JumpToItem(plan, action, anchor),
            LiveActionKind.JumpToSection => JumpToSection(plan, action, anchor),
            LiveActionKind.MoveOn => MoveOn(plan, action, anchor),
            LiveActionKind.Vamp => Vamp(plan, action, anchor),
            LiveActionKind.RepeatOnce => RepeatOnce(plan, action, anchor),
            LiveActionKind.End => End(plan, action, anchor),
            _ => new LiveResult(LiveOutcome.Rejected),
        };

        Pending = result.Pending;
        if (result.Outcome != LiveOutcome.Queued)
            Pending = null;
        return result;
    }

    // ---- Where the change happens ------------------------------------------------------

    /// <summary>The item the change leaves, the second it happens, and whether the item is cut short there.</summary>
    private sealed record Spot(int Index, PlanItem Item, double At, bool Holding)
    {
        public bool Cuts => !Holding && At < Item.PlanEnd - 1e-6;
    }

    private Spot? Anchor(SongPlan plan, LiveAction action, double now, bool holding)
    {
        if (holding)
        {
            var hold = plan.Items.Select((item, index) => (item, index)).LastOrDefault(x => x.item.Kind == PlanItemKind.Hold && x.item.PlanStart <= now + 0.002);
            return hold.item is null ? null : new Spot(hold.index, hold.item, hold.item.PlanStart, true);
        }

        var index = plan.ItemIndexAt(now);
        var item = plan.Items[index];
        if (item.Kind == PlanItemKind.Tail)
            return null;

        var timing = action.Kind is LiveActionKind.Vamp or LiveActionKind.RepeatOnce ? JumpTiming.EndOfSection : action.Timing;
        var at = ExecuteAt(plan, timing, now, item);

        // PB-44: under 30 ms away is too late; the end of the next item stands in for the end of this one.
        if (at - now < MinLeadSec && timing == JumpTiming.EndOfSection)
        {
            var next = NextPlaying(plan, index);
            if (next < 0)
                return new Spot(index, item, item.PlanEnd, false);
            index = next;
            item = plan.Items[index];
            at = item.PlanEnd;
        }

        return new Spot(index, item, at, false);
    }

    private static int IndexOf(SongPlan plan, string? id)
    {
        for (var i = 0; i < plan.Items.Count; i++)
        {
            if (plan.Items[i].Id == id)
                return i;
        }

        return -1;
    }

    private static int NextPlaying(SongPlan plan, int from)
    {
        for (var i = from + 1; i < plan.Items.Count; i++)
        {
            if (plan.Items[i].Length > 0)
                return i;
        }

        return -1;
    }

    private static double ExecuteAt(SongPlan plan, JumpTiming timing, double now, PlanItem item)
    {
        var grid = plan.SongGrid;
        var earliest = now + MinLeadSec;
        double at;
        switch (timing)
        {
            case JumpTiming.NextBar:
                at = grid.BarLineAfter(earliest);
                break;
            case JumpTiming.NextBeat:
                at = grid.BeatToTime(Math.Ceiling(grid.TimeToBeat(earliest) - 1e-9));
                break;
            case JumpTiming.Immediately:
                at = now + ImmediateLeadSec;
                break;
            default:
                return item.PlanEnd;
        }

        return Math.Min(at, item.PlanEnd);
    }

    /// <summary>Everything up to the spot, with the item there cut short when the change comes inside it.</summary>
    private static List<PlanItem> Prefix(SongPlan plan, Spot spot)
    {
        if (spot.Holding)
            return [.. plan.Items.Take(spot.Index)];

        var items = plan.Items.Take(spot.Index).ToList();
        items.Add(spot.Cuts ? Cut(plan, spot.Item, spot.At) : spot.Item);
        return items;
    }

    private static PlanItem Cut(SongPlan plan, PlanItem item, double at)
    {
        var length = Math.Max(0, at - item.PlanStart);
        var bars = Math.Max(1, (int)Math.Round(length / plan.SongGrid.BarLength));
        return item with
        {
            PlanEnd = at,
            SrcEnd = item.HasSource ? item.SrcStart + length : item.SrcEnd,
            LengthBars = bars,
            ToBar = item.FromBar + bars - 1,
            IsPartial = true,
            FadeOutSec = 0,
        };
    }

    private IEnumerable<PlanItem> Live(IEnumerable<PlanItem> items) =>
        items.Select(i => i with { Id = $"{i.Id}~{++_serial}", IsLive = true });

    private SongPlan Build(SongPlan plan, IEnumerable<PlanItem> items) =>
        new(plan.ChartGrid, plan.Sections, PlanBuilder.Relayout(items, _options.PlanOptions, plan.ChartGrid, plan.SourceDurationSec), plan.SourceDurationSec, false, plan.TempoFactor);

    private LiveResult Queue(SongPlan plan, LiveAction action, Spot spot, IEnumerable<PlanItem> edited, string target)
    {
        var next = Build(plan, edited);
        var after = spot.Holding ? null : NextAfter(next, spot.At);
        return new LiveResult(LiveOutcome.Queued, new PendingAction(action, spot.At, next, target, after));
    }

    /// <summary>The label of the item that plays after the target in the new plan (the NEXT card after the pending change).</summary>
    private static string? NextAfter(SongPlan plan, double at)
    {
        var index = plan.ItemIndexAt(at + 0.002);
        for (var i = index + 1; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            if (item.Kind is PlanItemKind.Section or PlanItemKind.Hold)
                return item.Label;
            if (item.Kind == PlanItemKind.Tail)
                return "Ending";
        }

        return null;
    }

    // ---- The actions -------------------------------------------------------------------

    private LiveResult JumpToItem(SongPlan plan, LiveAction action, Spot spot)
    {
        var target = IndexOf(plan, action.ItemId);
        if (target < 0 || plan.Items[target].Kind is PlanItemKind.Tail)
            return new LiveResult(LiveOutcome.Rejected, null, "That is not in the plan.");

        if (target == NextPlaying(plan, spot.Index) && action.Timing == JumpTiming.EndOfSection && !spot.Cuts)
            return new LiveResult(LiveOutcome.AlreadyNext, null, $"{plan.Items[target].Label} is next.");

        var items = Prefix(plan, spot);
        var from = target > spot.Index ? plan.Items.Skip(target) : Live(plan.Items.Skip(target));
        items.AddRange(from);
        return Queue(plan, action, spot, items, plan.Items[target].Label);
    }

    private LiveResult JumpToSection(SongPlan plan, LiveAction action, Spot spot)
    {
        var info = plan.Sections.FirstOrDefault(s => s.Id == action.SectionId);
        if (info is null)
            return new LiveResult(LiveOutcome.Rejected, null, "That section is not in the song.");

        var later = -1;
        for (var i = spot.Index + 1; i < plan.Items.Count; i++)
        {
            if (plan.Items[i].Kind == PlanItemKind.Section && plan.Items[i].Section?.Id == info.Id)
            {
                later = i;
                break;
            }
        }

        if (later >= 0)
            return JumpToItem(plan, action with { Kind = LiveActionKind.JumpToItem, ItemId = plan.Items[later].Id }, spot);

        // Not played again: it replaces what would play next, and the plan goes on from there (PB-47).
        var items = Prefix(plan, spot);
        var liveItem = PlanBuilder.LiveSection(info, $"live:{info.Id}~{++_serial}", 0, _options.PlanOptions);
        items.Add(liveItem);

        if (_options.ContinueAsWritten)
        {
            foreach (var following in plan.Sections.Where(s => s.Index > info.Index).OrderBy(s => s.Index))
                items.Add(PlanBuilder.LiveSection(following, $"live:{following.Id}~{++_serial}", 0, _options.PlanOptions));
        }
        else
        {
            var next = spot.Index + 1;
            if (next < plan.Items.Count && plan.Items[next].Kind == PlanItemKind.Section)
                next++;
            items.AddRange(plan.Items.Skip(next));
        }

        return Queue(plan, action, spot, items, info.Label);
    }

    private LiveResult MoveOn(SongPlan plan, LiveAction action, Spot spot)
    {
        var item = spot.Item;
        if (item.IsOpen)
        {
            // The pass that plays is the last one: later passes of the same repeat are taken out (PB-50).
            var items = Prefix(plan, spot);
            items.AddRange(plan.Items.Skip(spot.Index + 1).Where(i => !(i.IsOpen && i.EntryId == item.EntryId)));
            return Queue(plan, action, spot, items, "the next section");
        }

        // Skipping the rest of a section that has no later pass is the same as leaving it at its end.
        if (!spot.Cuts)
            return new LiveResult(LiveOutcome.NoChange, null, "The song moves on at the end of the section.");

        var skip = Prefix(plan, spot);
        skip.AddRange(plan.Items.Skip(spot.Index + 1));
        var nextItem = plan.Items.Skip(spot.Index + 1).FirstOrDefault(i => i.Length > 0 || i.Kind == PlanItemKind.Hold);
        return Queue(plan, action, spot, skip, nextItem?.Label ?? "Ending");
    }

    private LiveResult Vamp(SongPlan plan, LiveAction action, Spot spot)
    {
        var item = spot.Item;
        if (item.IsOpen)
            return MoveOn(plan, action with { Kind = LiveActionKind.MoveOn }, spot);
        if (item.Kind != PlanItemKind.Section)
            return new LiveResult(LiveOutcome.Rejected, null, "There is no section to repeat.");

        // The section becomes the first pass of an open repeat of its own; later passes of a written repeat are replaced.
        var group = $"live:{item.Id}";
        var passes = Math.Max(2, _options.MaxPasses);
        var items = plan.Items.Take(spot.Index).ToList();
        for (var pass = 1; pass <= passes; pass++)
        {
            items.Add(item with
            {
                Id = pass == 1 ? item.Id : $"{item.Id}~{++_serial}",
                EntryId = group,
                Pass = pass,
                Passes = 0,
                IsLive = pass > 1 || item.IsLive,
                FadeInSec = pass == 1 ? item.FadeInSec : 0,
                FadeOutSec = 0,
                NominalFadeOutSec = 0,
            });
        }

        items.AddRange(plan.Items.Skip(spot.Index + 1).Where(i => !(i.EntryId == item.EntryId && i.Kind == PlanItemKind.Section && i.EntryId is not null)));
        return Queue(plan, action, spot, items, item.Label);
    }

    private LiveResult RepeatOnce(SongPlan plan, LiveAction action, Spot spot)
    {
        var item = spot.Item;
        if (item.Kind != PlanItemKind.Section)
            return new LiveResult(LiveOutcome.Rejected, null, "There is no section to repeat.");

        var items = plan.Items.Take(spot.Index + 1).ToList();
        items.AddRange(Live([item with { FadeInSec = 0 }]));
        items.AddRange(plan.Items.Skip(spot.Index + 1));
        return Queue(plan, action, spot, items, item.Label);
    }

    private LiveResult End(SongPlan plan, LiveAction action, Spot spot)
    {
        var items = Prefix(plan, spot);

        // The first Ending or Outro the plan still has, else the song's Ending, else the ring-out.
        var ending = -1;
        for (var i = spot.Index + 1; i < plan.Items.Count; i++)
        {
            var section = plan.Items[i].Section;
            if (plan.Items[i].Kind == PlanItemKind.Section && section is not null && section.Type is SectionType.Ending or SectionType.Outro)
            {
                ending = i;
                break;
            }
        }

        string target;
        if (ending >= 0)
        {
            items.AddRange(plan.Items.Skip(ending).Where(i => i.Kind != PlanItemKind.Tail));
            target = plan.Items[ending].Label;
        }
        else
        {
            var song = plan.Sections.LastOrDefault(s => s.Type is SectionType.Ending or SectionType.Outro && s.Id != spot.Item.Section?.Id);
            if (song is not null)
            {
                items.Add(PlanBuilder.LiveSection(song, $"live:{song.Id}~{++_serial}", 0, _options.PlanOptions));
                target = song.Label;
            }
            else
            {
                target = "the ring-out";
            }
        }

        if (spot.Item.Section?.Type is SectionType.Ending or SectionType.Outro && ending < 0 && !spot.Cuts)
            return new LiveResult(LiveOutcome.NoChange, null, "The song is already ending.");

        return Queue(plan, action, spot, items, target);
    }
}
