// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Music;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Tests.Arrangements;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Planning;

// Plans with holds and open repeats (PB-41, PB-50…52, ARR-32, ARR-38, AT-13, AT-14), on fixture B after detection.
public class HoldAndOpenRepeatTests
{
    private static readonly TempoGrid B = Sections.SectionTests.B;
    private const double DurationB = Sections.SectionTests.DurationB;
    private static readonly double Bar = B.BarLength;

    private static SongPlan Plan(string order, PlanOptions? options = null)
    {
        var infos = ArrangementTests.InfosB();
        var parsed = QuickOrder.Parse(order, infos);
        Assert.False(parsed.HasProblems);
        var arrangement = new Arrangement { Id = "x", Name = "x", Entries = [.. parsed.Entries] };
        return PlanBuilder.Build(B, infos, DurationB, arrangement, null, options);
    }

    [Fact]
    public void AT13_a_hold_rings_the_section_out_then_waits_and_goes_in_on_a_count_in()
    {
        var plan = Plan("V1 HOLD C", new PlanOptions { OriginalKey = Key.Parse("E"), KeyOffset = 1 });

        Assert.Equal(
            [PlanItemKind.LeadIn, PlanItemKind.Section, PlanItemKind.Tail, PlanItemKind.Hold, PlanItemKind.CountIn, PlanItemKind.Section, PlanItemKind.Tail],
            plan.Items.Select(i => i.Kind));

        var verse = plan.Items[1];
        var tail = plan.Items[2];
        var hold = plan.Items[3];
        var countIn = plan.Items[4];
        Assert.Equal(2 * Bar, tail.Length, 6);                     // the verse rings out for two bars
        Assert.Equal(tail.PlanEnd, hold.PlanStart, 9);
        Assert.Equal(0, hold.Length);
        Assert.True(hold.HoldPad);
        Assert.False(hold.HoldClick);
        Assert.Equal(Bar, countIn.Length, 6);                      // 1 bar of click on GO (ARR-38)
        Assert.True(countIn.AfterHold);
        Assert.Equal(countIn.PlanEnd, plan.Items[5].PlanStart, 9); // then Chorus 1

        // The engine hears one hold marker at the end of the tail, with the pad key of the verse (E + 1 = F).
        var holds = plan.Holds();
        Assert.Single(holds);
        Assert.Equal(tail.PlanEnd, holds[0].AtSec, 9);
        Assert.True(holds[0].Pad);
        Assert.Equal(5, holds[0].PadKey);

        // The hold has no length: the clock and the song bars skip over it (PB-33), and the card names what comes.
        Assert.Equal("HOLD", plan.At(verse.PlanStart + 1).NextLabel);
        Assert.EndsWith("+", plan.At(0).Time);
        var segments = plan.Segments();
        for (var i = 1; i < segments.Count; i++)
            Assert.Equal(segments[i - 1].End, segments[i].Start, 9); // contiguous: the hold is not a segment
        Assert.Contains(segments, s => s.Click && s.Src is null); // the count-in clicks
    }

    [Fact]
    public void A_cut_ending_and_a_hold_without_a_count_in_leave_out_the_tail_and_the_click()
    {
        var infos = ArrangementTests.InfosB();
        var hold = ArrangementEntry.NewHold() with { Hold = new HoldOptions { Pad = false, Click = true, CountInBars = 0 } };
        var arrangement = new Arrangement
        {
            Id = "c", Name = "c",
            Entries = [ArrangementEntry.ForSection(ArrangementTests.Find(infos, "Verse 1").Id) with { Ending = EndingMode.Cut }, hold, ArrangementEntry.ForSection(ArrangementTests.Find(infos, "Chorus 1").Id)],
        };
        var plan = PlanBuilder.Build(B, infos, DurationB, arrangement);
        Assert.Equal([PlanItemKind.LeadIn, PlanItemKind.Section, PlanItemKind.Hold, PlanItemKind.Section, PlanItemKind.Tail], plan.Items.Select(i => i.Kind));
        Assert.True(plan.Items[2].HoldClick);
        Assert.Null(plan.Holds()[0].PadKey);
    }

    [Fact]
    public void AT14_an_open_repeat_is_built_as_sixteen_passes_and_counts_once()
    {
        var plan = Plan("V1 C x? E");
        var chorus = plan.Items.Where(i => i.Kind == PlanItemKind.Section && i.Label == "Chorus 1").ToList();

        Assert.Equal(16, chorus.Count);
        Assert.All(chorus, i => Assert.True(i.IsOpen));
        Assert.Equal(Enumerable.Range(1, 16), chorus.Select(i => i.Pass));
        Assert.Equal(15 * 8 * Bar, plan.ExtraOpenSec, 6);

        // Verse 1 (8 bars) + Chorus 1 once (8) + Outro-less Ending: the total counts the chorus once.
        var once = Plan("V1 C E");
        Assert.Equal(once.TotalBars, plan.TotalBars);
        Assert.Equal(once.HeardDurationSec, plan.HeardDurationSec, 6);

        // In the second pass the song bar and the remaining time are those of the first pass (PB-32, PB-33).
        var first = plan.At(chorus[0].PlanStart + 2 * Bar);
        var second = plan.At(chorus[1].PlanStart + 2 * Bar);
        Assert.Equal(first.SongBar, second.SongBar);
        Assert.Equal(first.Time[(first.Time.IndexOf('/') + 1)..], second.Time[(second.Time.IndexOf('/') + 1)..]); // the same remaining time
        Assert.Equal("until cued", second.NowPass);
        Assert.EndsWith("+", second.Time);

        // Everything after the repeat sits after all sixteen passes in engine time.
        var ending = plan.Items.Single(i => i.Label == "Ending" && i.Kind == PlanItemKind.Section);
        Assert.Equal(chorus[^1].PlanEnd, ending.PlanStart, 6);
    }

    [Fact]
    public void The_screen_sees_an_open_repeat_as_one_block_on_the_effective_clock()
    {
        var plan = Plan("V1 C x? E");
        var shown = plan.DisplayItems;

        Assert.Equal(plan.Items.Count - 15, shown.Count);
        Assert.Single(shown, i => i.Label == "Chorus 1");
        var chorus = shown.Single(i => i.Label == "Chorus 1");
        var ending = shown.Single(i => i.Label == "Ending" && i.Kind == PlanItemKind.Section);
        Assert.Equal(chorus.PlanEnd, ending.PlanStart, 6);                     // no gap where the other passes were
        Assert.Equal(plan.ExpectedDurationSec, shown[^1].PlanEnd, 6);

        // Seeking to a place on the screen goes to the first pass; the way back is the same place.
        var eff = chorus.PlanStart + 3 * Bar;
        var seek = plan.FromEffective(eff);
        Assert.Equal(plan.Items.First(i => i.Label == "Chorus 1").PlanStart + 3 * Bar, seek, 6);
        Assert.Equal(eff, plan.EffectiveSec(seek), 6);

        // After the repeat the two clocks differ by the extra passes.
        var later = plan.FromEffective(ending.PlanStart + Bar);
        Assert.Equal(ending.PlanStart + Bar + plan.ExtraOpenSec, later, 6);
    }

    [Fact]
    public void An_open_repeat_honours_its_own_maximum_of_passes()
    {
        var infos = ArrangementTests.InfosB();
        var arrangement = new Arrangement
        {
            Id = "m", Name = "m",
            Entries = [ArrangementEntry.ForSection(ArrangementTests.Find(infos, "Chorus 1").Id) with { Repeat = new RepeatSpec { UntilCued = true, MaxPasses = 3 } }],
        };
        var plan = PlanBuilder.Build(B, infos, DurationB, arrangement);
        Assert.Equal(3, plan.Items.Count(i => i.Kind == PlanItemKind.Section));
    }
}
