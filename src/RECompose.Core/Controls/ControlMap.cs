// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace RECompose.Core.Controls;

/// <summary>What a key, a pedal or a controller can ask for (LIVE-50…55).</summary>
public enum ControlAction
{
    /// <summary>GO in a hold, move on from a repeat, else the next plan item (the page-turner's forward key).</summary>
    Next,

    /// <summary>Cancel the pending action; in Rehearsal lock with nothing pending, restart the section (ARR-71).</summary>
    Back,
    Go,
    PlayPause,
    Stop,
    MoveOn,
    Vamp,
    RepeatOnce,
    End,
    Cancel,
    KeyUp,
    KeyDown,
    Panic,
    RestartSection,
    LoopSection,

    /// <summary>Push-to-talk (VC-1): voice listens while this is held.</summary>
    Talk,

    /// <summary>The Talk input was let go. Not bindable: the router sends it on release.</summary>
    TalkEnd,

    /// <summary>The next section of the binding's type (<see cref="ControlBinding.Target"/> "Chorus"), else the last one.</summary>
    JumpToSection,

    /// <summary>The Perform strip's tile at that position (<see cref="ControlBinding.Target"/> "1"…"8").</summary>
    JumpToTile,

    /// <summary>A pad key: the binding's pitch class (<see cref="ControlBinding.Target"/> "0"…"11"), else the note played.</summary>
    PadKey,
    PadPlayStop,

    /// <summary>A continuous controller sets a level: "Master", "Pads" or a track group (<see cref="ControlBinding.Target"/>).</summary>
    Fader,

    NextProfile,

    /// <summary>The profile named in <see cref="ControlBinding.Target"/>.</summary>
    SelectProfile,
}

/// <summary>What the router passes on: the action, the binding that matched, and for MIDI the note number and value.</summary>
public readonly record struct ControlEvent(ControlAction Action, ControlBinding? Binding = null, int Number = 0, int Value = 0);

public enum ControlKind
{
    Key,
    Note,
    ControlChange,
    ProgramChange,
}

/// <summary>A MIDI message the engine received (protocol 9). Channel 1–16.</summary>
public readonly record struct MidiMessage(ControlKind Kind, int Channel, int Number, int Value)
{
    /// <summary>Note on with velocity 0 is a note off.</summary>
    public bool IsDown(int threshold) => Kind switch
    {
        ControlKind.Note => Value > 0,
        ControlKind.ControlChange => Value >= threshold,
        ControlKind.ProgramChange => true,
        _ => false,
    };
}

/// <summary>What a binding listens to: a computer key, or a MIDI note, controller or program change (LIVE-52).</summary>
public sealed record ControlInput
{
    public ControlKind Kind { get; init; }

    /// <summary>The key name for a keyboard input ("PageDown", "Ctrl+Shift+Space").</summary>
    public string? Key { get; init; }

    /// <summary>1–16; 0 listens on every channel.</summary>
    public int Channel { get; init; }

    public int Number { get; init; }

    /// <summary>A range of notes from <see cref="Number"/> up to this one (pads from a keyboard); null is one note.</summary>
    public int? NumberTo { get; init; }

    /// <summary>A controller counts as pressed at or above this value (a sustain pedal sends 127 and 0).</summary>
    public int Threshold { get; init; } = 64;

    public static ControlInput FromKey(string key) => new() { Kind = ControlKind.Key, Key = key };

    /// <summary>What Learn makes of the first message it hears (LIVE-53).</summary>
    public static ControlInput Learned(MidiMessage m) => new() { Kind = m.Kind, Channel = m.Channel, Number = m.Number };

    public bool Matches(MidiMessage m) =>
        Kind == m.Kind && Kind != ControlKind.Key && (Channel == 0 || Channel == m.Channel) && m.Number >= Number && m.Number <= Top;

    private int Top => NumberTo ?? Number;

    /// <summary>When two bindings take the same message, the narrower acts: one note over a range, one channel over any.</summary>
    public int Specificity => (Top == Number ? 2 : 0) + (Channel == 0 ? 0 : 1);

    /// <summary>The two inputs can take the same key or message.</summary>
    public bool Overlaps(ControlInput other) => Kind == other.Kind && (Kind == ControlKind.Key
        ? string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)
        : (Channel == 0 || other.Channel == 0 || Channel == other.Channel) && Number <= other.Top && other.Number <= Top);

    /// <summary>"PageDown", "Note 60 ch 1", "CC 64 ch 1 ≥ 64", "Program 5 ch 2".</summary>
    public string Display => Kind switch
    {
        ControlKind.Key => Key ?? string.Empty,
        ControlKind.Note when Top != Number => $"Notes {Number.ToString(CultureInfo.InvariantCulture)}–{Top.ToString(CultureInfo.InvariantCulture)}{ChannelText}",
        ControlKind.Note => $"Note {Number.ToString(CultureInfo.InvariantCulture)}{ChannelText}",
        ControlKind.ControlChange => $"CC {Number.ToString(CultureInfo.InvariantCulture)}{ChannelText} ≥ {Threshold.ToString(CultureInfo.InvariantCulture)}",
        _ => $"Program {Number.ToString(CultureInfo.InvariantCulture)}{ChannelText}",
    };

    private string ChannelText => Channel == 0 ? " any ch" : $" ch {Channel.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// One row of Settings › Controls. With <see cref="Long"/> the binding has two meanings: a short press does
/// <see cref="Action"/> (when the key comes up) and holding it for the long-press time does <see cref="Long"/> (LIVE-54).
/// </summary>
public sealed record ControlBinding(ControlAction Action, ControlInput Input, ControlAction? Long = null)
{
    /// <summary>What the action applies to: a section type, a tile number, a pitch class, a fader target or a profile.</summary>
    public string? Target { get; init; }

    /// <summary>Pads: the pad sounds while the note is held instead of each press starting or stopping it.</summary>
    public bool Hold { get; init; }

    /// <summary>Jumps: only while Perform is open.</summary>
    public bool PerformOnly { get; init; }

    /// <summary>Faders: the level moves only once the controller passes where it already is, so it never jumps.</summary>
    public bool SoftTakeover { get; init; } = true;

    /// <summary>A fader's controller has no press threshold to show.</summary>
    public string Display => Action == ControlAction.Fader && Input.Kind == ControlKind.ControlChange
        ? Input.Display[..Input.Display.IndexOf(" ≥", StringComparison.Ordinal)]
        : Input.Display;
}

/// <summary>The bindings in force (LIVE-50, LIVE-51). The default is the page-turner profile.</summary>
public static class ControlDefaults
{
    public static IReadOnlyList<ControlBinding> PageTurner { get; } =
    [
        new(ControlAction.Next, ControlInput.FromKey("PageDown")),
        new(ControlAction.Next, ControlInput.FromKey("Right")),
        new(ControlAction.Back, ControlInput.FromKey("PageUp")),
        new(ControlAction.Back, ControlInput.FromKey("Left")),
        new(ControlAction.Cancel, ControlInput.FromKey("Escape")),
        new(ControlAction.Go, ControlInput.FromKey("G")),
        new(ControlAction.Vamp, ControlInput.FromKey("V")),
        new(ControlAction.RepeatOnce, ControlInput.FromKey("R")),
        new(ControlAction.MoveOn, ControlInput.FromKey("M")),
        new(ControlAction.End, ControlInput.FromKey("E")),
        new(ControlAction.LoopSection, ControlInput.FromKey("L")),
    ];

    /// <summary>The bindings every profile has: Panic and the profile switches.</summary>
    public static IReadOnlyList<ControlBinding> Global { get; } =
    [
        new(ControlAction.Panic, ControlInput.FromKey("Ctrl+Shift+Space")),
    ];

    public static string Name(ControlAction action) => action switch
    {
        ControlAction.Next => "Next",
        ControlAction.Back => "Back",
        ControlAction.Go => "GO",
        ControlAction.PlayPause => "Play or pause",
        ControlAction.Stop => "Stop",
        ControlAction.MoveOn => "Move on",
        ControlAction.Vamp => "Vamp",
        ControlAction.RepeatOnce => "Repeat",
        ControlAction.End => "End",
        ControlAction.Cancel => "Cancel",
        ControlAction.KeyUp => "Key up",
        ControlAction.KeyDown => "Key down",
        ControlAction.Panic => "Panic",
        ControlAction.RestartSection => "Restart section",
        ControlAction.LoopSection => "Loop section",
        ControlAction.Talk => "Push to talk",
        ControlAction.JumpToSection => "Jump to section",
        ControlAction.JumpToTile => "Jump to tile",
        ControlAction.PadKey => "Pads from a keyboard",
        ControlAction.PadPlayStop => "Pads play or stop",
        ControlAction.Fader => "Fader",
        ControlAction.NextProfile => "Next profile",
        ControlAction.SelectProfile => "Profile",
        _ => action.ToString(),
    };

    /// <summary>"Chorus", "Tile 3", "Pad B♭", "Fader Drums", "Profile Band"; the action's name without a target.</summary>
    public static string Name(ControlAction action, string? target)
    {
        if (string.IsNullOrEmpty(target))
            return Name(action);
        return action switch
        {
            ControlAction.JumpToSection when Enum.TryParse<Sections.SectionType>(target, out var type) => Sections.SectionTypes.DisplayName(type),
            ControlAction.JumpToTile => $"Tile {target}",
            ControlAction.PadKey when int.TryParse(target, CultureInfo.InvariantCulture, out var pc) => $"Pad {new Music.Key(pc).ToDisplayString()}",
            ControlAction.Fader => $"Fader {target}",
            ControlAction.SelectProfile => $"Profile {target}",
            _ => $"{Name(action)} {target}",
        };
    }
}

/// <summary>
/// Turns presses into actions (LIVE-54, LIVE-55): the same input twice within the debounce time counts once, and a binding
/// with a long action tells a tap from a hold. Pure: the caller supplies the clock.
/// </summary>
public sealed class ControlRouter(IReadOnlyList<ControlBinding> bindings, double debounceSec = 0.25, double longPressSec = 0.6)
{
    private sealed class Held
    {
        public required ControlBinding Binding { get; init; }
        public double Since { get; init; }
        public bool LongFired { get; set; }
    }

    private readonly Dictionary<ControlBinding, double> _lastAccepted = [];
    private readonly List<Held> _held = [];

    public IReadOnlyList<ControlBinding> Bindings { get; } = bindings;

    /// <summary>A computer key went down or up.</summary>
    public IReadOnlyList<ControlEvent> Key(string key, bool down, double nowSec) =>
        Handle(Bindings.Where(b => b.Input.Kind == ControlKind.Key && string.Equals(b.Input.Key, key, StringComparison.OrdinalIgnoreCase)), down, nowSec);

    /// <summary>
    /// A MIDI message arrived from the engine. Faders and pad keys pass on every message as it is (a fader's value, a
    /// key's note on and off); the rest are presses.
    /// </summary>
    public IReadOnlyList<ControlEvent> Midi(MidiMessage message, double nowSec)
    {
        var matches = Bindings.Where(b => b.Input.Matches(message)).ToList();
        if (matches.Count == 0)
            return [];
        var narrowest = matches.Max(b => b.Input.Specificity);
        matches.RemoveAll(b => b.Input.Specificity < narrowest);

        var events = new List<ControlEvent>();
        var value = message.Kind == ControlKind.ProgramChange ? 127 : message.Value;
        foreach (var binding in matches.Where(b => IsDirect(b.Action)))
            events.Add(new ControlEvent(binding.Action, binding, message.Number, value));
        var presses = matches.Where(b => !IsDirect(b.Action)).ToList();
        if (presses.Count > 0)
            events.AddRange(Handle(presses, presses.Any(b => message.IsDown(b.Input.Threshold)), nowSec));
        return events;
    }

    private static bool IsDirect(ControlAction action) => action is ControlAction.Fader or ControlAction.PadKey;

    private List<ControlEvent> Handle(IEnumerable<ControlBinding> matches, bool down, double now)
    {
        var actions = new List<ControlEvent>();
        foreach (var binding in matches)
        {
            if (IsDirect(binding.Action))
            {
                actions.Add(new ControlEvent(binding.Action, binding, Value: down ? 127 : 0)); // a key on the computer keyboard
            }
            else if (down)
            {
                // A repeating key (auto-repeat while held) or a bouncing pedal counts once (LIVE-55).
                if (_held.Any(h => h.Binding == binding))
                    continue;
                if (_lastAccepted.TryGetValue(binding, out var last) && now - last < debounceSec)
                    continue;
                _lastAccepted[binding] = now;

                if (binding.Long is null)
                    actions.Add(new ControlEvent(binding.Action, binding));
                else
                    _held.Add(new Held { Binding = binding, Since = now });
            }
            else if (binding.Action == ControlAction.Talk)
            {
                actions.Add(new ControlEvent(ControlAction.TalkEnd, binding));
            }
            else if (_held.FirstOrDefault(h => h.Binding == binding) is { } held)
            {
                _held.Remove(held);
                if (!held.LongFired)
                    actions.Add(new ControlEvent(binding.Action, binding)); // let go before the long-press time: the short action
            }
        }

        return actions;
    }

    /// <summary>The long actions whose hold time has now passed.</summary>
    public IReadOnlyList<ControlEvent> Tick(double nowSec)
    {
        var actions = new List<ControlEvent>();
        foreach (var held in _held)
        {
            if (!held.LongFired && nowSec - held.Since >= longPressSec && held.Binding.Long is { } action)
            {
                held.LongFired = true;
                actions.Add(new ControlEvent(action, held.Binding));
            }
        }

        return actions;
    }
}
