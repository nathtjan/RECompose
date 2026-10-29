// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Automation;

/// <summary>What a cue sends (AUTO-6).</summary>
public enum CueActionKind
{
    MidiNote,
    ControlChange,
    ProgramChange,
    Speak,
}

/// <summary>Which passes of a repeated section a cue fires in (AUTO-8).</summary>
public enum CuePasses
{
    Every,
    First,
    Last,
}

/// <summary>Cues fire at the exact time or on the nearest beat or bar line (AUTO-3).</summary>
public enum CueSnap
{
    None,
    Beat,
    Bar,
}

/// <summary>A cue's action (AUTO-6): a note with its channel, velocity and length, a CC, a program change, or text to speak.</summary>
public sealed record CueAction
{
    public CueActionKind Kind { get; init; } = CueActionKind.MidiNote;

    /// <summary>MIDI channel 1–16.</summary>
    public int Channel { get; init; } = 1;
    public int Note { get; init; } = 72;
    public int Velocity { get; init; } = 127;
    public int LengthMs { get; init; } = 100;
    public int Controller { get; init; }
    public int Value { get; init; } = 127;
    public int Program { get; init; }
    public string? Text { get; init; }

    /// <summary>"MIDI note C4", "CC 64 = 127", "Program 5", "Speak “Big chorus”" (AUTO-5 log, AUTO-6 summary).</summary>
    public string Summary(string middleC = "C3") => Kind switch
    {
        CueActionKind.MidiNote => $"MIDI note {MidiNotes.Name(Note, middleC)}",
        CueActionKind.ControlChange => $"CC {Controller.ToString(CultureInfo.InvariantCulture)} = {Value.ToString(CultureInfo.InvariantCulture)}",
        CueActionKind.ProgramChange => $"Program {Program.ToString(CultureInfo.InvariantCulture)}",
        _ => $"Speak “{Text}”",
    };
}

/// <summary>A cue on a song (AUTO-1): a name and an action at a source time, shown wherever the plan plays that time.</summary>
public sealed record Cue
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public double SourceSec { get; init; }
    public CueAction Action { get; init; } = new();
    public CueSnap Snap { get; init; }
    public bool Enabled { get; init; } = true;
    public CuePasses Passes { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>"Cue 1" for the log (AUTO-5), whether the cue is called "Cue 1" or "1".</summary>
    public string LogName => Name.StartsWith("Cue", StringComparison.OrdinalIgnoreCase) ? Name : $"Cue {Name}";

    /// <summary>AUTO-1: "Cue {n}", one more than the highest number among the song's "Cue n" names.</summary>
    public static string NextName(IEnumerable<Cue> cues) =>
        "Cue " + (cues.Select(c => c.Name.StartsWith("Cue ", StringComparison.Ordinal) && int.TryParse(c.Name[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The source time it fires at, after the snap (AUTO-3).</summary>
    public double FireSec(TempoGrid grid) => Snap switch
    {
        CueSnap.Beat => grid.NearestBeatTime(SourceSec),
        CueSnap.Bar => grid.NearestBarTime(SourceSec),
        _ => SourceSec,
    };
}

/// <summary>The song's automatic section cues (AUTO-20), off by default.</summary>
public sealed record SongAutomation
{
    /// <summary>A MIDI note per section type, for lighting looks (AUTO-21).</summary>
    public bool SectionNotes { get; init; }

    /// <summary>The spoken guide: each section's name one bar ahead (AUTO-31).</summary>
    public bool SpokenGuide { get; init; }
}

/// <summary>MIDI note names and the section-type note map (AUTO-21, SET-30).</summary>
public static class MidiNotes
{
    private static readonly string[] Names = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    /// <summary>"E3" for 64 with middle C (60) called C3, "E4" with it called C4.</summary>
    public static string Name(int note, string middleC = "C3")
    {
        note = Math.Clamp(note, 0, 127);
        var octave = note / 12 - (middleC == "C4" ? 1 : 2);
        return Names[note % 12] + octave.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"C4", "F#3", "F♯3", "Bb2" or a number 0–127 → the MIDI note; null when it is neither.</summary>
    public static int? Parse(string? text, string middleC = "C3")
    {
        text = text?.Trim().Replace('♯', '#').Replace('♭', 'b');
        if (string.IsNullOrEmpty(text))
            return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number is >= 0 and <= 127 ? number : null;

        var pitch = "C D EF G A B".IndexOf(char.ToUpperInvariant(text[0]), StringComparison.Ordinal);
        if (pitch < 0)
            return null;
        var rest = text[1..];
        if (rest.StartsWith('#'))
            (pitch, rest) = (pitch + 1, rest[1..]);
        else if (rest.StartsWith('b'))
            (pitch, rest) = (pitch - 1, rest[1..]);
        if (!int.TryParse(rest, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var octave))
            return null;
        var note = (octave + (middleC == "C4" ? 1 : 2)) * 12 + pitch;
        return note is >= 0 and <= 127 ? note : null;
    }

    /// <summary>The default map (AUTO-21): Intro C3 (60) up to Ending B3, similar types sharing a note.</summary>
    public static int DefaultNote(SectionType type) => 60 + type switch
    {
        SectionType.Intro => 0,
        SectionType.Other => 1,
        SectionType.Verse => 2,
        SectionType.PreChorus => 3,
        SectionType.Chorus => 4,
        SectionType.PostChorus => 5,
        SectionType.Bridge => 6,
        SectionType.Interlude or SectionType.Instrumental or SectionType.Turnaround => 7,
        SectionType.Vamp or SectionType.Breakdown or SectionType.Build => 8,
        SectionType.Tag => 9,
        SectionType.Outro => 10,
        _ => 11, // Ending
    };

    /// <summary>The note for a type: the user's map where it has one, else the default.</summary>
    public static int NoteFor(SectionType type, IReadOnlyDictionary<SectionType, int>? map) =>
        map is not null && map.TryGetValue(type, out var note) ? note : DefaultNote(type);

    /// <summary>
    /// AUTO-2: a new cue's note is the first one at C4 or above that neither the section map nor another cue of the song uses.
    /// </summary>
    public static int FirstFreeNote(IEnumerable<Cue> cues, IReadOnlyDictionary<SectionType, int>? map, string middleC = "C3")
    {
        var used = Enum.GetValues<SectionType>().Select(t => NoteFor(t, map))
            .Concat(cues.Where(c => c.Action.Kind == CueActionKind.MidiNote).Select(c => c.Action.Note))
            .ToHashSet();
        for (var note = middleC == "C4" ? 60 : 72; note <= 127; note++)
        {
            if (!used.Contains(note))
                return note;
        }

        return 127;
    }

    /// <summary>AUTO-7: the section type whose note a cue's note equals, if any.</summary>
    public static SectionType? Clash(Cue cue, IReadOnlyDictionary<SectionType, int>? map) =>
        cue.Action.Kind != CueActionKind.MidiNote
            ? null
            : Enum.GetValues<SectionType>().Cast<SectionType?>().FirstOrDefault(t => NoteFor(t!.Value, map) == cue.Action.Note);
}
