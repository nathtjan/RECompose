// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Analysis;

/// <summary>What Detect from guide produced (SEC-14…17).</summary>
public sealed record DetectionResult(
    List<Section> Sections,
    List<GuideCall> Calls,
    int DetectedCount,
    int KeptCount,
    int NeedsReviewCount);

/// <summary>
/// Turns the guide's word timestamps into written sections (SEC-13…17). Pure: the words, the
/// grid and a "do the music tracks sound here" test go in; sections and calls come out.
/// </summary>
public static class SectionDetector
{
    /// <summary>Word gaps below this belong to one utterance (SEC-13).</summary>
    public const double UtteranceGapSec = 0.6;

    /// <summary>Below this confidence a section needs review (SEC-15).</summary>
    public const double ReviewThreshold = 0.7;

    /// <summary>A call that runs this far past a bar line still lands on it.</summary>
    private const double SpillBeats = 0.25;

    private static readonly string[] NumberWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    /// <summary>Directives that make an utterance a call without creating a section (SEC-12).</summary>
    private static readonly string[][] Directives =
    [
        ["all", "in"], ["break"], ["hits"], ["drums"], ["last", "time"], ["key", "change"],
    ];

    /// <summary>
    /// The prompt that biases transcription toward the call vocabulary (SEC-12): section words,
    /// numbers one to ten, counts and directives.
    /// </summary>
    public static string VocabularyPrompt =>
        "Intro. Verse 1. Verse 2. Pre-Chorus. Chorus. Post-Chorus. Bridge. Interlude. Instrumental. Turnaround. " +
        "Vamp. Tag. Breakdown. Build. Outro. Ending. One, two, three, four. Five, six, seven, eight. Nine, ten. " +
        "All in. Break. Hits. Drums. Last time. Key change.";

    /// <summary>
    /// SEC-14/15/17. <paramref name="musicSounds"/> answers whether any music track is above
    /// −40 dBFS between two source times (the first-bar rule). <paramref name="existing"/>
    /// are the current sections: manual, marked and edited ones are kept.
    /// </summary>
    public static DetectionResult Detect(
        IReadOnlyList<GuideWord> words,
        TempoGrid grid,
        double durationSec,
        Func<double, double, bool> musicSounds,
        IReadOnlyList<Section>? existing = null)
    {
        var calls = FindCalls(words, grid, durationSec, musicSounds);

        // Candidates in time order, then the merge rules.
        var candidates = calls
            .Where(c => c.Call.SectionName is not null && c.Call.SectionStartSec is not null)
            .OrderBy(c => c.Call.SectionStartSec)
            .ThenBy(c => c.Call.Start)
            .ToList();

        var accepted = new List<Candidate>();
        foreach (var c in candidates)
        {
            var start = c.Call.SectionStartSec!.Value;
            if (accepted.Count > 0)
            {
                var previous = accepted[^1];
                var previousStart = previous.Call.SectionStartSec!.Value;

                // Calls landing on the same bar merge, keeping the more specific name.
                if (Math.Abs(previousStart - start) < 0.001)
                {
                    if (Specificity(c) > Specificity(previous))
                        accepted[^1] = c;
                    continue;
                }

                // The same name again within 2 bars counts once.
                if (SameName(c, previous) && start - previousStart <= 2 * grid.BarLength + 0.001)
                    continue;
            }

            accepted.Add(c);
        }

        // Naming: unnumbered verses are numbered in order (SEC-14).
        var verseNumber = 0;
        var detected = new List<Section>();
        var used = new Dictionary<string, string>();
        foreach (var c in accepted)
        {
            var name = c.Name;
            if (c.Keyword.Type == SectionType.Verse)
            {
                if (c.Number is { } n and > 0)
                    verseNumber = Math.Max(verseNumber, n);
                else
                    name = $"{c.Keyword.DisplayName} {(++verseNumber).ToString(CultureInfo.InvariantCulture)}";
            }

            var confidence = Math.Round(c.WordConfidence * c.Certainty, 3);
            var section = new Section
            {
                Id = Section.NewId(),
                Name = name,
                Type = c.Keyword.Type,
                StartSec = c.Call.SectionStartSec!.Value,
                Origin = SectionOrigin.Guide,
                Confidence = confidence,
                NeedsReview = confidence < ReviewThreshold,
                CallId = c.Call.Id,
            };
            detected.Add(section);
            used[c.Call.Id] = section.Id;
        }

        // SEC-17: keep manual, marked and edited sections; replace untouched guide sections.
        var kept = (existing ?? []).Where(s => s.Origin != SectionOrigin.Guide || s.Edited).ToList();
        var merged = new List<Section>(kept);
        foreach (var s in detected)
        {
            if (!kept.Any(k => Math.Abs(k.StartSec - s.StartSec) < 0.001))
                merged.Add(s);
            else
                used.Remove(s.CallId!);
        }

        var result = SectionList.Sorted(merged);
        var finalCalls = calls.Select(c => c.Call with { UsedForSectionId = used.GetValueOrDefault(c.Call.Id) }).ToList();
        return new DetectionResult(result, finalCalls, detected.Count, kept.Count, result.Count(s => s.NeedsReview));
    }

    /// <summary>
    /// SEC-13: the calls in the guide (utterances with a vocabulary term), each section call
    /// with the name it announces and where that section starts (SEC-14 start rules).
    /// </summary>
    public static List<GuideCall> Calls(IReadOnlyList<GuideWord> words, TempoGrid grid, double durationSec, Func<double, double, bool> musicSounds) =>
        FindCalls(words, grid, durationSec, musicSounds).Select(c => c.Call).ToList();

    /// <summary>
    /// The SEC-16 result box: "{title}: {n} sections: {labels}. Click: {bpm} BPM, {bpb} beats per bar.
    /// Guide: {n} sections from {m} spoken calls." plus " {k} need review." and the SEC-17 report.
    /// </summary>
    public static string Summary(string title, IReadOnlyList<Section> sections, TempoGrid grid, DetectionResult result)
    {
        var labels = SectionList.Labels(SectionList.Sorted(sections));
        var calls = result.Calls.Count;
        var text = $"{title}: {Plural(sections.Count, "section")}: {string.Join(", ", labels)}. " +
                   $"Click: {Format.Bpm(grid.Bpm)} BPM, {grid.BeatsPerBar.ToString(CultureInfo.InvariantCulture)} beats per bar. " +
                   $"Guide: {Plural(result.DetectedCount, "section")} from {Plural(calls, "spoken call")}.";
        if (sections.Count == 0)
            text = $"{title}: no sections found. Click: {Format.Bpm(grid.Bpm)} BPM, {grid.BeatsPerBar.ToString(CultureInfo.InvariantCulture)} beats per bar. " +
                   $"Guide: {Plural(calls, "spoken call")}, none naming a section.";
        if (result.NeedsReviewCount > 0)
            text += result.NeedsReviewCount == 1 ? " 1 needs review." : $" {result.NeedsReviewCount.ToString(CultureInfo.InvariantCulture)} need review.";
        if (result.KeptCount > 0)
            text += $" Kept {Plural(result.KeptCount, "edited section")}.";
        return text;
    }

    // ---- Call parsing ------------------------------------------------------------------

    private sealed record Token(string Text, int? Number, GuideWord Word);

    private sealed record Candidate(GuideCall Call, SectionTypes.Keyword Keyword, int? Number, string Name, double WordConfidence, double Certainty);

    private static List<Candidate> FindCalls(IReadOnlyList<GuideWord> words, TempoGrid grid, double durationSec, Func<double, double, bool> musicSounds)
    {
        // Tokens: one per normalised word ("Pre-Chorus" gives two), keeping the source word.
        var tokens = new List<Token>();
        foreach (var w in words.OrderBy(w => w.Start))
        {
            foreach (var t in SectionTypes.Words(w.Text))
                tokens.Add(new Token(t, ParseNumber(t), w));
        }

        // Utterances: word gaps under 0.6 s.
        var utterances = new List<List<Token>>();
        foreach (var token in tokens)
        {
            if (utterances.Count > 0 && (ReferenceEquals(utterances[^1][^1].Word, token.Word) || token.Word.Start - utterances[^1][^1].Word.End < UtteranceGapSec))
                utterances[^1].Add(token);
            else
                utterances.Add([token]);
        }

        // Calls: utterances with a vocabulary term.
        var parsed = new List<(List<Token> Tokens, int KeywordIndex, SectionTypes.Keyword? Keyword)>();
        foreach (var u in utterances)
        {
            var texts = u.Select(t => t.Text).ToList();
            int keywordIndex = -1;
            SectionTypes.Keyword? keyword = null;
            for (var i = 0; i < texts.Count && keyword is null; i++)
            {
                if (SectionTypes.MatchAt(texts, i) is { } k && !IsDirectiveAt(texts, i))
                {
                    keyword = k;
                    keywordIndex = i;
                }
            }

            var vocabulary = keyword is not null || u.Any(t => t.Number is > 0) || Enumerable.Range(0, texts.Count).Any(i => IsDirectiveAt(texts, i));
            if (vocabulary)
                parsed.Add((u, keywordIndex, keyword));
        }

        var result = new List<Candidate>();
        for (var ci = 0; ci < parsed.Count; ci++)
        {
            var (u, keywordIndex, keyword) = parsed[ci];
            var call = new GuideCall
            {
                Id = Section.NewId(),
                Text = string.Join(' ', u.Select(t => t.Word).Distinct().Select(w => w.Text.Trim())),
                Start = u[0].Word.Start,
                End = u[^1].Word.End,
                Confidence = Math.Round(u.Select(t => t.Word.Confidence).Distinct().Min(), 3),
            };

            if (keyword is null)
            {
                result.Add(new Candidate(call, SectionTypes.Keywords[0], null, string.Empty, call.Confidence, 0));
                continue;
            }

            // A number right after the keyword names the section ("verse two"), unless it starts a count ("chorus one two three four").
            var afterKeyword = keywordIndex + keyword.Words.Length;
            int? number = null;
            var numberToken = afterKeyword < u.Count ? u[afterKeyword] : null;
            if (numberToken?.Number is { } n and > 0 and <= 20 && !StartsCount(u, afterKeyword))
                number = n;

            var name = number is { } num ? $"{keyword.DisplayName} {num.ToString(CultureInfo.InvariantCulture)}" : keyword.DisplayName;

            // Count words after the call, within 2 bars and before the next section call (SEC-14).
            var keywordEnd = (number is not null ? numberToken!.Word : u[afterKeyword - 1].Word).End;
            var countWords = u.Skip(afterKeyword + (number is null ? 0 : 1)).Where(t => t.Number is > 0).Select(t => t.Word).ToList();
            var callEnd = call.End;
            for (var cj = ci + 1; cj < parsed.Count; cj++)
            {
                var next = parsed[cj];
                if (next.Keyword is not null || next.Tokens[0].Word.Start > callEnd + 2 * grid.BarLength)
                    break;
                countWords.AddRange(next.Tokens.Where(t => t.Number is > 0).Select(t => t.Word));
            }

            double start;
            double certainty;
            if (countWords.Count > 0)
            {
                start = grid.BarLineAfter(countWords.Max(w => w.End), SpillBeats);
                certainty = 1.0;
            }
            else
            {
                start = grid.BarLineAfter(Math.Max(keywordEnd, call.End), SpillBeats);
                certainty = 0.8;
            }

            // First-bar rule: a call inside bar 1 over music that already sounds starts at bar 1.
            var bar1 = grid.FirstDownbeatSec;
            if (call.Start >= bar1 - 0.001 && call.Start < bar1 + grid.BarLength && musicSounds(bar1, bar1 + grid.BarLength))
            {
                start = bar1;
                certainty = 0.6;
            }

            if (start >= durationSec - 0.001 && durationSec > 0)
            {
                result.Add(new Candidate(call, keyword, number, name, call.Confidence, 0));
                continue;
            }

            var keywordWords = u.Skip(keywordIndex).Take(keyword.Words.Length + (number is null ? 0 : 1)).Select(t => t.Word.Confidence);
            var wordConfidence = keywordWords.Min();
            result.Add(new Candidate(call with { SectionName = name, SectionStartSec = Math.Round(start, 6) }, keyword, number, name, wordConfidence, certainty));
        }

        return result;
    }

    private static bool StartsCount(List<Token> u, int index) =>
        index + 1 < u.Count && u[index].Number is { } a && u[index + 1].Number is { } b && b == a + 1;

    private static bool IsDirectiveAt(List<string> texts, int index)
    {
        foreach (var d in Directives)
        {
            if (index + d.Length > texts.Count)
                continue;
            var match = true;
            for (var j = 0; j < d.Length && match; j++)
                match = texts[index + j] == d[j];
            if (match)
                return true;
        }

        return false;
    }

    private static int? ParseNumber(string token)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n;
        var i = Array.IndexOf(NumberWords, token);
        return i >= 0 ? i : null;
    }

    private static int Specificity(Candidate c) => (c.Number is not null ? 2 : 0) + (c.Keyword.Type != SectionType.Other ? 1 : 0);

    private static bool SameName(Candidate a, Candidate b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n.ToString(CultureInfo.InvariantCulture)} {word}s";
}
