// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Timing;
using RECompose.Core.Voice;

namespace RECompose.Core.Tests.Voice;

// Voice understanding (VC-12, VC-20, VC-30, VC-35, VC-36, VC-40, VC-45, VC-60, VC-70) on fixture A (Champion).
public class VoiceTests
{
    private static readonly TempoGrid A = new(72, 3, 0.02);
    private const double DurationA = 310.02;

    private static List<SectionInfo> Infos(List<Section>? sections = null) =>
        SectionList.Describe(sections ?? Sections.SectionTests.FixtureASections(), A, DurationA);

    private static SongPlan AsWritten(List<SectionInfo> infos) => PlanBuilder.Build(A, infos, DurationA, null);

    // The demo variant: Intro, then Chorus 1 twice.
    private static SongPlan Demo(List<SectionInfo> infos) => PlanBuilder.Build(A, infos, DurationA, new Arrangement
    {
        Id = "demo",
        Name = "Demo",
        Entries =
        [
            ArrangementEntry.ForSection(infos.Single(i => i.Label == "Intro").Id),
            ArrangementEntry.ForSection(infos.Single(i => i.Label == "Chorus 1").Id) with { Repeat = new RepeatSpec { Count = 2 } },
        ],
    });

    private static double Inside(SongPlan plan, string label, int pass = 1) =>
        plan.Items.First(i => i.Label == label && i.Pass == pass && i.Kind == PlanItemKind.Section).PlanStart + 1;

    private static VoiceResolution One(string heard, SongPlan plan, double now)
    {
        var commands = VoiceGrammar.Parse(heard);
        Assert.Single(commands);
        return VoiceResolver.Resolve(commands[0], plan, now);
    }

    [Fact]
    public void VC12_normalising_drops_punctuation_and_apostrophes()
    {
        Assert.Equal("let s go to the bridge", VoiceText.Normalize("  Let's go to the BRIDGE! "));
        Assert.Equal("verse two next", VoiceText.Normalize("Verse two, next."));
        Assert.Equal("Verse two", VoiceText.Spoken("Verse 2"));
    }

    [Theory]
    [InlineData("let's go to the bridge", VoiceIntent.Jump)]
    [InlineData("skip to verse two", VoiceIntent.Jump)]
    [InlineData("let's go straight into the chorus", VoiceIntent.Jump)]
    [InlineData("back to the chorus", VoiceIntent.Jump)]
    [InlineData("chorus next", VoiceIntent.Jump)]
    [InlineData("Bridge!", VoiceIntent.Jump)]
    [InlineData("skip the bridge", VoiceIntent.Skip)]
    [InlineData("skip that", VoiceIntent.Skip)]
    [InlineData("skip next", VoiceIntent.Skip)]
    [InlineData("go", VoiceIntent.MoveOn)]
    [InlineData("let's move on", VoiceIntent.MoveOn)]
    [InlineData("keep moving", VoiceIntent.MoveOn)]
    [InlineData("out of this", VoiceIntent.MoveOn)]
    [InlineData("one more time", VoiceIntent.Repeat)]
    [InlineData("again", VoiceIntent.Repeat)]
    [InlineData("chorus again", VoiceIntent.Repeat)]
    [InlineData("stay here", VoiceIntent.Vamp)]
    [InlineData("stay in this section", VoiceIntent.Vamp)]
    [InlineData("vamp", VoiceIntent.Vamp)]
    [InlineData("loop this", VoiceIntent.Vamp)]
    [InlineData("hold here", VoiceIntent.Vamp)]
    [InlineData("let's end", VoiceIntent.End)]
    [InlineData("end it here", VoiceIntent.End)]
    [InlineData("take it to the end", VoiceIntent.End)]
    [InlineData("wrap it up", VoiceIntent.End)]
    [InlineData("last time", VoiceIntent.End)]
    [InlineData("never mind", VoiceIntent.Cancel)]
    [InlineData("cancel", VoiceIntent.Cancel)]
    [InlineData("scratch that", VoiceIntent.Cancel)]
    [InlineData("keep going", VoiceIntent.Cancel)]
    [InlineData("as you were", VoiceIntent.Cancel)]
    [InlineData("yes", VoiceIntent.Confirm)]
    [InlineData("do it", VoiceIntent.Confirm)]
    [InlineData("stop the tracks", VoiceIntent.Stop)]
    [InlineData("kill the tracks", VoiceIntent.Stop)]
    public void VC20_each_intent_is_understood(string heard, VoiceIntent intent)
    {
        var command = Assert.Single(VoiceGrammar.Parse(heard));
        Assert.Equal(intent, command.Intent);
    }

    [Fact]
    public void VC20_fillers_and_bare_stop_do_nothing_and_commands_read_left_to_right()
    {
        Assert.Empty(VoiceGrammar.Parse("hey guys okay please"));
        Assert.Empty(VoiceGrammar.Parse("stop"));
        Assert.Empty(VoiceGrammar.Parse("bridge", acceptBareNames: false));
        Assert.Equal([VoiceIntent.Cancel, VoiceIntent.Vamp], VoiceGrammar.Parse("Never mind, let's stay in this section").Select(c => c.Intent));
        Assert.Equal([VoiceIntent.Cancel, VoiceIntent.MoveOn], VoiceGrammar.Parse("cancel that, move on").Select(c => c.Intent));
    }

    [Theory]
    [InlineData("go to the bridge", JumpTiming.EndOfSection)]
    [InlineData("go to the bridge after this", JumpTiming.EndOfSection)]
    [InlineData("go to the bridge at the end of this section", JumpTiming.EndOfSection)]
    [InlineData("go to the bridge now", JumpTiming.NextBar)]
    [InlineData("right now go to the bridge", JumpTiming.NextBar)]
    [InlineData("bridge on the one", JumpTiming.NextBar)]
    [InlineData("bridge next bar", JumpTiming.NextBar)]
    [InlineData("bridge next beat", JumpTiming.NextBeat)]
    public void VC35_timing_phrases(string heard, JumpTiming timing)
    {
        var command = Assert.Single(VoiceGrammar.Parse(heard));
        Assert.Equal(VoiceIntent.Jump, command.Intent);
        Assert.Equal(SectionType.Bridge, command.Target!.Type);
        Assert.Equal(timing, command.Timing);
    }

    [Theory]
    [InlineData("go to verse two", "Verse 2")]
    [InlineData("verse 1", "Verse 1")]
    [InlineData("chorus two", "Chorus 2")]
    [InlineData("the second chorus", "Chorus 2")]
    [InlineData("third chorus", "Chorus 3")]
    [InlineData("last chorus", "Chorus 3")]
    [InlineData("go to the chorus", "Chorus 2")]
    [InlineData("bridge", "Bridge")]
    [InlineData("intro", "Intro")]
    public void VC30_targets_resolve_against_the_song_and_the_remaining_plan(string heard, string label)
    {
        var infos = Infos();
        var plan = AsWritten(infos);
        var r = One(heard, plan, Inside(plan, "Verse 2"));
        Assert.Equal(VoiceResultKind.Action, r.Kind);
        Assert.Equal(LiveActionKind.JumpToSection, r.Action!.Kind);
        Assert.Equal(ActionSource.Voice, r.Action.Source);
        Assert.Equal(label, r.Label);
        Assert.Equal(infos.Single(i => i.Label == label).Id, r.Action.SectionId);
    }

    [Fact]
    public void VC30_custom_names_match_fuzzily_and_no_match_is_ignored()
    {
        var sections = Sections.SectionTests.FixtureASections();
        sections = SectionList.Rename(sections, sections.Single(s => s.Name == "Interlude").Id, "Spontaneous");
        var infos = Infos(sections);
        var plan = AsWritten(infos);

        var command = Assert.Single(VoiceGrammar.Parse("go to the spontanious", ["Spontaneous"]));
        var r = VoiceResolver.Resolve(command, plan, Inside(plan, "Intro"));
        Assert.Equal("Spontaneous", r.Label);

        var none = One("go to the pre chorus", plan, Inside(plan, "Intro"));
        Assert.Equal(VoiceResultKind.Ignored, none.Kind);
        Assert.Equal("didn't catch a section", VoiceLines.Caption(none));
        Assert.Equal("Voice ignored: Pre chorus (no matching section)", VoiceLines.Log(none));
    }

    [Fact]
    public void VC70_lets_go_to_verse_in_the_first_chorus_replaces_the_next_item_with_verse_2()
    {
        var infos = Infos();
        var plan = Demo(infos);
        var now = Inside(plan, "Chorus 1");

        Assert.Equal("let s go to verse", VoiceText.Normalize("Let's go to verse"));
        var r = One("Let's go to verse", plan, now);
        Assert.Equal("Verse 2", r.Label); // rule 3b: not in the remaining plan, the next written after Chorus 1
        Assert.Equal(JumpTiming.EndOfSection, r.Action!.Timing);
        Assert.Equal("Voice: Verse 2, at the end of this section", VoiceLines.Log(r));
        Assert.Equal("Verse 2 next", VoiceLines.Caption(r));
        Assert.Equal("Verse two next", VoiceLines.Confirmation(r));

        var live = new LivePlan(plan);
        var result = live.Apply(r.Action, now);
        Assert.Equal(LiveOutcome.Queued, result.Outcome);
        Assert.True(live.Tick(result.Pending!.ExecuteAt));
        Assert.Equal(["Intro", "Chorus 1", "Verse 2", "Ending"], live.Committed.Items.Where(i => i.Length > 0).Select(i => i.Label));
    }

    [Fact]
    public void VC36_a_target_that_is_already_next_queues_nothing()
    {
        var plan = Demo(Infos());
        var now = Inside(plan, "Intro");
        var r = One("chorus", plan, now);

        Assert.Equal("Chorus 1", r.Label);
        Assert.Equal(LiveOutcome.AlreadyNext, new LivePlan(plan).Apply(r.Action!, now).Outcome);
        Assert.Equal("Voice: Chorus 1 (already next)", VoiceLines.Log(r, alreadyNext: true));
        Assert.Equal("Chorus one is already next", VoiceLines.Confirmation(r, alreadyNext: true));
    }

    [Fact]
    public void Repeat_skip_and_the_other_actions()
    {
        var plan = AsWritten(Infos());

        var again = One("chorus again", plan, Inside(plan, "Chorus 1"));
        Assert.Equal(LiveActionKind.RepeatOnce, again.Action!.Kind);
        Assert.Equal("Voice: repeat Chorus 1", VoiceLines.Log(again));
        Assert.Equal(LiveActionKind.JumpToSection, One("chorus again", plan, Inside(plan, "Verse 2")).Action!.Kind);

        var skip = One("skip next", plan, Inside(plan, "Intro")); // Verse 1 is next: Chorus 1 follows it
        Assert.Equal(LiveActionKind.JumpToItem, skip.Action!.Kind);
        Assert.Equal("Chorus 1", skip.Label);
        var wrong = One("skip the bridge", plan, Inside(plan, "Intro"));
        Assert.Equal(VoiceResultKind.Ignored, wrong.Kind);
        Assert.Equal("Voice ignored: Bridge (Bridge is not next)", VoiceLines.Log(wrong));

        var vamp = One("stay here", plan, Inside(plan, "Bridge"));
        Assert.Equal("Voice: vamp Bridge", VoiceLines.Log(vamp));
        Assert.Equal("Vamping", VoiceLines.Confirmation(vamp));
        Assert.Equal("Voice: move on", VoiceLines.Log(One("move on", plan, 1)));
        Assert.Equal("Voice: end", VoiceLines.Log(One("wrap it up", plan, 1)));
        Assert.Equal("Voice: cancel", VoiceLines.Log(One("never mind", plan, 1)));
        Assert.Equal(VoiceResultKind.Stop, One("kill the tracks", plan, 1).Kind);
    }

    [Fact]
    public void VC40_a_command_the_guide_was_saying_is_dropped_another_word_is_not()
    {
        var plan = AsWritten(Infos());
        GuideWord[] guide = [new("Chorus", 62.1, 62.5, 0.93)];
        var at = plan.PlanAt(62.1)!.Value;
        var spoken = VoiceGate.GuideWords(plan, guide, at - 5, at + 5).ToList();
        Assert.Single(spoken);

        var chorus = Assert.Single(VoiceGrammar.Parse("chorus"));
        var bridge = Assert.Single(VoiceGrammar.Parse("bridge"));
        Assert.True(VoiceGate.IsEcho(chorus.Keywords, at + 0.6, at + 1.0, spoken)); // within 300 ms of the guide's word
        Assert.False(VoiceGate.IsEcho(chorus.Keywords, at + 1.0, at + 1.5, spoken));
        Assert.False(VoiceGate.IsEcho(bridge.Keywords, at, at + 0.5, spoken));
        Assert.True(VoiceGate.IsEcho(bridge.Keywords, 10, 11, VoiceGate.Clip("Bridge next", 10.5, 11.2)));
        Assert.Equal("Voice ignored: Chorus (the guide track was talking)", VoiceLines.Ignored(chorus, VoiceLines.GuideTalking));
    }

    [Fact]
    public void VC45_low_confidence_and_the_same_command_within_two_seconds_are_dropped()
    {
        var plan = AsWritten(Infos());
        var bridge = One("bridge", plan, 1);
        var safeguards = new VoiceSafeguards();

        Assert.Equal(VoiceVerdict.LowConfidence, safeguards.Check(bridge, 0.5, 100));
        Assert.Equal(VoiceVerdict.Accept, safeguards.Check(bridge, 0.9, 100));
        Assert.Equal(VoiceVerdict.Duplicate, safeguards.Check(bridge, 0.9, 101.5));
        Assert.Equal(VoiceVerdict.Accept, safeguards.Check(One("vamp", plan, 1), 0.9, 101.6));
        Assert.Equal(VoiceVerdict.Accept, safeguards.Check(bridge, 0.9, 104));
    }

    [Fact]
    public void VC11_the_bias_list_has_the_songs_names_and_the_command_words()
    {
        var list = VoiceText.BiasList(Infos());
        Assert.Contains("Chorus 2", list);
        Assert.Contains("Interlude", list);
        Assert.Contains("never", list);
        Assert.Contains("second", list);
        Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
