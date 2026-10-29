// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Planning;
using RECompose.Core.Tests.Arrangements;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Planning;

// Live changes as plan rewrites (PB-44, PB-45, PB-47, PB-50…52, AT-20…23, AT-27), on fixture B.
public class LivePlanTests
{
    private static readonly TempoGrid B = Sections.SectionTests.B;
    private const double DurationB = Sections.SectionTests.DurationB;
    private static readonly double Bar = B.BarLength;

    private static SongPlan Build(string order, PlanOptions? options = null)
    {
        var infos = ArrangementTests.InfosB();
        var parsed = QuickOrder.Parse(order, infos);
        Assert.False(parsed.HasProblems);
        var arrangement = new Arrangement { Id = "x", Name = "x", Entries = [.. parsed.Entries] };
        return PlanBuilder.Build(B, infos, DurationB, arrangement, null, options);
    }

    private static LivePlan Live(string order, bool continueAsWritten = false) =>
        new(Build(order), new LiveOptions { ContinueAsWritten = continueAsWritten });

    private static IEnumerable<string> Labels(SongPlan plan) =>
        plan.Items.Where(i => i.Kind == PlanItemKind.Section).Select(i => i.Label);

    private static void AssertContiguous(SongPlan plan)
    {
        for (var i = 1; i < plan.Items.Count; i++)
            Assert.Equal(plan.Items[i - 1].PlanEnd, plan.Items[i].PlanStart, 9);
    }

    [Fact]
    public void AT21_a_jump_to_a_later_section_skips_what_is_between_at_the_end_of_the_section()
    {
        var live = Live("V1 C V2 C E");
        var plan = live.Committed;
        var verse1 = plan.Items.First(i => i.Label == "Verse 1");
        var now = verse1.PlanStart + Bar;
        var ending = plan.Items.Single(i => i.Label == "Ending" && i.Kind == PlanItemKind.Section);

        var result = live.Apply(new LiveAction(LiveActionKind.JumpToItem, ItemId: ending.Id), now);

        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.Equal(verse1.PlanEnd, result.Pending!.ExecuteAt, 9);
        Assert.Equal("Ending", result.Pending.Target);
        Assert.Equal(["Verse 1", "Ending"], Labels(live.Plan));
        AssertContiguous(live.Plan);

        // Nothing happens before the execute point; at it the new plan is the plan (PB-45).
        Assert.Same(plan, live.Committed);
        Assert.False(live.Tick(now));
        Assert.True(live.Tick(verse1.PlanEnd));
        Assert.Null(live.Pending);
        Assert.Equal(["Verse 1", "Ending"], Labels(live.Committed));
    }

    [Fact]
    public void A_jump_to_the_section_that_plays_next_changes_nothing()
    {
        var live = Live("V1 C V2");
        var plan = live.Committed;
        var chorus = plan.Items.First(i => i.Label == "Chorus 1");

        var result = live.Apply(new LiveAction(LiveActionKind.JumpToItem, ItemId: chorus.Id), plan.Items.First(i => i.Label == "Verse 1").PlanStart + 1);

        Assert.Equal(LiveOutcome.AlreadyNext, result.Outcome);
        Assert.Null(live.Pending);
    }

    [Fact]
    public void AT21_a_jump_back_plays_the_section_again_and_goes_on()
    {
        var live = Live("V1 C V2");
        var plan = live.Committed;
        var chorus = plan.Items.First(i => i.Label == "Chorus 1");
        var verse1 = plan.Items.First(i => i.Label == "Verse 1");

        var result = live.Apply(new LiveAction(LiveActionKind.JumpToItem, JumpTiming.EndOfSection, verse1.Id), chorus.PlanStart + 1);

        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.Equal(["Verse 1", "Chorus 1", "Verse 1", "Chorus 1", "Verse 2"], Labels(live.Plan));
        Assert.True(live.Plan.Items.Where(i => i.Label == "Verse 1").Skip(1).All(i => i.IsLive));
        Assert.Equal(live.Plan.Items.Count, live.Plan.Items.Select(i => i.Id).Distinct().Count());
        AssertContiguous(live.Plan);
    }

    [Fact]
    public void A_jump_on_the_next_bar_cuts_the_section_and_keeps_the_grid()
    {
        var live = Live("V1 C V2 C E");
        var plan = live.Committed;
        var verse1 = plan.Items.First(i => i.Label == "Verse 1");
        var target = plan.Items.First(i => i.Label == "Verse 2");
        var now = verse1.PlanStart + 2.4 * Bar;

        var result = live.Apply(new LiveAction(LiveActionKind.JumpToItem, JumpTiming.NextBar, target.Id), now);

        Assert.Equal(verse1.PlanStart + 3 * Bar, result.Pending!.ExecuteAt, 6);
        var cut = live.Plan.Items.First(i => i.Label == "Verse 1");
        Assert.Equal(3 * Bar, cut.Length, 6);
        Assert.Equal(3, cut.LengthBars);
        Assert.True(cut.IsPartial);
        Assert.Equal(["Verse 1", "Verse 2", "Chorus 2", "Ending"], Labels(live.Plan));
        AssertContiguous(live.Plan);
    }

    [Fact]
    public void A_target_under_thirty_milliseconds_away_waits_for_the_next_point()
    {
        var live = Live("V1 C V2 C E");
        var plan = live.Committed;
        var verse1 = plan.Items.First(i => i.Label == "Verse 1");
        var ending = plan.Items.Single(i => i.Label == "Ending" && i.Kind == PlanItemKind.Section);

        var result = live.Apply(new LiveAction(LiveActionKind.JumpToItem, ItemId: ending.Id), verse1.PlanEnd - 0.01);

        // The end of the verse is too close: the jump leaves the chorus after it instead.
        Assert.Equal(plan.Items.First(i => i.Label == "Chorus 1").PlanEnd, result.Pending!.ExecuteAt, 9);
        Assert.Equal(["Verse 1", "Chorus 1", "Ending"], Labels(live.Plan));
    }

    [Fact]
    public void AT22_a_section_the_plan_does_not_play_again_replaces_what_plays_next()
    {
        var live = Live("V1 C V2 C E");
        var plan = live.Committed;
        var tag = plan.Sections.First(s => s.Label == "Tag");
        var chorus = plan.Items.First(i => i.Label == "Chorus 1");

        var result = live.Apply(new LiveAction(LiveActionKind.JumpToSection, SectionId: tag.Id), chorus.PlanStart + 1);

        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.Equal(["Verse 1", "Chorus 1", "Tag", "Chorus 2", "Ending"], Labels(live.Plan));
        Assert.True(live.Plan.Items.Single(i => i.Label == "Tag").IsLive);
        Assert.Equal(tag.EndSec - tag.StartSec, live.Plan.Items.Single(i => i.Label == "Tag").Length, 6);
        AssertContiguous(live.Plan);
    }

    [Fact]
    public void A_section_jump_can_carry_on_as_written_instead()
    {
        var live = Live("V1 C V2 C E", continueAsWritten: true);
        var plan = live.Committed;
        var tag = plan.Sections.First(s => s.Label == "Tag");
        var chorus = plan.Items.First(i => i.Label == "Chorus 1");

        live.Apply(new LiveAction(LiveActionKind.JumpToSection, SectionId: tag.Id), chorus.PlanStart + 1);

        var after = Labels(live.Plan).SkipWhile(l => l != "Tag").Skip(1).ToList();
        Assert.Equal(plan.Sections.Where(s => s.Index > tag.Index).Select(s => s.Label), after);
    }

    [Fact]
    public void AT23_moving_on_ends_an_open_repeat_at_the_end_of_the_pass()
    {
        var live = Live("V1 C x? E");
        var plan = live.Committed;
        var pass3 = plan.Items.Where(i => i.Label == "Chorus 1").ElementAt(2);

        var result = live.Apply(new LiveAction(LiveActionKind.MoveOn), pass3.PlanStart + 2 * Bar);

        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.Equal(pass3.PlanEnd, result.Pending!.ExecuteAt, 9);
        Assert.Equal(3, live.Plan.Items.Count(i => i.Label == "Chorus 1"));
        Assert.Equal(["Verse 1", "Chorus 1", "Chorus 1", "Chorus 1", "Ending"], Labels(live.Plan));
        AssertContiguous(live.Plan);

        // The last pass carries the fade-out the entry asks for; the ones before it do not.
        var chorus = live.Plan.Items.Where(i => i.Label == "Chorus 1").ToList();
        Assert.All(chorus.Take(2), i => Assert.Equal(0, i.FadeOutSec));
    }

    [Fact]
    public void Moving_on_during_a_hold_releases_it_without_changing_the_plan()
    {
        var live = Live("V1 HOLD C");
        var hold = live.Committed.Items.Single(i => i.Kind == PlanItemKind.Hold);

        var result = live.Apply(new LiveAction(LiveActionKind.MoveOn), hold.PlanStart, holding: true);

        Assert.Equal(LiveOutcome.ReleaseHold, result.Outcome);
        Assert.Null(live.Pending);
    }

    [Fact]
    public void A_jump_during_a_hold_leaves_it_and_plays_the_target()
    {
        var live = Live("V1 HOLD C V2");
        var plan = live.Committed;
        var hold = plan.Items.Single(i => i.Kind == PlanItemKind.Hold);
        var verse2 = plan.Items.First(i => i.Label == "Verse 2");

        var result = live.Apply(new LiveAction(LiveActionKind.JumpToItem, ItemId: verse2.Id), hold.PlanStart, holding: true);

        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.Equal(hold.PlanStart, result.Pending!.ExecuteAt, 9);
        Assert.DoesNotContain(live.Plan.Items, i => i.Kind == PlanItemKind.Hold);
        Assert.Equal(["Verse 1", "Verse 2"], Labels(live.Plan));
    }

    [Fact]
    public void AT27_a_vamp_repeats_the_section_until_moved_on_and_moving_on_ends_it()
    {
        var live = Live("V1 C V2");
        var plan = live.Committed;
        var chorus = plan.Items.First(i => i.Label == "Chorus 1");
        var now = chorus.PlanStart + Bar;

        var result = live.Apply(new LiveAction(LiveActionKind.Vamp), now);

        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.Equal(chorus.PlanEnd, result.Pending!.ExecuteAt, 9);
        var passes = live.Plan.Items.Where(i => i.Label == "Chorus 1").ToList();
        Assert.Equal(16, passes.Count);
        Assert.All(passes, i => Assert.True(i.IsOpen));
        Assert.Equal(Enumerable.Range(1, 16), passes.Select(i => i.Pass));
        Assert.Equal(15 * chorus.Length, live.Plan.ExtraOpenSec, 6);   // counts once (PB-33)
        Assert.Equal(plan.ExpectedDurationSec, live.Plan.ExpectedDurationSec, 6);
        AssertContiguous(live.Plan);

        // Once the vamp plays, moving on in a later pass ends it at that pass.
        live.Tick(chorus.PlanEnd);
        var second = live.Plan.Items.Where(i => i.Label == "Chorus 1").ElementAt(1);
        var moveOn = live.Apply(new LiveAction(LiveActionKind.MoveOn), second.PlanStart + 1);
        Assert.Equal(LiveOutcome.Queued, moveOn.Outcome);
        Assert.Equal(2, live.Plan.Items.Count(i => i.Label == "Chorus 1"));
        Assert.Equal(["Verse 1", "Chorus 1", "Chorus 1", "Verse 2"], Labels(live.Plan));
    }

    [Fact]
    public void Vamping_a_vamp_moves_on()
    {
        var live = Live("V1 C x?");
        var chorus = live.Committed.Items.First(i => i.Label == "Chorus 1");

        var result = live.Apply(new LiveAction(LiveActionKind.Vamp), chorus.PlanStart + 1);

        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.Equal(1, live.Plan.Items.Count(i => i.Label == "Chorus 1"));
    }

    [Fact]
    public void Repeat_once_plays_the_current_section_a_second_time_and_goes_on()
    {
        var live = Live("V1 C V2");
        var chorus = live.Committed.Items.First(i => i.Label == "Chorus 1");

        var result = live.Apply(new LiveAction(LiveActionKind.RepeatOnce), chorus.PlanStart + 1);

        Assert.Equal(chorus.PlanEnd, result.Pending!.ExecuteAt, 9);
        Assert.Equal(["Verse 1", "Chorus 1", "Chorus 1", "Verse 2"], Labels(live.Plan));
        Assert.Single(live.Plan.Items, i => i.Label == "Chorus 1" && i.IsLive);
        AssertContiguous(live.Plan);
    }

    [Fact]
    public void End_goes_to_the_ending_the_plan_still_has_or_the_songs_own()
    {
        var live = Live("V1 C V2 C E");
        var verse1 = live.Committed.Items.First(i => i.Label == "Verse 1");

        var result = live.Apply(new LiveAction(LiveActionKind.End), verse1.PlanStart + 1);

        Assert.Equal("Ending", result.Pending!.Target);
        Assert.Equal(["Verse 1", "Ending"], Labels(live.Plan));

        // A plan without an ending takes the song's Ending section.
        var plain = Live("V1 C V2");
        var chorus = plain.Committed.Items.First(i => i.Label == "Chorus 1");
        var toEnd = plain.Apply(new LiveAction(LiveActionKind.End), chorus.PlanStart + 1);
        Assert.Equal(LiveOutcome.Queued, toEnd.Outcome);
        Assert.Equal(["Verse 1", "Chorus 1", "Ending"], Labels(plain.Plan));
    }

    [Fact]
    public void A_new_action_replaces_the_pending_one_and_cancel_restores_the_plan()
    {
        var live = Live("V1 C V2 C E");
        var plan = live.Committed;
        var verse1 = plan.Items.First(i => i.Label == "Verse 1");
        var ending = plan.Items.Single(i => i.Label == "Ending" && i.Kind == PlanItemKind.Section);

        live.Apply(new LiveAction(LiveActionKind.JumpToItem, ItemId: ending.Id), verse1.PlanStart + 1);
        live.Apply(new LiveAction(LiveActionKind.RepeatOnce), verse1.PlanStart + 1);

        Assert.Equal(["Verse 1", "Verse 1", "Chorus 1", "Verse 2", "Chorus 2", "Ending"], Labels(live.Plan));
        Assert.True(live.Cancel());
        Assert.Same(plan, live.Plan);
        Assert.False(live.Cancel());
    }

    [Fact]
    public void While_the_song_rings_out_there_is_nothing_to_change()
    {
        var live = Live("V1 C");
        var tail = live.Committed.Items.Last(i => i.Kind == PlanItemKind.Tail);

        var result = live.Apply(new LiveAction(LiveActionKind.RepeatOnce), tail.PlanStart + 0.1);

        Assert.Equal(LiveOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void A_change_keeps_the_ring_out_at_the_end_and_before_holds()
    {
        var live = Live("V1 C HOLD V2");
        var plan = live.Committed;
        var verse1 = plan.Items.First(i => i.Label == "Verse 1");

        live.Apply(new LiveAction(LiveActionKind.RepeatOnce), verse1.PlanStart + 1);

        var kinds = live.Plan.Items.Select(i => i.Kind).ToList();
        Assert.Equal(PlanItemKind.Tail, live.Plan.Items[kinds.IndexOf(PlanItemKind.Hold) - 1].Kind);
        Assert.Equal(PlanItemKind.Tail, kinds[^1]);
        AssertContiguous(live.Plan);
    }

    [Fact]
    public void AT16_a_loop_puts_a_bar_of_click_between_passes_counts_once_and_move_on_ends_it()
    {
        var live = Live("V1 C V2");
        var chorus = live.Committed.Items.First(i => i.Label == "Chorus 1");

        var result = live.Apply(new LiveAction(LiveActionKind.Loop), chorus.PlanStart + 1);
        Assert.Equal(LiveOutcome.Queued, result.Outcome);

        var items = live.Plan.Items;
        var firstPass = items.Select((item, index) => (item, index)).First(x => x.item.Label == "Chorus 1").index;
        Assert.Equal(PlanItemKind.CountIn, items[firstPass + 1].Kind);
        Assert.Equal(Bar, items[firstPass + 1].Length, 6);                // one bar of click between passes
        Assert.Equal("Chorus 1", items[firstPass + 2].Label);
        Assert.True(items[firstPass + 1].IsOpen && items[firstPass + 1].Pass == 2);
        Assert.Equal(16, items.Count(i => i.Label == "Chorus 1"));
        Assert.Equal(15, items.Count(i => i.Kind == PlanItemKind.CountIn && i.IsLive));
        AssertContiguous(live.Plan);

        // The screen sees one block; the announced length is unchanged.
        Assert.Single(live.Plan.DisplayItems, i => i.Label == "Chorus 1");
        Assert.DoesNotContain(live.Plan.DisplayItems, i => i.Kind == PlanItemKind.CountIn);
        Assert.Equal(live.Committed.ExpectedDurationSec, live.Plan.ExpectedDurationSec, 6);

        // Moving on during the second pass ends the loop at the end of that pass.
        live.Tick(chorus.PlanEnd);
        var second = live.Plan.Items.Where(i => i.Label == "Chorus 1").ElementAt(1);
        var move = live.Apply(new LiveAction(LiveActionKind.MoveOn), second.PlanStart + 1);
        Assert.Equal(LiveOutcome.Queued, move.Outcome);
        Assert.Equal(2, live.Plan.Items.Count(i => i.Label == "Chorus 1"));
        Assert.Equal(1, live.Plan.Items.Count(i => i.Kind == PlanItemKind.CountIn));
        Assert.Equal(["Verse 1", "Chorus 1", "Chorus 1", "Verse 2"], Labels(live.Plan));
    }

    [Fact]
    public void ARR90_a_plan_that_adds_to_the_end_is_the_same_as_the_old_one_up_to_the_end_of_what_plays()
    {
        // The same entries before and after the edit, as an edit of the arrangement keeps them.
        var infos = ArrangementTests.InfosB();
        var entries = QuickOrder.Parse("V1 C V2", infos).Entries;
        SongPlan Plan(IEnumerable<ArrangementEntry> list, PlanOptions? options = null) =>
            PlanBuilder.Build(B, infos, DurationB, new Arrangement { Id = "x", Name = "x", Entries = [.. list] }, null, options);

        var old = Plan(entries.Take(2));
        var grown = Plan(entries);
        var verse = old.Items.First(i => i.Label == "Verse 1");
        var chorus = old.Items.First(i => i.Label == "Chorus 1");

        Assert.True(grown.SamePrefix(old, verse.PlanEnd));
        Assert.True(grown.SamePrefix(old, chorus.PlanEnd));
        Assert.False(Plan(entries.Reverse()).SamePrefix(old, chorus.PlanEnd));                       // another order from the start
        Assert.False(Plan(entries, new PlanOptions { TempoFactor = 0.9 }).SamePrefix(old, chorus.PlanEnd)); // another tempo
    }
}
