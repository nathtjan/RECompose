// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace RECompose.Core.Sections;

/// <summary>The kind of a section (DM-21). It drives the colour, the letters and later the MIDI note.</summary>
public enum SectionType
{
    Intro,
    Verse,
    PreChorus,
    Chorus,
    PostChorus,
    Bridge,
    Interlude,
    Instrumental,
    Turnaround,
    Vamp,
    Tag,
    Breakdown,
    Build,
    Outro,
    Ending,
    Other,
}

/// <summary>Keywords, display names, letters and colour tokens of the section types (SEC-42, design system "Sections").</summary>
public static class SectionTypes
{
    /// <summary>A spoken or typed word for a type, and the name a detected section gets from it.</summary>
    public sealed record Keyword(string Phrase, SectionType Type, string DisplayName)
    {
        public string[] Words { get; } = Phrase.Split(' ');
    }

    /// <summary>SEC-42 in spec order. Matching prefers the longest phrase, then this order.</summary>
    public static readonly IReadOnlyList<Keyword> Keywords =
    [
        new("pre chorus", SectionType.PreChorus, "Pre-Chorus"),
        new("prechorus", SectionType.PreChorus, "Pre-Chorus"),
        new("pre", SectionType.PreChorus, "Pre-Chorus"),
        new("post chorus", SectionType.PostChorus, "Post-Chorus"),
        new("postchorus", SectionType.PostChorus, "Post-Chorus"),
        new("chorus", SectionType.Chorus, "Chorus"),
        new("refrain", SectionType.Chorus, "Refrain"),
        new("verse", SectionType.Verse, "Verse"),
        new("bridge", SectionType.Bridge, "Bridge"),
        new("intro", SectionType.Intro, "Intro"),
        new("interlude", SectionType.Interlude, "Interlude"),
        new("instrumental", SectionType.Instrumental, "Instrumental"),
        new("inst", SectionType.Instrumental, "Instrumental"),
        new("solo", SectionType.Instrumental, "Solo"),
        new("turnaround", SectionType.Turnaround, "Turnaround"),
        new("turn", SectionType.Turnaround, "Turnaround"),
        new("vamp", SectionType.Vamp, "Vamp"),
        new("tag", SectionType.Tag, "Tag"),
        new("breakdown", SectionType.Breakdown, "Breakdown"),
        new("down chorus", SectionType.Breakdown, "Down Chorus"),
        new("build", SectionType.Build, "Build"),
        new("outro", SectionType.Outro, "Outro"),
        new("ending", SectionType.Ending, "Ending"),
        new("end", SectionType.Ending, "Ending"),
    ];

    /// <summary>The type a name implies (SEC-42, first match), or Other.</summary>
    public static SectionType FromName(string? name)
    {
        var words = Words(name);
        for (var i = 0; i < words.Length; i++)
        {
            if (MatchAt(words, i) is { } keyword)
                return keyword.Type;
        }

        return SectionType.Other;
    }

    /// <summary>The longest keyword starting at <paramref name="index"/>, if any.</summary>
    public static Keyword? MatchAt(IReadOnlyList<string> words, int index)
    {
        Keyword? best = null;
        foreach (var keyword in Keywords)
        {
            if (index + keyword.Words.Length > words.Count)
                continue;
            var match = true;
            for (var j = 0; j < keyword.Words.Length && match; j++)
                match = words[index + j] == keyword.Words[j];
            if (match && (best is null || keyword.Words.Length > best.Words.Length))
                best = keyword;
        }

        return best;
    }

    /// <summary>Lower-case words with punctuation removed; hyphens split words ("Pre-Chorus" → pre, chorus).</summary>
    public static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>"Pre-Chorus", for type selects.</summary>
    public static string DisplayName(SectionType type) => type switch
    {
        SectionType.PreChorus => "Pre-Chorus",
        SectionType.PostChorus => "Post-Chorus",
        _ => type.ToString(),
    };

    /// <summary>The badge letters without a number (design system: I, V, PC, C, PoC, B, IL…); null for Other (a diamond icon).</summary>
    public static string? Letters(SectionType type) => type switch
    {
        SectionType.Intro => "I",
        SectionType.Verse => "V",
        SectionType.PreChorus => "PC",
        SectionType.Chorus => "C",
        SectionType.PostChorus => "PoC",
        SectionType.Bridge => "B",
        SectionType.Interlude => "IL",
        SectionType.Instrumental => "IN",
        SectionType.Turnaround => "TU",
        SectionType.Vamp => "VA",
        SectionType.Breakdown => "BR",
        SectionType.Build => "BU",
        SectionType.Tag => "T",
        SectionType.Outro => "O",
        SectionType.Ending => "E",
        _ => null,
    };

    /// <summary>The badge for a labelled section: letters plus the label's trailing number ("C2", "V1", "B").</summary>
    public static string? Badge(SectionType type, string label)
    {
        if (Letters(type) is not { } letters)
            return null;
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? letters + n.ToString(CultureInfo.InvariantCulture)
            : letters;
    }

    /// <summary>The colour token for a type (UI-90 color.section.*). Similar types share a fill.</summary>
    public static string ColorToken(SectionType type) => type switch
    {
        SectionType.Intro => "section-intro",
        SectionType.Verse => "section-verse",
        SectionType.PreChorus => "section-pre-chorus",
        SectionType.Chorus => "section-chorus",
        SectionType.PostChorus => "section-post-chorus",
        SectionType.Bridge => "section-bridge",
        SectionType.Interlude or SectionType.Instrumental or SectionType.Turnaround => "section-interlude",
        SectionType.Vamp or SectionType.Breakdown or SectionType.Build => "section-vamp",
        SectionType.Tag => "section-tag",
        SectionType.Outro => "section-outro",
        SectionType.Ending => "section-ending",
        _ => "section-other",
    };
}
