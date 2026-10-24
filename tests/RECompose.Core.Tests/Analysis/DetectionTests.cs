// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Analysis;
using RECompose.Core.Sections;
using RECompose.Core.Tests.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Analysis;

// SEC-13…17 on fixture B (AT-35, AT-37) and SEC-1 on synthetic click onsets.
public class DetectionTests
{
    private const string TitleB = "Come Ye Sinners-The Worship Initiative, Vol. 22";
    private static readonly TempoGrid B = SectionTests.B;

    /// <summary>
    /// Fixture B's guide as a recogniser would report it: 12 section calls spoken in the bar
    /// before each section (on beat 2, or on beat 1 when a count follows), 4 counts and
    /// 3 directives (19 calls). The Verse 3 call is
    /// quiet, so its words come back with low confidence.
    /// </summary>
    internal static List<GuideWord> FixtureBWords()
    {
        var words = new List<GuideWord>();
        var beat = B.BeatLength;

        void Say(double start, double confidence, params string[] text)
        {
            foreach (var w in text)
            {
                words.Add(new GuideWord(w, start, start + 0.35, confidence));
                start += 0.42;
            }
        }

        var calls = new (int Bar, string[] Text)[]
        {
            (2, ["Verse", "one."]), (11, ["Chorus."]), (19, ["Verse", "2."]), (27, ["Verse", "three."]), (35, ["Chorus."]),
            (43, ["Verse", "four."]), (51, ["Verse", "five."]), (59, ["Chorus."]), (67, ["Chorus."]), (75, ["Tag."]),
            (77, ["Outro."]), (80, ["Ending."]),
        };
        int[] counted = [11, 35, 59, 80];
        foreach (var (bar, text) in calls)
            Say(B.BarToTime(bar - 1) + (counted.Contains(bar) ? 0.05 : beat), bar == 27 ? 0.5 : 0.96, text);

        // Counts after four calls: "one two three four", a separate utterance in the same bar.
        foreach (var bar in counted)
            Say(B.BarToTime(bar - 1) + 1.05, 0.9, "1,", "2,", "3,", "4.");

        // Directives in the middle of sections.
        Say(B.BarToTime(55) + beat, 0.9, "All", "in.");
        Say(B.BarToTime(63) + beat, 0.9, "Drums.");
        Say(B.BarToTime(71) + beat, 0.9, "Last", "time.");
        return words.OrderBy(w => w.Start).ToList();
    }

    private static bool Silent(double from, double to) => false;

    [Fact]
    public void AT35_detects_fixture_B_from_its_guide()
    {
        var result = SectionDetector.Detect(FixtureBWords(), B, SectionTests.DurationB, Silent);
        var infos = SectionList.Describe(result.Sections, B, SectionTests.DurationB);

        Assert.Equal(19, result.Calls.Count);
        Assert.Equal(
            ["Verse 1", "Chorus 1", "Verse 2", "Verse 3", "Chorus 2", "Verse 4", "Verse 5", "Chorus 3", "Chorus 4", "Tag", "Outro", "Ending"],
            infos.Select(i => i.Label));
        Assert.Equal(3.334, infos[0].StartSec, 3);
        Assert.Equal(2, infos[0].ChartBar);
        Assert.Equal(253.334, infos[10].StartSec, 3);
        Assert.Equal(77, infos[10].ChartBar);

        var flagged = Assert.Single(infos, i => i.Section.NeedsReview);
        Assert.Equal("Verse 3", flagged.Label);
        Assert.All(infos, i => Assert.Equal(SectionOrigin.Guide, i.Section.Origin));
        Assert.Equal(1.0 * 0.96, infos[1].Section.Confidence!.Value, 3); // Chorus with a count
        Assert.Equal(0.8 * 0.96, infos[0].Section.Confidence!.Value, 3); // Verse 1, no count

        Assert.Equal(
            $"{TitleB}: 12 sections: Verse 1, Chorus 1, Verse 2, Verse 3, Chorus 2, Verse 4, Verse 5, Chorus 3, Chorus 4, Tag, Outro, Ending. " +
            "Click: 72 BPM, 4 beats per bar. Guide: 12 sections from 19 spoken calls. 1 needs review.",
            SectionDetector.Summary(TitleB, result.Sections, B, result));

        // Each section links to its written call (AUTO-30).
        var verse1Call = result.Calls.Single(c => c.UsedForSectionId == infos[0].Id);
        Assert.Equal("Verse one.", verse1Call.Text);
    }

    [Fact]
    public void AT37_redetect_keeps_edited_sections()
    {
        var first = SectionDetector.Detect(FixtureBWords(), B, SectionTests.DurationB, Silent);
        var tag = first.Sections.Single(s => s.Name == "Tag");
        var edited = SectionList.Rename(first.Sections, tag.Id, "Tag (big)");

        var again = SectionDetector.Detect(FixtureBWords(), B, SectionTests.DurationB, Silent, edited);

        Assert.Equal(12, again.Sections.Count);
        Assert.Contains(again.Sections, s => s.Name == "Tag (big)" && s.Id == tag.Id);
        Assert.DoesNotContain(again.Sections, s => s.Name == "Tag");
        Assert.Equal(1, again.KeptCount);
        Assert.EndsWith("Kept 1 edited section.", SectionDetector.Summary(TitleB, again.Sections, B, again));

        // Untouched guide sections were replaced (new ids).
        Assert.DoesNotContain(again.Sections, s => s.Name == "Verse 1" && first.Sections.Any(f => f.Id == s.Id));
    }

    [Fact]
    public void Calls_on_the_same_bar_merge_and_repeats_within_two_bars_count_once()
    {
        var words = new List<GuideWord>
        {
            new("Chorus", B.BarToTime(10) + 0.2, B.BarToTime(10) + 0.6, 0.9),
            new("Chorus", B.BarToTime(10) + 1.4, B.BarToTime(10) + 1.8, 0.9),     // same bar again
            new("Chorus", B.BarToTime(11) + 1.0, B.BarToTime(11) + 1.4, 0.9),     // the next bar: within 2 bars
            new("Bridge", B.BarToTime(20) + 0.2, B.BarToTime(20) + 0.6, 0.9),
            new("Bridge", B.BarToTime(20) + 1.4, B.BarToTime(20) + 1.8, 0.9),
            new("two", B.BarToTime(20) + 2.0, B.BarToTime(20) + 2.3, 0.9),        // "Bridge two": more specific
        };

        var result = SectionDetector.Detect(words, B, SectionTests.DurationB, Silent);
        Assert.Equal(["Chorus", "Bridge 2"], result.Sections.Select(s => s.Name));
        Assert.Equal(B.BarToTime(11), result.Sections[0].StartSec, 6);
    }

    [Fact]
    public void Unnumbered_verses_are_numbered_in_order_and_counts_move_the_start()
    {
        var beat = B.BeatLength;
        var words = new List<GuideWord>
        {
            new("Verse", B.BarToTime(1) + beat, B.BarToTime(1) + beat + 0.4, 0.9),
            new("Verse", B.BarToTime(9) + beat, B.BarToTime(9) + beat + 0.4, 0.9),
            // A count that runs into the next bar puts the start after it.
            new("one", B.BarToTime(10) + 2 * beat, B.BarToTime(10) + 2 * beat + 0.3, 0.9),
            new("two", B.BarToTime(10) + 3 * beat, B.BarToTime(10) + 3 * beat + 0.3, 0.9),
            new("three", B.BarToTime(11), B.BarToTime(11) + 0.3, 0.9),
            new("four", B.BarToTime(11) + beat, B.BarToTime(11) + beat + 0.3, 0.9),
        };

        var result = SectionDetector.Detect(words, B, SectionTests.DurationB, Silent);
        Assert.Equal(["Verse 1", "Verse 2"], result.Sections.Select(s => s.Name));
        Assert.Equal(B.BarToTime(2), result.Sections[0].StartSec, 6);
        Assert.Equal(B.BarToTime(12), result.Sections[1].StartSec, 6);
        Assert.Equal(0.9, result.Sections[1].Confidence!.Value, 3);
    }

    [Fact]
    public void First_bar_rule_starts_at_bar_1_over_sounding_music()
    {
        var words = new List<GuideWord> { new("Intro", B.BarToTime(1) + 0.3, B.BarToTime(1) + 0.7, 0.95) };

        var quiet = SectionDetector.Detect(words, B, SectionTests.DurationB, Silent);
        Assert.Equal(B.BarToTime(2), quiet.Sections[0].StartSec, 6);

        var playing = SectionDetector.Detect(words, B, SectionTests.DurationB, (from, to) => from < 1);
        Assert.Equal(B.BarToTime(1), playing.Sections[0].StartSec, 6);
        Assert.Equal(0.6 * 0.95, playing.Sections[0].Confidence!.Value, 3);
        Assert.True(playing.Sections[0].NeedsReview);
    }

    [Fact]
    public void A_call_that_spills_onto_the_downbeat_lands_on_it()
    {
        var words = new List<GuideWord> { new("Chorus", B.BarToTime(11) - 0.5, B.BarToTime(11) + 0.1, 0.9) };
        var result = SectionDetector.Detect(words, B, SectionTests.DurationB, Silent);
        Assert.Equal(B.BarToTime(11), result.Sections[0].StartSec, 6);
    }

    [Fact]
    public void Words_that_are_not_calls_are_ignored()
    {
        var words = new List<GuideWord>
        {
            new("Here", 10, 10.3, 0.9), new("we", 10.35, 10.5, 0.9), new("go", 10.55, 10.8, 0.9),
        };
        var result = SectionDetector.Detect(words, B, SectionTests.DurationB, Silent);
        Assert.Empty(result.Calls);
        Assert.Empty(result.Sections);
        Assert.StartsWith("X: no sections found.", SectionDetector.Summary("X", result.Sections, B, result));
    }

    // ---- SEC-1 click analysis -----------------------------------------------------------

    private static List<ClickOnset> Click(double bpm, int beatsPerBar, double first, int beats, int pickups = 0, double jitter = 0, int seed = 1)
    {
        var random = new Random(seed);
        var p = 60 / bpm;
        var onsets = new List<ClickOnset>();
        for (var k = -pickups; k < beats; k++)
        {
            var accent = k >= 0 && k % beatsPerBar == 0;
            var t = first + k * p + (random.NextDouble() * 2 - 1) * jitter;
            onsets.Add(new ClickOnset(t, accent ? -6 + random.NextDouble() * 0.2 : -10 + random.NextDouble() * 0.2, accent ? 2400 : 1600));
        }

        return onsets;
    }

    [Fact]
    public void SEC1_fits_tempo_meter_and_downbeat()
    {
        var grid = ClickFit.Fit(Click(72, 4, 0.0007, 328, jitter: 0.0005))!;
        Assert.InRange(grid.Bpm, 71.99, 72.01);
        Assert.Equal(4, grid.BeatsPerBar);
        Assert.Equal(0.0007, grid.FirstDownbeatSec, 3);
        Assert.False(grid.MeterAssumed);
        Assert.Equal("72", Format.Bpm(grid.Bpm));
    }

    [Fact]
    public void SEC1_pickup_beats_come_before_the_first_downbeat()
    {
        var grid = ClickFit.Fit(Click(72, 3, 1.67, 300, pickups: 2))!;
        Assert.Equal(3, grid.BeatsPerBar);
        Assert.Equal(1.67, grid.FirstDownbeatSec, 3);
        Assert.Equal(2, grid.PickupBeats);
    }

    [Fact]
    public void SEC1_survives_missing_clicks_and_stray_onsets()
    {
        var onsets = Click(128, 4, 0.5, 400, jitter: 0.001);
        onsets.RemoveAll(o => o.Time > 60 && o.Time < 75);                  // the click drops out for a breakdown
        onsets.Add(new ClickOnset(10.2, -3, 900));                           // a stray hit between beats
        onsets.Add(new ClickOnset(0.1, -20, 500));                           // count-in breath
        var grid = ClickFit.Fit(onsets)!;
        Assert.InRange(grid.Bpm, 127.98, 128.02);
        Assert.Equal(4, grid.BeatsPerBar);
        Assert.Equal(0.5, grid.FirstDownbeatSec, 2);
    }

    [Fact]
    public void SEC1_without_accents_the_meter_is_assumed()
    {
        var onsets = Enumerable.Range(0, 100).Select(k => new ClickOnset(0.25 + k * 0.5, -8, 2000)).ToList();
        var grid = ClickFit.Fit(onsets)!;
        Assert.Equal(120, grid.Bpm, 2);
        Assert.Equal(4, grid.BeatsPerBar);
        Assert.True(grid.MeterAssumed);
        Assert.Equal(0.25, grid.FirstDownbeatSec, 3);
    }

    [Fact]
    public void SEC1_too_few_onsets_is_no_click()
    {
        Assert.Null(ClickFit.Fit([new ClickOnset(1, -6, 2000), new ClickOnset(2, -6, 2000)]));
    }
}
