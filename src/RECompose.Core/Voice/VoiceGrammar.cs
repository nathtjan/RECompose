// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Planning;
using RECompose.Core.Sections;

namespace RECompose.Core.Voice;

/// <summary>The intents of VC-20.</summary>
public enum VoiceIntent
{
    Jump,
    Skip,
    MoveOn,
    Repeat,
    Vamp,
    End,
    Cancel,
    Confirm,
    Stop,
}

/// <summary>
/// A spoken section reference (VC-30): a type with a number ("verse two"), an ordinal ("second chorus"), "last", or a
/// custom section's name. <see cref="Words"/> are the words that named it, for the guide gate (VC-40).
/// </summary>
public sealed record SectionRef(SectionType Type, int? Number, int? Ordinal, bool Last, string? Name, IReadOnlyList<string> Words)
{
    public string Text => string.Join(' ', Words);
}

/// <summary>One command of an utterance. <see cref="Keywords"/> are the words the gate compares with what the app was saying.</summary>
public sealed record VoiceCommand(VoiceIntent Intent, JumpTiming Timing, SectionRef? Target, IReadOnlyList<string> Keywords, bool TimingSaid = false);

/// <summary>
/// The grammar of VC-20 with the timing phrases of VC-35. Fillers go, timing phrases are taken out and given to the command
/// they belong to, and the rest is read left to right, so "never mind, let's stay in this section" is CANCEL then VAMP.
/// </summary>
public static class VoiceGrammar
{
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "hey", "okay", "ok", "actually", "let", "s", "can", "we", "please", "guys", "the", "just", "um", "uh", "so", "and", "then", "alright", "a", "us",
    };

    private static readonly (string[] Words, JumpTiming Timing)[] TimingPhrases =
    [
        (["at", "the", "end", "of", "this", "section"], JumpTiming.EndOfSection),
        (["at", "end", "of", "this", "section"], JumpTiming.EndOfSection),
        (["at", "the", "end", "of", "this"], JumpTiming.EndOfSection),
        (["at", "the", "end"], JumpTiming.EndOfSection),
        (["after", "this", "section"], JumpTiming.EndOfSection),
        (["after", "this"], JumpTiming.EndOfSection),
        (["right", "now"], JumpTiming.NextBar),
        (["on", "the", "one"], JumpTiming.NextBar),
        (["on", "one"], JumpTiming.NextBar),
        (["next", "bar"], JumpTiming.NextBar),
        (["next", "beat"], JumpTiming.NextBeat),
        (["now"], JumpTiming.NextBar),
    ];

    private enum Shape
    {
        /// <summary>The intent alone.</summary>
        Plain,

        /// <summary>A section follows ("go to", "back to").</summary>
        Target,

        /// <summary>"skip": a section may follow.</summary>
        MaybeTarget,
    }

    // Longest phrase first wins at a position (after fillers are gone).
    private static readonly (string[] Words, VoiceIntent Intent, Shape Shape)[] Triggers =
    [
        (["never", "mind"], VoiceIntent.Cancel, Shape.Plain),
        (["nevermind"], VoiceIntent.Cancel, Shape.Plain),
        (["cancel"], VoiceIntent.Cancel, Shape.Plain),
        (["scratch", "that"], VoiceIntent.Cancel, Shape.Plain),
        (["keep", "going"], VoiceIntent.Cancel, Shape.Plain),
        (["as", "you", "were"], VoiceIntent.Cancel, Shape.Plain),
        (["stop", "tracks"], VoiceIntent.Stop, Shape.Plain),
        (["kill", "tracks"], VoiceIntent.Stop, Shape.Plain),
        (["end", "it", "here"], VoiceIntent.End, Shape.Plain),
        (["take", "it", "to", "end"], VoiceIntent.End, Shape.Plain),
        (["wrap", "it", "up"], VoiceIntent.End, Shape.Plain),
        (["last", "time"], VoiceIntent.End, Shape.Plain),
        (["end"], VoiceIntent.End, Shape.Plain),
        (["stay", "in", "this", "section"], VoiceIntent.Vamp, Shape.Plain),
        (["stay", "on", "this"], VoiceIntent.Vamp, Shape.Plain),
        (["stay", "here"], VoiceIntent.Vamp, Shape.Plain),
        (["stay"], VoiceIntent.Vamp, Shape.Plain),
        (["vamp", "this"], VoiceIntent.Vamp, Shape.Plain),
        (["vamp"], VoiceIntent.Vamp, Shape.Plain),
        (["loop", "this"], VoiceIntent.Vamp, Shape.Plain),
        (["hold", "here"], VoiceIntent.Vamp, Shape.Plain),
        (["hold", "this"], VoiceIntent.Vamp, Shape.Plain),
        (["one", "more", "time"], VoiceIntent.Repeat, Shape.Plain),
        (["again"], VoiceIntent.Repeat, Shape.Plain),
        (["move", "on"], VoiceIntent.MoveOn, Shape.Plain),
        (["keep", "moving"], VoiceIntent.MoveOn, Shape.Plain),
        (["out", "of", "this"], VoiceIntent.MoveOn, Shape.Plain),
        (["go", "on"], VoiceIntent.MoveOn, Shape.Plain),
        (["skip", "to"], VoiceIntent.Jump, Shape.Target),
        (["skip", "that"], VoiceIntent.Skip, Shape.Plain),
        (["skip", "this"], VoiceIntent.Skip, Shape.Plain),
        (["skip", "next"], VoiceIntent.Skip, Shape.Plain),
        (["skip"], VoiceIntent.Skip, Shape.MaybeTarget),
        (["go", "straight", "into"], VoiceIntent.Jump, Shape.Target),
        (["go", "straight", "to"], VoiceIntent.Jump, Shape.Target),
        (["straight", "into"], VoiceIntent.Jump, Shape.Target),
        (["straight", "to"], VoiceIntent.Jump, Shape.Target),
        (["go", "back", "to"], VoiceIntent.Jump, Shape.Target),
        (["back", "to"], VoiceIntent.Jump, Shape.Target),
        (["jump", "to"], VoiceIntent.Jump, Shape.Target),
        (["take", "it", "to"], VoiceIntent.Jump, Shape.Target),
        (["go", "into"], VoiceIntent.Jump, Shape.Target),
        (["go", "to"], VoiceIntent.Jump, Shape.Target),
        (["yes"], VoiceIntent.Confirm, Shape.Plain),
        (["yeah"], VoiceIntent.Confirm, Shape.Plain),
        (["confirm"], VoiceIntent.Confirm, Shape.Plain),
        (["do", "it"], VoiceIntent.Confirm, Shape.Plain),
        (["go"], VoiceIntent.MoveOn, Shape.MaybeTarget),
    ];

    private const string TimingMark = "\u0001";

    /// <summary>
    /// The commands in a transcript (raw or normalised). <paramref name="customNames"/> are the song's section names with no type
    /// word (VC-30 rule 4); with <paramref name="acceptBareNames"/> a section name alone means go there (VC-1).
    /// </summary>
    public static IReadOnlyList<VoiceCommand> Parse(string transcript, IEnumerable<string>? customNames = null, bool acceptBareNames = true)
    {
        var (words, marks) = TakeTiming(VoiceText.Words(transcript));
        var custom = (customNames ?? []).Select(n => VoiceText.Words(n)).Where(w => w.Length > 0).Distinct(new WordsComparer()).ToList();

        var commands = new List<VoiceCommand>();
        JumpTiming? early = null;
        var i = 0;
        while (i < words.Count)
        {
            if (words[i] == TimingMark)
            {
                var timing = marks[i];
                if (commands.Count == 0)
                    early = timing;
                else
                    commands[^1] = commands[^1] with { Timing = timing, TimingSaid = true };
                i++;
                continue;
            }

            if (Trigger(words, i) is { } trigger)
            {
                var after = i + trigger.Words.Length;
                var keywords = trigger.Words.ToList();
                SectionRef? target = null;
                if (trigger.Shape != Shape.Plain && Reference(words, after, custom) is { } found)
                {
                    target = found.Ref;
                    after = found.End;
                    keywords = [.. found.Ref.Words];
                }

                // "go chorus" is a jump; "go" alone moves on.
                var intent = trigger.Intent == VoiceIntent.MoveOn && target is not null ? VoiceIntent.Jump : trigger.Intent;
                commands.Add(new VoiceCommand(intent, JumpTiming.EndOfSection, target, keywords));
                if (intent == VoiceIntent.Jump && target is not null && after < words.Count && words[after] == "next")
                    after++;
                i = after;
                continue;
            }

            if (Reference(words, i, custom) is { } reference)
            {
                var end = reference.End;
                if (end < words.Count && words[end] == "again")
                {
                    commands.Add(new VoiceCommand(VoiceIntent.Repeat, JumpTiming.EndOfSection, reference.Ref, reference.Ref.Words));
                    i = end + 1;
                    continue;
                }

                var next = end < words.Count && words[end] == "next";
                if (next || acceptBareNames)
                    commands.Add(new VoiceCommand(VoiceIntent.Jump, JumpTiming.EndOfSection, reference.Ref, reference.Ref.Words));
                i = next ? end + 1 : end;
                continue;
            }

            i++;
        }

        if (early is { } first && commands.Count > 0 && !commands[0].TimingSaid)
            commands[0] = commands[0] with { Timing = first, TimingSaid = true };
        return commands;
    }

    /// <summary>Timing phrases become a mark (they would otherwise read as "end" or "next"), then fillers go.</summary>
    private static (List<string> Words, Dictionary<int, JumpTiming> Marks) TakeTiming(string[] all)
    {
        var words = new List<string>();
        var marks = new Dictionary<int, JumpTiming>();
        var i = 0;
        while (i < all.Length)
        {
            var matched = false;
            foreach (var (phrase, timing) in TimingPhrases)
            {
                if (!StartsWith(all, i, phrase))
                    continue;

                // "on one" is a timing only when nothing follows it that it could count ("one more time").
                if (phrase[^1] == "one" && i + phrase.Length < all.Length && all[i + phrase.Length] == "more")
                    continue;
                marks[words.Count] = timing;
                words.Add(TimingMark);
                i += phrase.Length;
                matched = true;
                break;
            }

            if (matched)
                continue;
            if (!Fillers.Contains(all[i]))
                words.Add(all[i]);
            i++;
        }

        return (words, marks);
    }

    private static (string[] Words, VoiceIntent Intent, Shape Shape)? Trigger(List<string> words, int index)
    {
        (string[] Words, VoiceIntent Intent, Shape Shape)? best = null;
        foreach (var trigger in Triggers)
        {
            if (StartsWith(words, index, trigger.Words) && (best is null || trigger.Words.Length > best.Value.Words.Length))
                best = trigger;
        }

        return best;
    }

    private static (SectionRef Ref, int End)? Reference(List<string> words, int index, List<string[]> custom)
    {
        if (index >= words.Count)
            return null;

        // Custom names first: they may contain a type word ("Spontaneous Chorus" is still its own name when the song has it).
        foreach (var name in custom.OrderByDescending(n => n.Length))
        {
            if (index + name.Length > words.Count)
                continue;
            var heard = string.Join(' ', words.Skip(index).Take(name.Length));
            var wanted = string.Join(' ', name);
            if (heard == wanted || (wanted.Length >= 4 && VoiceText.Levenshtein(heard, wanted) <= 2))
                return (new SectionRef(SectionType.Other, null, null, false, wanted, [.. words.Skip(index).Take(name.Length)]), index + name.Length);
        }

        var i = index;
        int? ordinal = null;
        var last = false;
        if (VoiceText.Ordinal(words[i]) is { } n)
        {
            ordinal = n;
            i++;
        }
        else if (words[i] == "last")
        {
            last = true;
            i++;
        }

        if (i >= words.Count || SectionTypes.MatchAt(words, i) is not { } keyword)
            return null;

        var spoken = new List<string>(keyword.Words);
        i += keyword.Words.Length;
        int? number = null;
        if (ordinal is null && !last && i < words.Count && VoiceText.Number(words[i]) is { } k && k > 0
            && !(words[i] == "one" && i + 1 < words.Count && words[i + 1] == "more"))
        {
            number = k;
            i++;
        }

        return (new SectionRef(keyword.Type, number, ordinal, last, null, spoken), i);
    }

    private static bool StartsWith(IReadOnlyList<string> words, int index, string[] phrase)
    {
        if (index + phrase.Length > words.Count)
            return false;
        for (var j = 0; j < phrase.Length; j++)
        {
            if (words[index + j] != phrase[j])
                return false;
        }

        return true;
    }

    private sealed class WordsComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(string[] obj) => string.Join(' ', obj).GetHashCode(StringComparison.Ordinal);
    }
}
