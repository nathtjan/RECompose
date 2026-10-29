// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using RECompose.Core.Sections;

namespace RECompose.Core.Voice;

/// <summary>Normalising what was heard (VC-12), number words, and the words recognition is biased towards (VC-11).</summary>
public static class VoiceText
{
    public static readonly IReadOnlyList<string> NumberWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
    public static readonly IReadOnlyList<string> OrdinalWords = ["zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];

    /// <summary>The words of the grammar (VC-20, VC-35), for the bias list.</summary>
    public static readonly IReadOnlyList<string> CommandWords =
    [
        "go", "to", "skip", "next", "back", "straight", "into", "jump", "move", "on", "keep", "moving", "going", "out", "of", "this",
        "one", "more", "time", "again", "stay", "here", "section", "vamp", "loop", "hold", "end", "it", "take", "wrap", "up", "last",
        "never", "mind", "cancel", "scratch", "that", "as", "you", "were", "yes", "confirm", "do", "stop", "kill", "tracks",
        "now", "right", "bar", "beat", "after",
    ];

    /// <summary>VC-12: lower-case, every non-alphanumeric (apostrophes too) a space, spaces collapsed ("Let's" → "let s").</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                    builder.Append(' ');
                builder.Append(c);
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }

    public static string[] Words(string? text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>"two" or "2" → 2.</summary>
    public static int? Number(string word)
    {
        var index = IndexOf(NumberWords, word);
        if (index >= 0)
            return index;
        return int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and < 100 ? n : null;
    }

    /// <summary>"second" or "2nd" → 2.</summary>
    public static int? Ordinal(string word)
    {
        var index = IndexOf(OrdinalWords, word);
        if (index > 0)
            return index;
        if (word.Length > 2 && word[^2..] is "st" or "nd" or "rd" or "th"
            && int.TryParse(word[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and < 100)
            return n;
        return null;
    }

    /// <summary>"Verse 2" → "Verse two", for the MD's confirmations (VC-70).</summary>
    public static string Spoken(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            if (int.TryParse(words[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < NumberWords.Count)
                words[i] = NumberWords[n];
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// VC-11: the command words, the song's section names, labels and type words, and the number and ordinal words,
    /// once each, as the recogniser's prompt.
    /// </summary>
    public static IReadOnlyList<string> BiasList(IEnumerable<SectionInfo> sections)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        void Add(string phrase)
        {
            if (phrase.Length > 0 && seen.Add(phrase.ToLowerInvariant()))
                list.Add(phrase);
        }

        foreach (var section in sections)
        {
            Add(section.Section.Name.Trim());
            Add(section.Label);
            if (section.Type != SectionType.Other)
                Add(SectionTypes.DisplayName(section.Type));
        }

        foreach (var word in CommandWords.Concat(NumberWords.Skip(1)).Concat(OrdinalWords.Skip(1).Take(4)))
            Add(word);
        return list;
    }

    /// <summary>The bias list as one prompt line for the recogniser.</summary>
    public static string Prompt(IEnumerable<SectionInfo> sections) => string.Join(", ", BiasList(sections));

    /// <summary>Edit distance, for custom section names (VC-30 rule 4).</summary>
    public static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static int IndexOf(IReadOnlyList<string> list, string word)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == word)
                return i;
        }

        return -1;
    }
}
