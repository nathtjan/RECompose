// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Controls;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Controls;

// MIDI controls beyond presses (LIVE-50…53): profiles, key ranges, faders, section jumps by type, soft takeover.
public class ControlProfilesTests
{
    private static MidiMessage Note(int number, int velocity = 100, int channel = 1) => new(ControlKind.Note, channel, number, velocity);

    private static ControlInput Keys(int from, int to, int channel = 1) => new() { Kind = ControlKind.Note, Channel = channel, Number = from, NumberTo = to };

    [Fact]
    public void LIVE51_settings_from_before_profiles_become_the_default_profile_and_panic_moves_to_the_global_layer()
    {
        var old = new List<ControlBinding>
        {
            new(ControlAction.Go, ControlInput.FromKey("G")),
            new(ControlAction.Panic, ControlInput.FromKey("Ctrl+Shift+Space")),
        };

        var (profiles, global) = ControlProfiles.From(old, null, null);

        var profile = Assert.Single(profiles);
        Assert.Equal(ControlProfiles.DefaultName, profile.Name);
        Assert.Equal([ControlAction.Go], profile.Bindings.Select(b => b.Action));
        Assert.Equal([ControlAction.Panic], global.Select(b => b.Action));

        // A first start: the page-turner keys, Panic in the global layer.
        var (fresh, freshGlobal) = ControlProfiles.From(null, null, null);
        Assert.Equal(ControlDefaults.PageTurner, fresh[0].Bindings);
        Assert.Equal(ControlDefaults.Global, freshGlobal);
    }

    [Fact]
    public void LIVE51_next_profile_goes_round()
    {
        List<ControlProfile> profiles = [new("Default", []), new("Band", []), new("Youth", [])];
        Assert.Equal("Band", ControlProfiles.Next(profiles, "Default"));
        Assert.Equal("Default", ControlProfiles.Next(profiles, "Youth"));
    }

    [Fact]
    public void LIVE52_a_key_range_passes_each_note_on_and_off_and_the_narrower_binding_wins()
    {
        var pads = new ControlBinding(ControlAction.PadKey, Keys(36, 59));
        var go = new ControlBinding(ControlAction.Go, ControlInput.Learned(Note(48)));
        var router = new ControlRouter([pads, go]);

        // Two notes at once are not a bounce: each passes on, with its note number.
        Assert.Equal([new ControlEvent(ControlAction.PadKey, pads, 38, 100)], router.Midi(Note(38), 0));
        Assert.Equal([new ControlEvent(ControlAction.PadKey, pads, 43, 90)], router.Midi(Note(43, 90), 0.01));
        Assert.Equal([new ControlEvent(ControlAction.PadKey, pads, 38, 0)], router.Midi(Note(38, 0), 0.5));

        // Note 48 is in the range and bound on its own: only GO acts.
        Assert.Equal([ControlAction.Go], router.Midi(Note(48), 1).Acts());
        Assert.Empty(router.Midi(Note(60), 2));                // above the range
        Assert.Empty(router.Midi(Note(40, channel: 2), 2));   // another channel
        Assert.Equal("Notes 36–59 ch 1", pads.Display);

        Assert.Equal(2, ControlProfiles.PitchClass(pads, 38));
        Assert.Equal(10, ControlProfiles.PitchClass(new ControlBinding(ControlAction.PadKey, ControlInput.FromKey("B")) { Target = "10" }, 0));
    }

    [Fact]
    public void LIVE53_learning_an_input_another_binding_takes_says_which_one_acts()
    {
        var pads = new ControlBinding(ControlAction.PadKey, Keys(36, 59));
        var go = new ControlBinding(ControlAction.Go, ControlInput.Learned(Note(48)));

        Assert.Equal("Note 48 ch 1 is also Pads from a keyboard (Notes 36–59 ch 1). The narrower binding acts.", ControlProfiles.Overlap([pads, go], go));
        Assert.Null(ControlProfiles.Overlap([pads, go], new ControlBinding(ControlAction.Vamp, ControlInput.Learned(Note(70)))));
        Assert.Contains("Both act.", ControlProfiles.Overlap([pads], new ControlBinding(ControlAction.PadPlayStop, Keys(50, 72))), StringComparison.Ordinal);
    }

    [Fact]
    public void LIVE52_a_fader_passes_every_value_without_threshold_or_debounce()
    {
        var fader = new ControlBinding(ControlAction.Fader, ControlInput.Learned(new MidiMessage(ControlKind.ControlChange, 1, 7, 0))) { Target = "Drums" };
        var router = new ControlRouter([fader]);

        Assert.Equal(0, router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 7, 0), 0).Single().Value);
        Assert.Equal(5, router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 7, 5), 0.01).Single().Value);
        Assert.Equal(6, router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 7, 6), 0.02).Single().Value);
        Assert.Equal("CC 7 ch 1", fader.Display);

        // The same curve as the screen: the bottom is the fader's bottom stop, the top +6 dB.
        Assert.Equal(-60, ControlProfiles.FaderDb(0, -60, 6));
        Assert.Equal(6, ControlProfiles.FaderDb(127, -60, 6));
        Assert.Equal(-19.8, ControlProfiles.FaderDb(64, -40, 0));
    }

    [Fact]
    public void LIVE52_soft_takeover_waits_for_the_controller_to_reach_the_level_and_lets_go_when_it_moves_elsewhere()
    {
        var takeover = new SoftTakeover();

        Assert.False(takeover.Accept("Drums", -30, -6));    // the controller is far below the strip
        Assert.False(takeover.Accept("Drums", -12, -6));
        Assert.True(takeover.Accept("Drums", -3, -6));      // crossed it
        Assert.True(takeover.Accept("Drums", -4, -3));
        Assert.True(takeover.Accept("Drums", -4.5, -4));    // where the controller left it

        // A preset puts the strip at 0 dB: the controller has to come to it again.
        Assert.False(takeover.Accept("Drums", -5, 0));
        Assert.True(takeover.Accept("Drums", -0.5, 0));     // within a decibel
    }

    [Fact]
    public void LIVE50_a_section_button_jumps_to_the_next_section_of_its_type_else_the_last_one()
    {
        var grid = new TempoGrid(72, 3, 0.02);
        var infos = SectionList.Describe(Sections.SectionTests.FixtureASections(), grid, 310.02);
        var plan = PlanBuilder.Build(grid, infos, 310.02, null);
        double Inside(string label) => plan.Items.First(i => i.Label == label).PlanStart + 1;

        var next = ControlProfiles.FindSection(plan, Inside("Verse 1"), SectionType.Chorus);
        Assert.Equal(plan.Items.First(i => i.Label == "Chorus 1").Id, next?.ItemId);

        // In the last chorus there is none ahead: that chorus again.
        var again = ControlProfiles.FindSection(plan, Inside("Chorus 3"), SectionType.Chorus);
        Assert.Equal(infos.Single(i => i.Label == "Chorus 3").Id, again?.SectionId);

        Assert.Null(ControlProfiles.FindSection(plan, Inside("Intro"), SectionType.PreChorus));
        Assert.Equal("Pre-Chorus", ControlDefaults.Name(ControlAction.JumpToSection, "PreChorus"));
        Assert.Equal("Pad B♭", ControlDefaults.Name(ControlAction.PadKey, "10"));
    }
}
