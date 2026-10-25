// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Persistence;
using RECompose.Core.Planning;
using RECompose.Core.Recovery;
using RECompose.Core.Tests.Arrangements;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Recovery;

// The journal and the resume rules (REL-30…33, DM-80, AT-44), on fixture B.
public class JournalTests : IDisposable
{
    private static readonly TempoGrid B = Sections.SectionTests.B;
    private const double DurationB = Sections.SectionTests.DurationB;
    private static readonly double Bar = B.BarLength;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-journal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static LivePlan Live(string order)
    {
        var infos = ArrangementTests.InfosB();
        var parsed = QuickOrder.Parse(order, infos);
        var arrangement = new Arrangement { Id = "x", Name = "x", Entries = [.. parsed.Entries] };
        return new LivePlan(PlanBuilder.Build(B, infos, DurationB, arrangement));
    }

    [Fact]
    public void AT44_a_plan_with_live_changes_survives_the_journal_unchanged()
    {
        var live = Live("V1 HOLD C V2 C E");
        var verse = live.Committed.Items.First(i => i.Label == "Verse 1");
        live.Apply(new LiveAction(LiveActionKind.Loop), verse.PlanStart + 1);   // a loop with count-ins
        live.Tick(verse.PlanEnd);
        var plan = live.Committed;

        var store = new DataStore(_root);
        store.SaveJournal(Journal.From(plan, 10, "playing", "champion", "Champion") with { ServiceName = "Sunday", Lock = "service", Perform = true, PadKey = 5 });
        var read = store.LoadJournal()!;

        Assert.Equal("Champion", read.SongTitle);
        Assert.True(read.WasPlaying && read.Perform);
        Assert.Equal(5, read.PadKey);

        var restored = read.ToPlan(B, plan.Sections, DurationB); // the song's own sections, as the app has them
        Assert.Equal(plan.Items.Count, restored.Items.Count);
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var a = plan.Items[i];
            var b = restored.Items[i];
            Assert.Equal((a.Id, a.Kind, a.Label, a.Pass, a.Passes, a.IsLive, a.HoldPad, a.AfterHold), (b.Id, b.Kind, b.Label, b.Pass, b.Passes, b.IsLive, b.HoldPad, b.AfterHold));
            Assert.Equal(a.PlanStart, b.PlanStart, 9);
            Assert.Equal(a.PlanEnd, b.PlanEnd, 9);
            Assert.Equal(a.SrcStart, b.SrcStart, 9);
            Assert.Equal(a.Section?.Id, b.Section?.Id);
        }

        Assert.Equal(plan.ExpectedDurationSec, restored.ExpectedDurationSec, 6);
        Assert.Equal(plan.Holds().Count, restored.Holds().Count);
        Assert.Equal(plan.TotalBars, restored.TotalBars);
    }

    [Fact]
    public void A_damaged_journal_reads_as_missing_and_the_state_defaults_to_clean()
    {
        var store = new DataStore(_root);
        Assert.Null(store.LoadJournal());
        Assert.True(store.LoadState().CleanShutdown);

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "journal.json"), "{ not json");
        Assert.Null(store.LoadJournal());

        store.SaveState(new AppState { CleanShutdown = false, Mode = "perform", Lock = "service" });
        Assert.False(store.LoadState().CleanShutdown);
        store.SaveJournal(Journal.From(Live("V1 C").Committed, 0, "stopped", "s", "S"));
        store.DeleteJournal();
        Assert.Null(store.LoadJournal());
    }

    [Fact]
    public void REL31_the_resume_targets_are_the_section_that_played_and_the_one_after_it()
    {
        var plan = Live("V1 C V2 C E").Committed;
        var chorus = plan.Items.First(i => i.Label == "Chorus 1");

        var current = ResumeRules.Current(plan, chorus.PlanStart + 4 * Bar + 0.1)!;
        Assert.Equal("Chorus 1", current.SectionName);
        Assert.Equal(chorus.PlanStart, current.PlanSec, 9);

        var next = ResumeRules.Next(plan, chorus.PlanStart + 4 * Bar + 0.1)!;
        Assert.Equal("Verse 2", next.SectionName);

        // In the last section there is no next one.
        var ending = plan.Items.Last(i => i.Kind == PlanItemKind.Section);
        Assert.Null(ResumeRules.Next(plan, ending.PlanStart + 1));
    }

    [Fact]
    public void REL32_the_resume_plan_has_a_bar_of_click_in_front_of_the_chosen_section()
    {
        var plan = Live("V1 C V2 C E").Committed;
        var chorus = plan.Items.First(i => i.Label == "Chorus 1");
        var target = ResumeRules.Current(plan, chorus.PlanStart + 1)!;

        var (resumed, start) = ResumeRules.WithCountIn(plan, target, 1, new PlanOptions())!.Value;

        var count = resumed.Items.Single(i => i.Kind == PlanItemKind.CountIn && i.Id.StartsWith("resume-count-in", StringComparison.Ordinal));
        Assert.Equal(start, count.PlanStart, 9);
        Assert.Equal(Bar, count.Length, 6);
        var index = resumed.Items.Select((item, i) => (item, i)).First(x => x.item.Id == count.Id).i;
        Assert.Equal("Chorus 1", resumed.Items[index + 1].Label);
        Assert.Equal(count.PlanEnd, resumed.Items[index + 1].PlanStart, 9);
        for (var i = 1; i < resumed.Items.Count; i++)
            Assert.Equal(resumed.Items[i - 1].PlanEnd, resumed.Items[i].PlanStart, 9);
        Assert.Equal(plan.Items.Count(i => i.Kind == PlanItemKind.Section), resumed.Items.Count(i => i.Kind == PlanItemKind.Section));
    }
}
