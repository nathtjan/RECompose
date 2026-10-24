// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Sections;

// DM-20/21, SEC-41…43, SEC-50…53 and the as-written readouts (PB-30…35).
public class SectionTests
{
    // Fixture A: 72 BPM, 3/4 (bar = 2.5 s), first downbeat 0.02 s, 310.02 s (124 bars).
    private static readonly TempoGrid A = new(72, 3, 0.02);
    private const double DurationA = 310.02;

    // Fixture B: 72 BPM, 4/4 (bar = 3.333 s), first downbeat 0.0007 s, 82 bars.
    internal static readonly TempoGrid B = new(72, 4, 0.0007);
    internal const double DurationB = 0.0007 + 82 * 10.0 / 3;

    internal static List<Section> FixtureASections() =>
    [
        .. new (string Name, int Bar)[]
        {
            ("Intro", 1), ("Verse 1", 11), ("Chorus", 27), ("Vamp", 41), ("Verse 2", 49), ("Chorus", 65),
            ("Interlude", 79), ("Bridge", 87), ("Chorus", 103), ("Ending", 117),
        }.Select(s => Section.Create(s.Name, A.BarToTime(s.Bar), SectionOrigin.Guide)),
    ];

    [Theory]
    [InlineData("Verse 2", SectionType.Verse)]
    [InlineData("Pre-Chorus", SectionType.PreChorus)]
    [InlineData("pre chorus 2", SectionType.PreChorus)]
    [InlineData("Post Chorus", SectionType.PostChorus)]
    [InlineData("Chorus", SectionType.Chorus)]
    [InlineData("Refrain", SectionType.Chorus)]
    [InlineData("Down Chorus", SectionType.Breakdown)]
    [InlineData("Inst", SectionType.Instrumental)]
    [InlineData("Guitar Solo", SectionType.Instrumental)]
    [InlineData("Turn", SectionType.Turnaround)]
    [InlineData("Tag (big)", SectionType.Tag)]
    [InlineData("End", SectionType.Ending)]
    [InlineData("Hallelujah", SectionType.Other)]
    public void SEC42_types_come_from_the_first_keyword(string name, SectionType type)
    {
        Assert.Equal(type, SectionTypes.FromName(name));
    }

    [Fact]
    public void SEC41_repeated_names_get_occurrence_numbers_and_badges()
    {
        var infos = SectionList.Describe(FixtureASections(), A, DurationA);

        Assert.Equal(
            ["Intro", "Verse 1", "Chorus 1", "Vamp", "Verse 2", "Chorus 2", "Interlude", "Bridge", "Chorus 3", "Ending"],
            infos.Select(i => i.Label));
        Assert.Equal(["I", "V1", "C1", "VA", "V2", "C2", "IL", "B", "C3", "E"], infos.Select(i => i.Badge));
        Assert.Equal("section-chorus", infos[2].ColorToken);

        var chorus1 = infos[2];
        Assert.Equal(27, chorus1.ChartBar);
        Assert.Equal(14, chorus1.LengthBars);
        Assert.Equal(65.02, chorus1.StartSec, 6);
        Assert.Equal(100.02, chorus1.EndSec, 6);
        Assert.Equal(8, infos[^1].LengthBars); // Ending 117 to the end of bar 124
    }

    [Fact]
    public void SEC43_a_type_override_survives_a_rename()
    {
        var s = Section.Create("Hallelujah", 0.02, SectionOrigin.Manual);
        var list = SectionList.SetType([s], s.Id, SectionType.Chorus);
        Assert.Equal(SectionType.Chorus, list[0].Type);
        list = SectionList.Rename(list, s.Id, "Hallelujah 2");
        Assert.Equal(SectionType.Chorus, list[0].Type);

        var v = Section.Create("Section 1", 0.02, SectionOrigin.Marked);
        Assert.Equal(SectionType.Bridge, SectionList.Rename([v], v.Id, "Bridge")[0].Type);
    }

    [Fact]
    public void SEC50_moves_snap_to_bars_or_beats_and_stay_between_neighbours()
    {
        var sections = FixtureASections();
        var verse = sections[1];

        var moved = SectionList.Move(sections, verse.Id, A.BarToTime(12) + 0.4, A, DurationA);
        Assert.Equal(A.BarToTime(12), moved[1].StartSec, 6);
        Assert.True(moved[1].Edited);

        var beat = SectionList.Move(sections, verse.Id, A.BarToTime(11) - 0.8, A, DurationA, snapToBeat: true);
        Assert.Equal(A.BarToTime(11) - A.BeatLength, beat[1].StartSec, 6); // a pickup beat

        // Past the next section's start: refused.
        var refused = SectionList.Move(sections, verse.Id, A.BarToTime(30), A, DurationA);
        Assert.Equal(A.BarToTime(11), refused[1].StartSec, 6);
    }

    [Fact]
    public void SEC50_split_merge_and_delete()
    {
        var sections = FixtureASections();
        var (split, added) = SectionList.Split(sections, A.BarToTime(19) + 0.3, A, DurationA);
        Assert.NotNull(added);
        Assert.Equal(11, split.Count);
        Assert.Equal("Verse 1", added!.Name);
        Assert.Equal(A.BarToTime(19), added.StartSec, 6);

        var labels = SectionList.Describe(split, A, DurationA).Select(i => i.Label).ToList();
        Assert.Equal(["Verse 1 1", "Verse 1 2"], labels.Skip(1).Take(2)); // same name twice: numbered

        var merged = SectionList.MergeWithNext(split, split[1].Id);
        Assert.Equal(10, merged.Count);
        Assert.Equal(16, SectionList.Describe(merged, A, DurationA)[1].LengthBars);

        var deleted = SectionList.Delete(merged, merged[3].Id);
        Assert.DoesNotContain(deleted, s => s.Name == "Vamp");
    }

    [Fact]
    public void SEC52_M_marks_at_the_nearest_bar_and_names_it_from_the_nearest_call()
    {
        // AT-38: fixture B, sections removed, M at 33.9 s → chart bar 11 (33.334 s), named "Chorus".
        var calls = new List<GuideCall>
        {
            new() { Id = "c1", Text = "Verse one", SectionName = "Verse 1", SectionStartSec = B.BarToTime(2) },
            new() { Id = "c2", Text = "Chorus", SectionName = "Chorus", SectionStartSec = B.BarToTime(11) },
        };

        var result = SectionList.Mark([], 33.9, B, DurationB, calls);
        Assert.NotNull(result.Added);
        Assert.Equal(11, result.ChartBar);
        Assert.Equal(33.334, result.Added!.StartSec, 3);
        Assert.Equal("Chorus", result.Added.Name);
        Assert.Equal(SectionOrigin.Marked, result.Added.Origin);
        Assert.Equal("c2", result.Added.CallId);

        // Pressing M there again: nothing added, the existing section flashes.
        var again = SectionList.Mark(result.Sections, 34.2, B, DurationB, calls);
        Assert.True(again.AlreadyThere);
        Assert.Single(again.Sections);

        // Far from any call: "Section {n}".
        var far = SectionList.Mark(result.Sections, B.BarToTime(40), B, DurationB, calls);
        Assert.Equal("Section 2", far.Added!.Name);
    }

    [Fact]
    public void PB30_31_35_readouts_follow_the_written_sections()
    {
        var plan = PlanBuilder.Build(A, SectionList.Describe(FixtureASections(), A, DurationA), DurationA, null);

        // The 0.02 s before the first section is shorter than half a beat: the plan starts at S0 (PB-41).
        Assert.Equal(DurationA - 0.02, plan.DurationSec, 6);
        var rest = plan.At(0);
        Assert.Equal("INTRO", rest.NowLabel);
        Assert.Equal("bar 1 / 10", rest.NowDetail);
        Assert.Equal("VERSE 1", rest.NextLabel);
        Assert.Equal("AS WRITTEN", rest.NextTag);
        Assert.Equal("in 10 bars", rest.Countdown);
        Assert.Equal("bar 1.1 / 124", rest.SongBar);

        // Source 78.52 s: chart bar 32.2, the first chorus.
        var r = plan.At(plan.PlanAt(78.52)!.Value);
        Assert.Equal("CHORUS 1", r.NowLabel);
        Assert.Equal("bar 6 / 14", r.NowDetail);
        Assert.Equal("VAMP", r.NextLabel);
        Assert.Equal("in 8 bars, 2 beats", r.Countdown);
        Assert.Equal("bar 32.2 / 124", r.SongBar);
        Assert.Equal("chart bar 32.2", r.ChartBar);

        var chorus = A.BarToTime(27) - 0.02; // plan time of the first chorus
        Assert.Equal("in 1 beat", plan.Countdown(chorus - A.BeatLength, chorus));
        Assert.Equal("now", plan.Countdown(chorus, chorus));

        var last = plan.At(DurationA - 1);
        Assert.Equal("ENDING", last.NowLabel);
        Assert.Equal("END OF SONG", last.NextLabel);
    }

    [Fact]
    public void PB32_song_bars_count_from_the_first_section_after_a_lead_in()
    {
        // Fixture B: Verse 1 at chart bar 2; bar 1 is a lead-in.
        var sections = new List<Section> { Section.Create("Verse 1", B.BarToTime(2), SectionOrigin.Guide), Section.Create("Chorus", B.BarToTime(11), SectionOrigin.Guide) };
        var plan = PlanBuilder.Build(B, SectionList.Describe(sections, B, DurationB), DurationB, null);

        Assert.Equal(81, plan.TotalBars);
        var rest = plan.At(0);
        Assert.Equal("LEAD-IN", rest.NowLabel);
        Assert.Equal("beat −4", rest.NowDetail);
        Assert.Equal("VERSE 1", rest.NextLabel);
        Assert.Equal("in 1 bar", rest.Countdown);

        var verse = plan.At(B.BarToTime(4) + 0.1);
        Assert.Equal("bar 3 / 9", verse.NowDetail);
        Assert.Equal("bar 3.1 / 81", verse.SongBar);
        Assert.Equal("chart bar 4.1", verse.ChartBar);
    }

    [Fact]
    public void Without_sections_the_readouts_are_those_of_milestone_1()
    {
        var plan = PlanBuilder.Build(A, [], DurationA, null);
        var r = plan.At(78.52);
        Assert.Equal(string.Empty, r.NowLabel);
        Assert.Equal("bar 32.2 / 124", r.SongBar);
        Assert.False(plan.HasSections);
    }

    [Fact]
    public void Resnap_moves_starts_onto_a_new_grid()
    {
        var sections = FixtureASections();
        var shifted = SectionList.Resnap(sections, A with { FirstDownbeatSec = 0.5 });
        Assert.Equal(0.5, shifted[0].StartSec, 6);
        Assert.Equal(25.5, shifted[1].StartSec, 6);
    }

    [Fact]
    public void Sections_and_analysis_survive_a_round_trip()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-sections-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DataStore(root);
            var written = FixtureASections();
            var sections = SectionList.SetType(written, written[0].Id, SectionType.Other);
            var song = new Song
            {
                Id = "s", Title = "Champion", Source = new SongSource { Path = "/x" },
                Sections = sections,
                TempoSource = TempoSource.Click,
                ClickAnalysis = new ClickAnalysis { TrackId = "click", Bpm = 72, BeatsPerBar = 3, FirstDownbeatSec = 0.02, Onsets = 372, OnsetsUsed = 372 },
                GuideAnalysis = new GuideAnalysis
                {
                    TrackId = "guide", Model = "ggml-base.en",
                    Words = [new GuideWord("Chorus", 62.1, 62.5, 0.93)],
                    Calls = [new GuideCall { Id = "c", Text = "Chorus", SectionName = "Chorus", SectionStartSec = 65.02 }],
                },
            };
            store.SaveSong(song);
            var loaded = store.LoadSong("s")!;

            Assert.Equal(10, loaded.Sections.Count);
            Assert.Equal(SectionType.Chorus, loaded.Sections[2].Type);
            Assert.True(loaded.Sections[0].TypeOverridden);
            Assert.Equal(TempoSource.Click, loaded.TempoSource);
            Assert.Equal(3, loaded.ClickAnalysis!.BeatsPerBar);
            Assert.Equal(0.93, loaded.GuideAnalysis!.Words[0].Confidence, 3);
            Assert.Equal(65.02, loaded.GuideAnalysis.Calls[0].SectionStartSec!.Value, 3);
            Assert.Contains("\"type\": \"Chorus\"", File.ReadAllText(Path.Combine(root, "songs", "s.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
