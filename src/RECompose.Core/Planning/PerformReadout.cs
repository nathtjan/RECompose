// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Timing;

namespace RECompose.Core.Planning;

/// <summary>What the NEXT card is doing (LIVE-20).</summary>
public enum NextState
{
    /// <summary>The plan goes on as it is.</summary>
    Planned,

    /// <summary>An action waits for its execute point (PB-45).</summary>
    Pending,

    /// <summary>An open repeat or vamp plays until moved on (PB-50).</summary>
    Repeating,

    /// <summary>The clock waits for GO (PB-52).</summary>
    Holding,

    /// <summary>Nothing follows.</summary>
    EndOfSong,
}

/// <summary>How a tile of the plan strip stands (UI-14).</summary>
public enum TileState
{
    Played,
    Current,
    Upcoming,

    /// <summary>The target of the pending action.</summary>
    Target,

    /// <summary>Dropped by the pending action: struck through.</summary>
    Skipped,
}

public sealed record StripTile(string Id, string Label, TileState State, string Detail = "", bool IsLive = false, bool IsHold = false);

/// <summary>The NEXT card: the label, a tag line, and when it happens (LIVE-20, LIVE-21).</summary>
public sealed record NextCard(NextState State, string Label, string Tag, string Detail, string Countdown, string Note = "");

/// <summary>Everything the Perform screen shows about the playing song at one moment (LIVE-10, LIVE-20…23, UI-14).</summary>
public sealed record PerformReadout(
    PlanReadout Now,
    NextCard Next,
    IReadOnlyList<StripTile> Strip,
    bool Holding,
    string HoldText,
    double? PendingAtSec);

public static class PerformReadouts
{
    public static string SourceTag(ActionSource source) => source switch
    {
        ActionSource.Keys => "KEYS",
        ActionSource.Pedal => "PEDAL",
        ActionSource.Voice => "VOICE",
        _ => "MANUAL",
    };

    /// <summary>"Holding for 0:23" (PB-33).</summary>
    public static string HoldText(double elapsedSec) => "Holding for " + Format.Clock(Math.Max(0, elapsedSec));

    public static PerformReadout Build(LivePlan live, double planSec, bool holding = false, double holdElapsedSec = 0)
    {
        var plan = live.Committed;
        var pending = live.Pending;
        var now = plan.At(planSec);
        if (plan.Items.Count == 0)
            return new PerformReadout(now, new NextCard(NextState.EndOfSong, "END OF SONG", plan.IsAsWritten ? "AS WRITTEN" : "ARRANGEMENT", string.Empty, string.Empty), [], false, string.Empty, null);

        var index = holding ? HoldIndex(plan, planSec) : plan.ItemIndexAt(planSec);
        var current = plan.Items[index];

        if (holding)
            now = now with { NowLabel = "HOLD", NowDetail = HoldText(holdElapsedSec), NowPass = string.Empty, SectionProgress = 0 };

        var next = BuildNext(plan, pending, now, current, index, planSec, holding);
        return new PerformReadout(now, next, Strip(live, index), holding, holding ? HoldText(holdElapsedSec) : string.Empty, pending?.ExecuteAt);
    }

    private static int HoldIndex(SongPlan plan, double planSec)
    {
        for (var i = plan.Items.Count - 1; i >= 0; i--)
        {
            if (plan.Items[i].Kind == PlanItemKind.Hold && plan.Items[i].PlanStart <= planSec + 0.002)
                return i;
        }

        return plan.ItemIndexAt(planSec);
    }

    private static NextCard BuildNext(SongPlan plan, PendingAction? pending, PlanReadout now, PlanItem current, int index, double planSec, bool holding)
    {
        var tag = plan.IsAsWritten ? "AS WRITTEN" : "ARRANGEMENT";

        // The pending action comes first: it names what really happens next (LIVE-21).
        if (pending is not null)
        {
            var label = pending.Target.ToUpperInvariant();
            var detail = pending.After is { } after ? $"then {after}" : string.Empty;
            return new NextCard(NextState.Pending, label, $"NEXT · {SourceTag(pending.Action.Source)} · PENDING", detail, plan.Countdown(planSec, pending.ExecuteAt), string.Empty);
        }

        if (holding)
        {
            var after = plan.Items.Skip(index + 1).FirstOrDefault(i => i.Kind == PlanItemKind.Section);
            return new NextCard(NextState.Holding, after?.Label.ToUpperInvariant() ?? "END OF SONG", "NEXT · GO", "waiting for GO", "on GO", after?.Note ?? string.Empty);
        }

        if (current.IsOpen)
            return new NextCard(NextState.Repeating, "REPEATING", "NEXT · REPEATING", "cue to move on", string.Empty);

        if (string.IsNullOrEmpty(now.NextLabel) || now.NextLabel == "END OF SONG")
            return new NextCard(NextState.EndOfSong, "END OF SONG", tag, string.Empty, now.Countdown, string.Empty);

        return new NextCard(NextState.Planned, now.NextLabel, tag, string.Empty, now.Countdown, now.NextNote);
    }

    // ---- The plan strip ----------------------------------------------------------------

    private static bool Shown(PlanItem item) => item.Kind is PlanItemKind.Section or PlanItemKind.Hold or PlanItemKind.Song or PlanItemKind.Click or PlanItemKind.Pad;

    private static string Detail(PlanItem item)
    {
        if (item.IsOpen)
            return "until cued";
        if (item.Passes > 1)
            return $"pass {item.Pass.ToString(CultureInfo.InvariantCulture)} of {item.Passes.ToString(CultureInfo.InvariantCulture)}";
        return item.IsPartial ? $"{item.FromBar.ToString(CultureInfo.InvariantCulture)}–{item.ToBar.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
    }

    private static IEnumerable<PlanItem> Condensed(SongPlan plan) => plan.DisplayItems.Where(Shown);

    /// <summary>
    /// One tile per plan item in play order: what has played, the current item, what is skipped and what the pending action
    /// goes to, then the rest (UI-14). An open repeat is one tile.
    /// </summary>
    private static List<StripTile> Strip(LivePlan live, int currentIndex)
    {
        var plan = live.Committed;
        var tiles = new List<StripTile>();
        var current = plan.Items[currentIndex];
        var cut = live.Pending?.ExecuteAt;
        var replacement = live.Pending is null ? null : new HashSet<string>(live.Pending.Plan.Items.Select(i => i.Id));

        bool IsCurrentGroup(PlanItem i) => i.Id == current.Id || (current.IsOpen && i.IsOpen && i.EntryId == current.EntryId);

        foreach (var item in Condensed(plan))
        {
            var state = item.PlanEnd <= current.PlanStart + 1e-6 && !IsCurrentGroup(item) && item.Kind != PlanItemKind.Hold
                ? TileState.Played
                : IsCurrentGroup(item) || (item.Kind == PlanItemKind.Hold && current.Kind == PlanItemKind.Hold && item.Id == current.Id)
                    ? TileState.Current
                    : item.PlanStart < current.PlanStart ? TileState.Played : TileState.Upcoming;

            if (cut is { } at && state == TileState.Upcoming && item.PlanStart >= at - 1e-6)
            {
                // Items the new plan keeps are shown with it, after the skipped ones.
                if (replacement!.Contains(item.Id))
                    continue;
                state = TileState.Skipped;
            }

            tiles.Add(new StripTile(item.Id, item.Label, state, Detail(item), item.IsLive, item.Kind == PlanItemKind.Hold));
        }

        if (live.Pending is { } pending)
        {
            // What takes over at the execute point follows the skipped tiles; the first of it is the target.
            var kept = new HashSet<string>(plan.Items.Select(i => i.Id));
            var rest = Condensed(pending.Plan).Where(i => i.PlanStart >= pending.ExecuteAt - 1e-6).ToList();
            var first = true;
            foreach (var item in rest)
            {
                var state = first ? TileState.Target : TileState.Upcoming;
                first = false;
                tiles.Add(new StripTile(item.Id, item.Label, state, Detail(item), item.IsLive || !kept.Contains(item.Id), item.Kind == PlanItemKind.Hold));
            }
        }

        return tiles;
    }
}
