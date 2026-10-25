// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Controls;

namespace RECompose.Core.Tests.Controls;

// The control map (LIVE-50…55, AT-25): bindings, debounce, long press, Learn.
public class ControlMapTests
{
    private static ControlRouter Router(params ControlBinding[] bindings) => new(bindings);

    [Fact]
    public void LIVE50_the_page_turner_profile_binds_the_pages_and_the_arrows()
    {
        var router = new ControlRouter(ControlDefaults.PageTurner);

        Assert.Equal([ControlAction.Next], router.Key("PageDown", true, 0));
        Assert.Equal([ControlAction.Back], router.Key("PageUp", true, 0));
        Assert.Equal([ControlAction.Next], router.Key("right", true, 1));   // case does not matter
        Assert.Equal([ControlAction.Back], router.Key("Left", true, 1));
        Assert.Empty(router.Key("F9", true, 2));
    }

    [Fact]
    public void AT25_two_presses_within_250_ms_count_once()
    {
        var router = Router(new ControlBinding(ControlAction.Go, ControlInput.FromKey("PageDown")));

        Assert.Single(router.Key("PageDown", true, 10.00));
        router.Key("PageDown", false, 10.05);
        Assert.Empty(router.Key("PageDown", true, 10.20));     // a bounce
        router.Key("PageDown", false, 10.22);
        Assert.Single(router.Key("PageDown", true, 10.30));    // 300 ms after the first: a new press
    }

    [Fact]
    public void LIVE52_a_midi_controller_binds_with_channel_and_threshold_and_a_pedal_acts_on_the_press()
    {
        var pedal = ControlInput.Learned(new MidiMessage(ControlKind.ControlChange, 1, 64, 127));
        Assert.Equal("CC 64 ch 1 ≥ 64", pedal.Display);
        var router = Router(new ControlBinding(ControlAction.Go, pedal));

        Assert.Equal([ControlAction.Go], router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 64, 127), 0));
        Assert.Empty(router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 64, 0), 0.1));     // the release is not a press
        Assert.Empty(router.Midi(new MidiMessage(ControlKind.ControlChange, 2, 64, 127), 1));     // another channel
        Assert.Empty(router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 65, 127), 1));     // another controller
        Assert.Empty(router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 64, 30), 2));      // below the threshold
        Assert.Equal([ControlAction.Go], router.Midi(new MidiMessage(ControlKind.ControlChange, 1, 64, 100), 3));
    }

    [Fact]
    public void LIVE52_notes_and_program_changes_bind_and_note_on_with_velocity_zero_is_a_release()
    {
        var router = Router(
            new ControlBinding(ControlAction.Vamp, ControlInput.Learned(new MidiMessage(ControlKind.Note, 3, 60, 100))),
            new ControlBinding(ControlAction.End, ControlInput.Learned(new MidiMessage(ControlKind.ProgramChange, 2, 5, 0))));

        Assert.Equal([ControlAction.Vamp], router.Midi(new MidiMessage(ControlKind.Note, 3, 60, 90), 0));
        Assert.Empty(router.Midi(new MidiMessage(ControlKind.Note, 3, 60, 0), 0.05));
        Assert.Equal([ControlAction.End], router.Midi(new MidiMessage(ControlKind.ProgramChange, 2, 5, 0), 1));
        Assert.Equal("Program 5 ch 2", router.Bindings[1].Input.Display);
    }

    [Fact]
    public void LIVE54_a_binding_with_a_long_action_does_the_short_one_on_release_and_the_long_one_after_the_hold()
    {
        var binding = new ControlBinding(ControlAction.Next, ControlInput.FromKey("F5"), ControlAction.Panic);
        var router = Router(binding);

        // A tap: nothing on the press, the short action on the release.
        Assert.Empty(router.Key("F5", true, 0));
        Assert.Empty(router.Tick(0.3));
        Assert.Equal([ControlAction.Next], router.Key("F5", false, 0.35));

        // A hold: the long action once when the time passes; the release then does nothing more.
        Assert.Empty(router.Key("F5", true, 5));
        Assert.Empty(router.Tick(5.4));
        Assert.Equal([ControlAction.Panic], router.Tick(5.65));
        Assert.Empty(router.Tick(6));
        Assert.Empty(router.Key("F5", false, 6.2));
    }

    [Fact]
    public void A_key_that_repeats_while_held_acts_once()
    {
        var router = Router(new ControlBinding(ControlAction.Next, ControlInput.FromKey("PageDown")), new ControlBinding(ControlAction.Go, ControlInput.FromKey("F6"), ControlAction.Panic));

        Assert.Single(router.Key("PageDown", true, 0));
        for (var t = 0.5; t < 3; t += 0.03)
            Assert.Empty(router.Key("F6", true, t).Where(a => a == ControlAction.Go)); // never the short action while held
    }
}
