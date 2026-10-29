// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using RECompose.Core.Sections;

namespace RECompose.Core.Arrangements;

public enum TokenStatus
{
    Ok,
    Unknown,
    NoSection,
}

/// <summary>One word of the quick order with what it resolved to (ARR-60).</summary>
public sealed record QuickToken(string Text, int Start, int Length, TokenStatus Status, string Label, string? Message = null);

/// <summary>The parsed shorthand: the tokens for the live preview and the entries Apply would create.</summary>
public sealed record QuickOrderResult(IReadOnlyList<QuickToken> Tokens, IReadOnlyList<ArrangementEntry> Entries)
{
    public bool HasProblems => Tokens.Any(t => t.Status != TokenStatus.Ok);

    public IEnumerable<QuickToken> Problems => Tokens.Where(t => t.Status != TokenStatus.Ok);
}

/// <summary>
/// ARR-60: MD shorthand such as <c>V1 C V2 C B x2 C+2 End</c> becomes an arrangement. Tokens are separated by
/// spaces or commas. An unnumbered token picks the first written occurrence of its type after the section
/// chosen for the previous token, so <c>V1 C V2 C</c> plays the chorus after Verse 1 and then the one after Verse 2.
/// </summary>
public static partial class QuickOrder
{
    private static readonly Dictionary<string, (SectionType Type, string Canonical)> Names = BuildNames();

    private static Dictionary<string, (SectionType, string)> BuildNames()
    {
        var map = new Dictionary<string, (SectionType, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(SectionType type, string canonical, params string[] names)
        {
            foreach (var name in names)
                map[name] = (type, canonical);
        }

        Add(SectionType.Intro, "I (Intro)", "i", "intro");
        Add(SectionType.Verse, "V (Verse)", "v", "verse");
        Add(SectionType.PreChorus, "PC (Pre-Chorus)", "pc", "pre", "prechorus");
        Add(SectionType.Chorus, "C (Chorus)", "c", "chorus", "refrain");
        Add(SectionType.PostChorus, "PoC (Post-Chorus)", "poc", "postchorus");
        Add(SectionType.Bridge, "B (Bridge)", "b", "bridge");
        Add(SectionType.Interlude, "IL (Interlude)", "il", "interlude");
        Add(SectionType.Instrumental, "Inst (Instrumental)", "inst", "instrumental", "solo");
        Add(SectionType.Turnaround, "Tu (Turnaround)", "tu", "turn", "turnaround");
        Add(SectionType.Vamp, "Vamp", "vamp");
        Add(SectionType.Tag, "T (Tag)", "t", "tag");
        Add(SectionType.Breakdown, "Br (Breakdown)", "br", "breakdown", "downchorus");
        Add(SectionType.Build, "Build", "build");
        Add(SectionType.Outro, "O (Outro)", "o", "out", "outro");
        Add(SectionType.Ending, "E (Ending)", "e", "end", "ending");
        return map;
    }

    [GeneratedRegex(@"[^\s,]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^(?:\(\d+-\d+\)|[x×](?:\d+|\?)|[+\-−]\d+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixOnlyRegex();

    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z\-]*?)(?<num>\d+)?(?<suffix>(?:\(\d+-\d+\)|[x×](?:\d+|\?)|[+\-−]\d+)*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\(\d+-\d+\)|[x×](?:\d+|\?)|[+\-−]\d+", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixPartRegex();

    [GeneratedRegex(@"^(?<kind>click|pad)(?<n>\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedItemRegex();

    private sealed record RawToken(string Text, int Start, int Length);

    public static QuickOrderResult Parse(string text, IReadOnlyList<SectionInfo> sections)
    {
        var ordered = sections.OrderBy(s => s.StartSec).ToList();
        var tokens = new List<QuickToken>();
        var entries = new List<ArrangementEntry>();
        SectionInfo? previous = null;

        foreach (var raw in Words(text))
        {
            var word = raw.Text;

            if (word.Equals("hold", StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(ArrangementEntry.NewHold());
                tokens.Add(new QuickToken(word, raw.Start, raw.Length, TokenStatus.Ok, "Hold"));
                continue;
            }

            if (GeneratedItemRegex().Match(word) is { Success: true } gen)
            {
                var isClick = gen.Groups["kind"].Value.Equals("click", StringComparison.OrdinalIgnoreCase);
                var bars = gen.Groups["n"].Success ? int.Parse(gen.Groups["n"].Value, CultureInfo.InvariantCulture) : (isClick ? 4 : 8);
                var entry = (isClick ? ArrangementEntry.ClickBars(bars) : ArrangementEntry.PadBars(bars)).Clamped();
                entries.Add(entry);
                tokens.Add(new QuickToken(word, raw.Start, raw.Length, TokenStatus.Ok, $"{(isClick ? "Click" : "Pad")} {entry.LengthBars}"));
                continue;
            }

            if (SuffixOnlyRegex().IsMatch(word))
            {
                if (entries.Count > 0 && entries[^1].Kind == EntryKind.Section)
                {
                    entries[^1] = ApplySuffix(entries[^1], word);
                    tokens.Add(new QuickToken(word, raw.Start, raw.Length, TokenStatus.Ok, word));
                }
                else
                {
                    tokens.Add(new QuickToken(word, raw.Start, raw.Length, TokenStatus.Unknown, word, "Nothing to apply this to"));
                }

                continue;
            }

            var match = TokenRegex().Match(word);
            var key = match.Success ? match.Groups["name"].Value.Replace("-", string.Empty) : string.Empty;
            if (!match.Success || !Names.TryGetValue(key, out var known))
            {
                tokens.Add(new QuickToken(word, raw.Start, raw.Length, TokenStatus.Unknown, word, Suggest(key.Length > 0 ? key : word)));
                continue;
            }

            int? number = match.Groups["num"].Success ? int.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture) : null;
            var chosen = Resolve(ordered, known.Type, number, previous);
            if (chosen is null)
            {
                var typeName = SectionTypes.DisplayName(known.Type);
                tokens.Add(new QuickToken(word, raw.Start, raw.Length, TokenStatus.NoSection, word,
                    number is { } n ? $"No {typeName} {n.ToString(CultureInfo.InvariantCulture)} in this song" : $"No {typeName} in this song"));
                continue;
            }

            var section = ArrangementEntry.ForSection(chosen.Id);
            if (match.Groups["suffix"].Value.Length > 0)
                section = ApplySuffix(section, match.Groups["suffix"].Value);
            entries.Add(section.Clamped());
            previous = chosen;
            tokens.Add(new QuickToken(word, raw.Start, raw.Length, TokenStatus.Ok, chosen.Label));
        }

        return new QuickOrderResult(tokens, entries);
    }

    /// <summary>
    /// One section named as the shorthand names it ("Chorus", "V2", "Verse 2"), picked after <paramref name="previous"/> by the
    /// same rule (the assistant's arrangements, AS-30). Null with a reason when the song has no such section.
    /// </summary>
    public static SectionInfo? Section(string text, IReadOnlyList<SectionInfo> sections, SectionInfo? previous, out string? problem)
    {
        var word = string.Concat(Words(text).Select(w => w.Text));
        var match = TokenRegex().Match(word);
        if (!match.Success || !Names.TryGetValue(match.Groups["name"].Value.Replace("-", string.Empty), out var known))
        {
            problem = $"\"{text}\" is not a section of this song";
            return null;
        }

        int? number = match.Groups["num"].Success ? int.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture) : null;
        var chosen = Resolve(sections.OrderBy(s => s.StartSec).ToList(), known.Type, number, previous);
        problem = chosen is null ? $"No {SectionTypes.DisplayName(known.Type)}{(number is { } n ? $" {n.ToString(CultureInfo.InvariantCulture)}" : string.Empty)} in this song" : null;
        return chosen;
    }

    /// <summary>The words of the text: split on spaces and commas, with "Verse 2" and "Pre Chorus" kept as one token.</summary>
    private static List<RawToken> Words(string text)
    {
        var matches = WordRegex().Matches(text ?? string.Empty).Select(m => new RawToken(m.Value, m.Index, m.Length)).ToList();
        var result = new List<RawToken>();
        for (var i = 0; i < matches.Count; i++)
        {
            var current = matches[i];
            if (i + 1 < matches.Count && ShouldJoin(current.Text, matches[i + 1].Text))
            {
                var next = matches[i + 1];
                var end = next.Start + next.Length;
                result.Add(new RawToken(current.Text + next.Text, current.Start, end - current.Start));
                i++;
                continue;
            }

            result.Add(current);
        }

        return result;
    }

    private static bool ShouldJoin(string first, string second)
    {
        // "Verse 2" → "Verse2"; "Pre Chorus" → "PreChorus".
        if (second.All(char.IsDigit) && first.All(c => char.IsLetter(c) || c == '-') && Names.ContainsKey(first.Replace("-", string.Empty)))
            return true;
        return second.All(char.IsLetter) && Names.ContainsKey((first + second).Replace("-", string.Empty));
    }

    private static ArrangementEntry ApplySuffix(ArrangementEntry entry, string suffix)
    {
        foreach (Match part in SuffixPartRegex().Matches(suffix))
        {
            var value = part.Value;
            if (value[0] == '(')
            {
                var bars = value.Trim('(', ')').Split('-');
                entry = entry with { Range = new BarRange { FromBar = int.Parse(bars[0], CultureInfo.InvariantCulture), ToBar = int.Parse(bars[1], CultureInfo.InvariantCulture) } };
            }
            else if (value[0] is 'x' or 'X' or '×')
            {
                entry = value.EndsWith('?')
                    ? entry with { Repeat = new RepeatSpec { UntilCued = true } }
                    : entry with { Repeat = new RepeatSpec { Count = int.Parse(value[1..], CultureInfo.InvariantCulture) } };
            }
            else
            {
                var amount = int.Parse(value[1..], CultureInfo.InvariantCulture);
                entry = entry with { Transpose = value[0] == '+' ? amount : -amount };
            }
        }

        return entry;
    }

    private static SectionInfo? Resolve(List<SectionInfo> sections, SectionType type, int? number, SectionInfo? previous)
    {
        var ofType = sections.Where(s => s.Type == type).ToList();
        // "End" picks the song's Ending, or its Outro when there is none.
        if (ofType.Count == 0 && type == SectionType.Ending)
            ofType = sections.Where(s => s.Type == SectionType.Outro).ToList();
        if (ofType.Count == 0)
            return null;

        if (number is { } n)
        {
            // A section whose label ends in that number ("Verse 2", "Chorus 2"), otherwise the n-th written one.
            var byLabel = ofType.FirstOrDefault(s => TrailingNumber(s.Label) == n);
            if (byLabel is not null)
                return byLabel;
            return n >= 1 && n <= ofType.Count ? ofType[n - 1] : null;
        }

        if (previous is not null)
        {
            var after = ofType.FirstOrDefault(s => s.StartSec > previous.StartSec + 1e-6);
            if (after is not null)
                return after;
        }

        return ofType[0];
    }

    private static int? TrailingNumber(string label)
    {
        var i = label.Length;
        while (i > 0 && char.IsDigit(label[i - 1]))
            i--;
        return i < label.Length && i > 0 && label[i - 1] == ' ' ? int.Parse(label[i..], CultureInfo.InvariantCulture) : null;
    }

    private static string? Suggest(string name)
    {
        if (name.Length < 3)
            return "Not a section";
        var best = Names.Select(kv => (kv.Key, kv.Value.Canonical, Distance: Distance(name.ToLowerInvariant(), kv.Key.ToLowerInvariant())))
            .Where(c => c.Key.Length >= 3 && c.Distance <= 2)
            .OrderBy(c => c.Distance)
            .FirstOrDefault();
        return best.Canonical is null ? "Not a section" : $"Did you mean {best.Canonical}?";
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }

        return row[b.Length];
    }
}
