// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Planning;
using RECompose.Core.Tests.Arrangements;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Planning;

// The Perform screen's readouts (LIVE-10, LIVE-20…23, PB-33, UI-14), on fixture B.
public class PerformReadoutTests
{
    private static readonly TempoGrid B = Sections.SectionTests.B;
    private static readonly double Bar = B.BarLength;

    private static LivePlan Live(string order)
    {
        var infos = ArrangementTests.InfosB();
        var parsed = QuickOrder.Parse(order, infos);
        var arrangement = new Arrangement { Id = "x", Name = "x", Entries = [.. parsed.Entries] };
        return new LivePlan(PlanBuilder.Build(B, infos, Sections.SectionTests.DurationB, arrangement));
    }

    [Fact]
    public void LIVE20_the_planned_next_card_names_the_next_item_and_the_tag()
    {
        var live = Live("V1 C V2");
        var verse = live.Committed.Items.First(i => i.Label == "Verse 1");

        var read = PerformReadouts.Build(live, verse.PlanStart + Bar);

        Assert.Equal(NextState.Planned, read.Next.State);
        Assert.Equal("CHORUS 1", read.Next.Label);
        Assert.Equal("ARRANGEMENT", read.Next.Tag);
        Assert.Equal("VERSE 1", read.Now.NowLabel);
        Assert.Equal(["Verse 1", "Chorus 1", "Verse 2"], read.Strip.Where(t => !t.IsHold).Select(t => t.Label));
        Assert.Equal([TileState.Current, TileState.Upcoming, TileState.Upcoming], read.Strip.Select(t => t.State));
    }

    [Fact]
    public void LIVE21_a_pending_jump_turns_the_card_and_marks_target_and_skipped_tiles()
    {
        var live = Live("V1 C V2 C E");
        var plan = live.Committed;
        var verse = plan.Items.First(i => i.Label == "Verse 1");
        var ending = plan.Items.Single(i => i.Label == "Ending" && i.Kind == PlanItemKind.Section);
        live.Apply(new LiveAction(LiveActionKind.JumpToItem, ItemId: ending.Id, Source: ActionSource.Keys), verse.PlanStart + Bar);

        var read = PerformReadouts.Build(live, verse.PlanStart + Bar);

        Assert.Equal(NextState.Pending, read.Next.State);
        Assert.Equal("ENDING", read.Next.Label);
        Assert.Equal("NEXT · KEYS · PENDING", read.Next.Tag);
        Assert.Equal(verse.PlanEnd, read.PendingAtSec);
        Assert.Equal(
            [("Verse 1", TileState.Current), ("Chorus 1", TileState.Skipped), ("Verse 2", TileState.Skipped), ("Chorus 2", TileState.Skipped), ("Ending", TileState.Target)],
            read.Strip.Select(t => (t.Label, t.State)));

        live.Cancel();
        Assert.Equal(NextState.Planned, PerformReadouts.Build(live, verse.PlanStart + Bar).Next.State);
    }

    [Fact]
    public void LIVE22_an_open_repeat_reads_repeating_until_moved_on_and_is_one_tile()
    {
        var live = Live("V1 C x? E");
        var chorus = live.Committed.Items.Where(i => i.Label == "Chorus 1").ElementAt(1);

        var read = PerformReadouts.Build(live, chorus.PlanStart + 1);

        Assert.Equal(NextState.Repeating, read.Next.State);
        Assert.Equal("cue to move on", read.Next.Detail);
        Assert.Single(read.Strip, t => t.Label == "Chorus 1");
        Assert.Equal(TileState.Current, read.Strip.Single(t => t.Label == "Chorus 1").State);
        Assert.Equal("until cued", read.Strip.Single(t => t.Label == "Chorus 1").Detail);
    }

    [Fact]
    public void PB33_a_hold_shows_the_timer_and_the_section_that_follows()
    {
        var live = Live("V1 HOLD C");
        var hold = live.Committed.Items.Single(i => i.Kind == PlanItemKind.Hold);

        var read = PerformReadouts.Build(live, hold.PlanStart, holding: true, holdElapsedSec: 23.4);

        Assert.True(read.Holding);
        Assert.Equal("Holding for 0:23", read.HoldText);
        Assert.Equal("HOLD", read.Now.NowLabel);
        Assert.Equal(NextState.Holding, read.Next.State);
        Assert.Equal("CHORUS 1", read.Next.Label);
        Assert.Equal(TileState.Current, read.Strip.Single(t => t.IsHold).State);
        Assert.Equal(TileState.Played, read.Strip.First(t => t.Label == "Verse 1").State);
    }

    [Fact]
    public void The_last_item_has_no_next_and_a_vamp_in_the_plan_is_a_live_tile()
    {
        var live = Live("V1 C");
        var chorus = live.Committed.Items.First(i => i.Label == "Chorus 1");
        Assert.Equal(NextState.Planned, PerformReadouts.Build(live, chorus.PlanStart + 1).Next.State);

        live.Apply(new LiveAction(LiveActionKind.RepeatOnce), chorus.PlanStart + 1);
        var read = PerformReadouts.Build(live, chorus.PlanStart + 1);
        Assert.Contains(read.Strip, t => t.IsLive && t.State == TileState.Target);
    }
}
