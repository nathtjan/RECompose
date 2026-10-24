// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Tests.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Arrangements;

// ARR-10…60, DM-30, PB-40/41 and the readouts of the arrangement plan, on fixtures A and B (15-acceptance-tests).
public class ArrangementTests
{
    private static readonly TempoGrid A = new(72, 3, 0.02);
    private const double DurationA = 310.02;
    private static readonly TempoGrid B = SectionTests.B;
    private const double DurationB = SectionTests.DurationB;

    private static List<SectionInfo> InfosA() => SectionList.Describe(SectionTests.FixtureASections(), A, DurationA);

    // Fixture B after Detect (AT-35): Verse 1…5, Chorus ×4, Tag, Outro, Ending.
    private static List<SectionInfo> InfosB() => SectionList.Describe(
        new (string Name, int Bar)[]
        {
            ("Verse 1", 2), ("Chorus", 11), ("Verse 2", 19), ("Verse 3", 27), ("Chorus", 35), ("Verse 4", 43),
            ("Verse 5", 51), ("Chorus", 59), ("Chorus", 67), ("Tag", 75), ("Outro", 77), ("Ending", 80),
        }.Select(s => Section.Create(s.Name, B.BarToTime(s.Bar), SectionOrigin.Guide)), B, DurationB);

    private static SectionInfo Find(List<SectionInfo> infos, string label) => infos.Single(i => i.Label == label);

    // Fixture A's "Demo" variant: Intro, then Chorus 1 twice.
    private static Arrangement Demo(List<SectionInfo> infos) => new()
    {
        Id = "demo",
        Name = "Demo",
        Entries =
        [
            ArrangementEntry.ForSection(Find(infos, "Intro").Id),
            ArrangementEntry.ForSection(Find(infos, "Chorus 1").Id) with { Repeat = new RepeatSpec { Count = 2 } },
        ],
    };

    private static List<Track> TracksA() =>
    [
        new() { Id = "click", Name = "Click Track", File = "click.wav", Role = TrackRole.Click, Group = TrackGroups.Click },
        new() { Id = "guide", Name = "Guide", File = "guide.wav", Role = TrackRole.Guide, Group = TrackGroups.Guide },
        new() { Id = "drums", Name = "Drums", File = "drums.wav", Group = TrackGroups.Drums },
        new() { Id = "perc", Name = "Perc", File = "perc.wav", Group = TrackGroups.Percussion },
        new() { Id = "keys", Name = "Keys 1", File = "keys.wav", Group = TrackGroups.Keys },
    ];

    // ---- Plan builder and readouts ---------------------------------------------------

    [Fact]
    public void AT1_the_demo_variant_at_rest()
    {
        var infos = InfosA();
        var plan = PlanBuilder.Build(A, infos, DurationA, Demo(infos));

        Assert.Equal(["Intro", "Chorus 1", "Chorus 1", "Ending"], plan.Items.Select(i => i.Label));
        Assert.Equal(PlanItemKind.Tail, plan.Items[^1].Kind);
        Assert.Equal(38, plan.TotalBars);
        Assert.Equal(100, plan.DurationSec, 6);

        var rest = plan.At(0);
        Assert.Equal("INTRO", rest.NowLabel);
        Assert.Equal("bar 1 / 10", rest.NowDetail);
        Assert.Equal("CHORUS 1", rest.NextLabel); // not "as written · Verse 1" as in the demo app
        Assert.Equal("ARRANGEMENT", rest.NextTag);
        Assert.Equal("in 10 bars", rest.Countdown);
        Assert.Equal("bar 1.1 / 38", rest.SongBar);
        Assert.Equal("0:00 / −1:40", rest.Time);
    }

    [Fact]
    public void AT2_playing_the_first_pass_of_the_chorus()
    {
        var infos = InfosA();
        var plan = PlanBuilder.Build(A, infos, DurationA, Demo(infos));

        // Source 78.52 s is chart bar 32.2, in the first pass.
        var t = plan.PlanAt(78.52)!.Value;
        Assert.Equal(38.5, t, 6);
        var r = plan.At(t);
        Assert.Equal("CHORUS 1", r.NowLabel);
        Assert.Equal("pass 1 of 2", r.NowPass);
        Assert.Equal("bar 6 / 14", r.NowDetail);
        Assert.Equal("bar 16.2 / 38", r.SongBar);
        Assert.Equal("CHORUS 1 (pass 2)", r.NextLabel);
        Assert.Equal("in 8 bars, 2 beats", r.Countdown);
        Assert.Equal("0:38 / −1:01", r.Time);
        Assert.Equal("chart bar 32.2", r.ChartBar);

        // The second pass is a new item that plays the same audio.
        Assert.Equal(78.52, plan.SourceAt(t)!.Value, 6);
        Assert.Equal(78.52, plan.SourceAt(t + 35)!.Value, 6);
        Assert.Equal(t + 35, plan.PlanAt(78.52, notBeforePlanSec: 40)!.Value, 6);
        Assert.Equal("pass 2 of 2", plan.At(t + 35).NowPass);
        Assert.Equal("ENDING", plan.At(t + 35).NextLabel);
    }

    [Fact]
    public void The_tail_rings_out_unless_the_ending_cuts_or_the_file_has_no_audio_left()
    {
        var infos = InfosA();
        var cut = Demo(infos) with
        {
            Entries = [.. Demo(infos).Entries.Take(1), Demo(infos).Entries[1] with { Ending = EndingMode.Cut }],
        };
        Assert.Equal(PlanItemKind.Section, PlanBuilder.Build(A, infos, DurationA, cut).Items[^1].Kind);

        // The Ending section reaches the end of the file: nothing to ring out (AT-10).
        var ending = new Arrangement { Id = "e", Name = "E", Entries = [ArrangementEntry.ForSection(Find(infos, "Ending").Id)] };
        Assert.DoesNotContain(PlanBuilder.Build(A, infos, DurationA, ending).Items, i => i.Kind == PlanItemKind.Tail);
    }

    [Fact]
    public void AT10_shorthand_on_fixture_B_gives_the_expected_plan()
    {
        var infos = InfosB();
        var result = QuickOrder.Parse("V1 C V2 C O E", infos);
        Assert.False(result.HasProblems);

        var arrangement = new Arrangement { Id = "x", Name = "Short", Entries = [.. result.Entries] };
        Assert.Equal("1. Verse 1 · 2. Chorus 1 · 3. Verse 2 · 4. Chorus 2 · 5. Outro · 6. Ending", ArrangementSummary.Labels(arrangement, infos));
        Assert.Equal(35, Find(infos, "Chorus 2").ChartBar); // the chorus after Verse 2

        var plan = PlanBuilder.Build(B, infos, DurationB, arrangement);
        Assert.Equal(39, plan.TotalBars);
        Assert.Equal(PlanItemKind.LeadIn, plan.Items[0].Kind); // the track count-in
        Assert.Equal(6, plan.Items.Count(i => i.Kind == PlanItemKind.Section));
        Assert.DoesNotContain(plan.Items, i => i.Kind == PlanItemKind.Tail); // the Ending reaches the file end
        Assert.Equal("0:00 / −2:13", plan.At(0).Time);
        Assert.Equal("39 bars · 2:13", ArrangementSummary.Totals(plan));
    }

    [Fact]
    public void AT11_ranges_repeats_and_key_lifts()
    {
        var infos = InfosB();
        var result = QuickOrder.Parse("V1 C(1-4) V2 C4x2 C4+2 E", infos);
        Assert.False(result.HasProblems);
        var plan = PlanBuilder.Build(B, infos, DurationB, new Arrangement { Id = "x", Name = "x", Entries = [.. result.Entries] });

        var sections = plan.Items.Where(i => i.Kind == PlanItemKind.Section).ToList();
        Assert.Equal(["Verse 1", "Chorus 1", "Verse 2", "Chorus 4", "Chorus 4", "Chorus 4", "Ending"], sections.Select(i => i.Label));
        Assert.Equal(4, sections[1].LengthBars);
        Assert.True(sections[1].IsPartial);
        Assert.Equal("Chorus 1 · 1–4", sections[1].RangeLabel);
        Assert.Equal(B.BarToTime(11) + 4 * B.BarLength, sections[1].SrcEnd, 6);
        Assert.Equal([0, 0, 0, 0, 0, 2, 0], sections.Select(i => i.Transpose));
        Assert.Equal([1, 1, 1, 1, 2, 1, 1], sections.Select(i => i.Pass));
        Assert.Equal("1. Verse 1 · 2. Chorus 1 · 1–4 · 3. Verse 2 · 4. Chorus 4 ×2 · 5. Chorus 4 (+2) · 6. Ending", ArrangementSummary.Labels(new Arrangement { Id = "x", Name = "x", Entries = [.. result.Entries] }, infos));
    }

    [Fact]
    public void AT12_mutes_expand_groups_and_can_apply_to_the_first_pass_only()
    {
        var infos = InfosA();
        var chorus = Find(infos, "Chorus 1");
        var arrangement = new Arrangement
        {
            Id = "m",
            Name = "m",
            Entries =
            [
                ArrangementEntry.ForSection(Find(infos, "Verse 1").Id) with { Mutes = ArrangementEdit.DrumsOut() },
                ArrangementEntry.ForSection(chorus.Id) with { Repeat = new RepeatSpec { Count = 2 }, Mutes = ArrangementEdit.DrumsOut(firstPassOnly: true) },
                ArrangementEntry.ForSection(Find(infos, "Verse 2").Id) with { Mutes = ArrangementEdit.ClickAndGuideOnly() },
            ],
        };
        var plan = PlanBuilder.Build(A, infos, DurationA, arrangement, TracksA());

        Assert.Equal(["drums"], plan.Items[0].MutedTrackIds);
        Assert.Equal(["drums"], plan.Items[1].MutedTrackIds);
        Assert.Empty(plan.Items[2].MutedTrackIds); // second pass
        Assert.Equal(["drums", "perc", "keys"], plan.Items[3].MutedTrackIds); // click and guide keep playing
        Assert.Equal(["drums", "perc", "keys"], plan.Segments()[3].Mute);
    }

    [Fact]
    public void Fades_apply_to_the_first_and_last_pass_and_never_exceed_the_item()
    {
        var infos = InfosA();
        var arrangement = new Arrangement
        {
            Id = "f",
            Name = "f",
            Entries = [ArrangementEntry.ForSection(Find(infos, "Chorus 1").Id) with { Repeat = new RepeatSpec { Count = 2 }, FadeInBars = 2, FadeOutBars = 4 }],
        };
        var plan = PlanBuilder.Build(A, infos, DurationA, arrangement);
        Assert.Equal([5.0, 0.0], plan.Items.Take(2).Select(i => i.FadeInSec));
        Assert.Equal([0.0, 10.0], plan.Items.Take(2).Select(i => i.FadeOutSec));
    }

    [Fact]
    public void Ranges_are_clamped_to_the_section_and_removed_sections_are_skipped()
    {
        var infos = InfosA();
        var intro = Find(infos, "Intro");
        var arrangement = new Arrangement
        {
            Id = "r",
            Name = "r",
            Entries =
            [
                ArrangementEntry.ForSection(intro.Id) with { Range = new BarRange { FromBar = 8, ToBar = 40 } },
                ArrangementEntry.ForSection("gone"),
                ArrangementEntry.ForSection(intro.Id),
            ],
        };
        var plan = PlanBuilder.Build(A, infos, DurationA, arrangement);
        Assert.Equal(2, plan.Items.Count(i => i.Kind == PlanItemKind.Section));
        Assert.Equal(3, plan.Items[0].LengthBars); // bars 8–10 of 10
        Assert.Equal(intro.StartSec + 7 * A.BarLength, plan.Items[0].SrcStart, 6);
        Assert.Equal(intro.EndSec, plan.Items[0].SrcEnd, 6);
        Assert.Equal(0, plan.Items[1].EntryIndex - 2);
    }

    [Fact]
    public void Open_repeats_count_once_and_mark_the_remaining_time_open_ended()
    {
        var infos = InfosA();
        var arrangement = new Arrangement
        {
            Id = "o",
            Name = "o",
            Entries =
            [
                ArrangementEntry.ForSection(Find(infos, "Chorus 1").Id) with { Repeat = new RepeatSpec { UntilCued = true } },
                ArrangementEntry.ForSection(Find(infos, "Ending").Id),
            ],
        };
        var plan = PlanBuilder.Build(A, infos, DurationA, arrangement);
        Assert.True(plan.Items[0].IsOpen);
        Assert.Equal(1, plan.Items.Count(i => i.EntryId == arrangement.Entries[0].Id));
        Assert.True(plan.HasOpenEnd);
        Assert.EndsWith("+", plan.At(0).Time);
        Assert.Equal("until cued", plan.At(0).NowPass);
        Assert.EndsWith("+", ArrangementSummary.Totals(plan));
    }

    [Fact]
    public void Click_pad_and_hold_entries_are_planned_but_have_no_source()
    {
        var infos = InfosA();
        var arrangement = new Arrangement
        {
            Id = "g",
            Name = "g",
            Entries =
            [
                ArrangementEntry.ClickBars(4),
                ArrangementEntry.ForSection(Find(infos, "Intro").Id),
                ArrangementEntry.NewHold(),
                ArrangementEntry.PadBars(2) with { Note = "Pray" },
                ArrangementEntry.ForSection(Find(infos, "Ending").Id),
            ],
        };
        var plan = PlanBuilder.Build(A, infos, DurationA, arrangement);
        Assert.Equal([PlanItemKind.Click, PlanItemKind.Section, PlanItemKind.Hold, PlanItemKind.Pad, PlanItemKind.Section],
            plan.Items.Select(i => i.Kind));
        Assert.False(plan.Items[0].HasSource);
        Assert.Equal(10.0, plan.Items[0].Length, 6);
        Assert.Equal(0.0, plan.Items[2].Length);
        Assert.Null(plan.SourceAt(1));
        Assert.Equal("CLICK", plan.At(1).NowLabel);
        Assert.Equal("bar 1 / 4", plan.At(1).NowDetail);
        Assert.Equal("INTRO", plan.At(10).NowLabel);

        // The engine gets contiguous segments; the hold has no length and the click is silent.
        var segments = plan.Segments();
        Assert.Equal(4, segments.Count);
        Assert.Null(segments[0].Src);
        for (var i = 1; i < segments.Count; i++)
            Assert.Equal(segments[i - 1].End, segments[i].Start, 9);

        // The pad bars after the hold carry their note into the NEXT card (ARR-37).
        Assert.Equal("HOLD", plan.At(10).NextLabel);
        Assert.Equal("Pray", plan.At(10 + 25).NextNote == string.Empty ? plan.Items[3].Note : plan.At(10 + 25).NextNote);
    }

    [Fact]
    public void Notes_show_on_the_item_that_plays_and_under_next()
    {
        var infos = InfosA();
        var arrangement = new Arrangement
        {
            Id = "n",
            Name = "n",
            Entries =
            [
                ArrangementEntry.ForSection(Find(infos, "Intro").Id),
                ArrangementEntry.ForSection(Find(infos, "Chorus 1").Id) with { Note = "All in, big" },
            ],
        };
        var plan = PlanBuilder.Build(A, infos, DurationA, arrangement);
        Assert.Equal("All in, big", plan.At(1).NextNote);
        Assert.Equal("All in, big", plan.At(30).NowNote);
        Assert.Equal(string.Empty, plan.At(30).NextNote);
    }

    [Fact]
    public void Segments_are_contiguous_and_point_at_the_source()
    {
        var infos = InfosA();
        var plan = PlanBuilder.Build(A, infos, DurationA, Demo(infos));
        var segments = plan.Segments();
        Assert.Equal(4, segments.Count);
        Assert.Equal(0.02, segments[0].Src!.Value, 6);
        Assert.Equal(65.02, segments[1].Src!.Value, 6);
        Assert.Equal(65.02, segments[2].Src!.Value, 6);
        Assert.Equal(100.02, segments[3].Src!.Value, 6);
        Assert.Equal(5.0, segments[3].FadeOut, 6);
        for (var i = 1; i < segments.Count; i++)
            Assert.Equal(segments[i - 1].End, segments[i].Start, 9);
    }

    [Fact]
    public void The_plan_keeps_the_place_when_it_changes()
    {
        var infos = InfosA();
        var demo = Demo(infos);
        var before = PlanBuilder.Build(A, infos, DurationA, demo);
        var t = 38.5; // Chorus 1, first pass, bar 6

        // Adding a verse in front: the same entry and pass at the same offset.
        var longer = ArrangementEdit.Add(demo, ArrangementEntry.ForSection(Find(infos, "Verse 1").Id), 0);
        var after = PlanBuilder.Build(A, infos, DurationA, longer);
        var mapped = SongPlan.Remap(before, t, after, sameArrangement: true);
        Assert.Equal(78.52, after.SourceAt(mapped)!.Value, 6);
        Assert.Equal("pass 1 of 2", after.At(mapped).NowPass);

        // Removing the entry: the first entry that follows it (here the end).
        var removed = ArrangementEdit.Remove(demo, demo.Entries[1].Id);
        var gone = PlanBuilder.Build(A, infos, DurationA, removed);
        Assert.Equal(gone.Items[^1].PlanStart, SongPlan.Remap(before, t, gone, true), 6);

        // Switching to as written: the same source time.
        var written = PlanBuilder.Build(A, infos, DurationA, null);
        var switched = SongPlan.Remap(before, t, written, sameArrangement: false);
        Assert.Equal(78.52, written.SourceAt(switched)!.Value, 6);
    }

    [Fact]
    public void Time_converts_between_plan_and_source()
    {
        var infos = InfosA();
        var plan = PlanBuilder.Build(A, infos, DurationA, Demo(infos));
        Assert.Null(plan.PlanAt(150)); // the bridge is not in this arrangement
        Assert.Equal(0.02, plan.SourceAt(0)!.Value, 6);
        Assert.Equal(25.0, plan.PlanAt(65.02)!.Value, 6);
        Assert.Equal(60.0, plan.PlanAt(65.02, notBeforePlanSec: 30)!.Value, 6);
        Assert.Equal(25.0, plan.PlanAt(65.02, notBeforePlanSec: 90)!.Value, 6); // none later: the first
    }

    // ---- Validation (ARR-50) --------------------------------------------------------

    [Fact]
    public void ARR50_validation_messages()
    {
        var infos = InfosA();
        Assert.Equal("Nothing to play — add sections or switch to As written",
            ArrangementValidator.Validate(new Arrangement { Id = "a", Name = "a" }, infos).Single().Message);

        var arrangement = new Arrangement
        {
            Id = "a",
            Name = "a",
            Entries =
            [
                ArrangementEntry.ForSection("gone"),
                ArrangementEntry.ForSection(Find(infos, "Verse 1").Id) with { Range = new BarRange { FromBar = 5, ToBar = 2 } },
                ArrangementEntry.ForSection(Find(infos, "Chorus 1").Id) with { Transpose = 6 },
                ArrangementEntry.ForSection(Find(infos, "Bridge").Id) with { Repeat = new RepeatSpec { UntilCued = true } },
            ],
        };
        var issues = ArrangementValidator.Validate(arrangement, infos);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message == "Section removed — choose a replacement" && i.EntryId == arrangement.Entries[0].Id);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message == "A range needs at least 1 bar");
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message == "Large key lift may sound unnatural");
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message == "Song can only end by cue");
        Assert.True(ArrangementValidator.HasErrors(issues));

        var ringOut = new Arrangement { Id = "b", Name = "b", Entries = [ArrangementEntry.ForSection(Find(infos, "Chorus 1").Id)] };
        Assert.Equal("Song will ring out after Chorus 1", ArrangementValidator.Validate(ringOut, infos).Single().Message);
        var ended = new Arrangement { Id = "c", Name = "c", Entries = [ArrangementEntry.ForSection(Find(infos, "Ending").Id)] };
        Assert.Empty(ArrangementValidator.Validate(ended, infos));
    }

    // ---- Edits (ARR-10, ARR-11) -----------------------------------------------------

    [Fact]
    public void ARR10_edits_return_new_arrangements_and_clamp_options()
    {
        var infos = InfosA();
        var start = Demo(infos);
        var verse = ArrangementEntry.ForSection(Find(infos, "Verse 1").Id);

        var added = ArrangementEdit.Add(start, verse);
        Assert.Equal(3, added.Entries.Count);
        Assert.Equal(2, start.Entries.Count); // the input is untouched
        Assert.Equal(verse.Id, ArrangementEdit.Add(start, verse, 0).Entries[0].Id);

        var moved = ArrangementEdit.Move(added, verse.Id, 0);
        Assert.Equal(verse.Id, moved.Entries[0].Id);
        Assert.Equal(3, moved.Entries.Count);

        var (copied, copy) = ArrangementEdit.Duplicate(moved, verse.Id);
        Assert.Equal(copy!.Id, copied.Entries[1].Id);
        Assert.NotEqual(verse.Id, copy.Id);
        Assert.Equal(verse.SectionId, copy.SectionId);

        var changed = ArrangementEdit.Update(copied, copy.Id, e => e with
        {
            Transpose = 9, FadeInBars = 7, FadeOutBars = -2, Repeat = new RepeatSpec { Count = 20 },
            Note = new string('x', 120), Range = new BarRange { FromBar = 0, ToBar = 0 },
        });
        var entry = changed.Entries.Single(e => e.Id == copy.Id);
        Assert.Equal(6, entry.Transpose);
        Assert.Equal(4, entry.FadeInBars);
        Assert.Equal(0, entry.FadeOutBars);
        Assert.Equal(8, entry.Repeat.Count);
        Assert.Equal(80, entry.Note!.Length);
        Assert.Equal(1, entry.Range.FromBar);
        Assert.Equal(1, entry.Range.ToBar);

        Assert.Equal(2, ArrangementEdit.Remove(copied, copy.Id).Entries.Count(e => e.Id != verse.Id) );
        Assert.Equal("short 2", ArrangementEdit.UniqueName("short", ["Short", "As written"]));
        Assert.Equal("Short 3", ArrangementEdit.UniqueName("Short", ["Short", "Short 2"]));
    }

    [Fact]
    public void ARR11_new_variants_start_from_as_written_a_copy_or_nothing()
    {
        var infos = InfosA();
        var written = ArrangementEdit.FromAsWritten(infos, "Full", []);
        Assert.Equal(infos.Count, written.Entries.Count);
        Assert.Equal(infos.Select(i => i.Id), written.Entries.Select(e => e.SectionId));

        var copy = ArrangementEdit.Copy(Demo(infos), "Demo", ["Demo"]);
        Assert.Equal("Demo 2", copy.Name);
        Assert.Equal(ArrangementOrigin.Copy, copy.Origin);
        Assert.DoesNotContain(copy.Entries, e => Demo(infos).Entries.Any(o => o.Id == e.Id));
        Assert.Empty(ArrangementEdit.Empty("New", []).Entries);
    }

    [Fact]
    public void SEC54_merging_and_redetecting_keep_entries_pointing_at_the_same_section()
    {
        var infos = InfosA();
        var demo = Demo(infos);
        var chorus = Find(infos, "Chorus 1");

        // Merge: the removed section's entries follow the merged one.
        var merged = ArrangementEdit.RemapSection([demo], chorus.Id, Find(infos, "Verse 1").Id);
        Assert.Equal(Find(infos, "Verse 1").Id, merged[0].Entries[1].SectionId);

        // Re-detect: every guide section got a new id, labels are the same (SEC-17).
        var redetected = SectionList.Describe(SectionTests.FixtureASections(), A, DurationA);
        var remapped = ArrangementEdit.RemapAfterRedetect([demo], infos, redetected);
        Assert.Equal(Find(redetected, "Intro").Id, remapped[0].Entries[0].SectionId);
        Assert.Equal(Find(redetected, "Chorus 1").Id, remapped[0].Entries[1].SectionId);

        // A renamed section is found again by type and occurrence.
        var renamed = SectionList.Describe(
            SectionList.Rename(SectionTests.FixtureASections(), SectionTests.FixtureASections()[2].Id, "x").Select(s => s).ToList()
                .Select(s => s.Name == "Chorus" ? s with { Id = Section.NewId() } : s), A, DurationA);
        var byType = ArrangementEdit.RemapAfterRedetect([demo], infos, renamed);
        Assert.Equal(renamed.First(r => r.Type == SectionType.Chorus).Id, byType[0].Entries[1].SectionId);

        // Nothing to map to: the dead id stays, so validation flags it.
        var none = ArrangementEdit.RemapAfterRedetect([demo], infos, []);
        Assert.Equal(chorus.Id, none[0].Entries[1].SectionId);
        Assert.Contains(ArrangementValidator.Validate(none[0], []), i => i.Message.StartsWith("Section removed", StringComparison.Ordinal));
    }

    // ---- Quick order (ARR-60) ---------------------------------------------------------

    [Fact]
    public void ARR60_tokens_suffixes_and_special_entries()
    {
        var infos = InfosB();
        var r = QuickOrder.Parse("Verse 2, C x2 HOLD CLICK4 PAD V1", infos);
        Assert.False(r.HasProblems);
        Assert.Equal(["Verse 2", "Chorus 2", "x2", "Hold", "Click 4", "Pad 8", "Verse 1"], r.Tokens.Select(t => t.Label));
        Assert.Equal(6, r.Entries.Count);

        var joined = QuickOrder.Parse("Pre Chorus", infos); // joined into one token; this song has none
        Assert.Single(joined.Tokens);
        Assert.Equal(TokenStatus.NoSection, joined.Tokens[0].Status);

        var tokens = QuickOrder.Parse("V2 x2 +1", infos);
        Assert.False(tokens.HasProblems);
        Assert.Equal(1, tokens.Entries.Count);
        Assert.Equal(2, tokens.Entries[0].Repeat.Count);
        Assert.Equal(1, tokens.Entries[0].Transpose);

        var open = QuickOrder.Parse("C x?", infos);
        Assert.True(open.Entries[0].Repeat.UntilCued);

        var lowered = QuickOrder.Parse("c-1", infos);
        Assert.Equal(-1, lowered.Entries[0].Transpose);

        var generated = QuickOrder.Parse("click pad hold", infos);
        Assert.Equal([EntryKind.Click, EntryKind.Pad, EntryKind.Hold], generated.Entries.Select(e => e.Kind));
        Assert.Equal(4, generated.Entries[0].LengthBars);
        Assert.Equal(8, generated.Entries[1].LengthBars);
    }

    [Fact]
    public void ARR60_resolution_rules()
    {
        var infos = InfosB();
        string[] Ids(string text) => QuickOrder.Parse(text, infos).Entries.Select(e => infos.Single(i => i.Id == e.SectionId).Label).ToArray();

        Assert.Equal(["Verse 1", "Chorus 1", "Verse 2", "Chorus 2"], Ids("V1 C V2 C"));
        Assert.Equal(["Chorus 1", "Chorus 2", "Chorus 3"], Ids("C C C"));
        Assert.Equal(["Chorus 1", "Chorus 1"], Ids("C C1")); // numbered picks the occurrence
        Assert.Equal(["Chorus 4"], Ids("C4"));
        Assert.Equal(["Chorus 1", "Chorus 4", "Chorus 1"], Ids("C c4 C")); // after the last chorus it falls back to the first one
        Assert.Equal(["Ending"], Ids("End"));
        Assert.Equal(["Verse 5"], Ids("Verse 5"));
        Assert.Equal(["Tag"], Ids("tag"));
        Assert.Equal(["Outro"], Ids("Out"));

        // No Ending: End picks the Outro.
        var noEnding = infos.Where(i => i.Type != SectionType.Ending).ToList();
        Assert.Equal(["Outro"], QuickOrder.Parse("E", noEnding).Entries.Select(e => noEnding.Single(i => i.Id == e.SectionId).Label));
    }

    [Fact]
    public void ARR60_unknown_tokens_get_a_suggestion_and_missing_sections_a_reason()
    {
        var infos = InfosB();
        var r = QuickOrder.Parse("V1 Brige Zzz B2 V9 B", infos);
        Assert.True(r.HasProblems);
        Assert.Equal(TokenStatus.Unknown, r.Tokens[1].Status);
        Assert.Equal("Did you mean B (Bridge)?", r.Tokens[1].Message);
        Assert.Equal("Not a section", r.Tokens[2].Message == "Not a section" ? "Not a section" : r.Tokens[2].Message);
        Assert.Equal(TokenStatus.NoSection, r.Tokens[3].Status);
        Assert.Equal("No Bridge 2 in this song", r.Tokens[3].Message);
        Assert.Equal("No Verse 9 in this song", r.Tokens[4].Message);
        Assert.Equal("No Bridge in this song", r.Tokens[5].Message);
        Assert.Single(r.Entries); // only V1 resolved
        Assert.Equal(0, r.Tokens[0].Start);
        Assert.Equal(3, r.Tokens[1].Start);
        Assert.Equal(5, r.Tokens[1].Length);

        Assert.Equal("Nothing to apply this to", QuickOrder.Parse("x2", infos).Tokens[0].Message);
    }

    // ---- Persistence (DM-30, DM-90) ---------------------------------------------------

    [Fact]
    public void Variants_survive_a_round_trip_and_old_files_open_without_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-arr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DataStore(root);
            var infos = InfosA();
            var demo = Demo(infos) with
            {
                Entries =
                [
                    .. Demo(infos).Entries,
                    ArrangementEntry.NewHold(),
                    ArrangementEntry.PadBars(8) with { PadKey = "Db", Note = "Pray" },
                    ArrangementEntry.ForSection(Find(infos, "Bridge").Id) with
                    {
                        Range = new BarRange { FromBar = 2, ToBar = 6 }, Transpose = 2, FadeOutBars = 2, Ending = EndingMode.RingOut,
                        Mutes = new MuteSpec { Groups = ["Drums"], TrackIds = ["t1"], FirstPassOnly = true },
                        GuideCall = GuideCallMode.Custom, GuideCallText = "Big bridge",
                    },
                ],
            };
            var song = new Song
            {
                Id = "s", Title = "Champion", Source = new SongSource { Path = "/x" },
                Arrangements = [demo], DefaultArrangementId = demo.Id,
            };
            store.SaveSong(song);
            var loaded = store.LoadSong("s")!;

            Assert.Equal(demo.Id, loaded.ActiveArrangement!.Id);
            Assert.Equal(5, loaded.Arrangements[0].Entries.Count);
            Assert.Equal(2, loaded.Arrangements[0].Entries[1].Repeat.Count);
            Assert.Equal(EntryKind.Hold, loaded.Arrangements[0].Entries[2].Kind);
            Assert.Equal("Db", loaded.Arrangements[0].Entries[3].PadKey);
            var bridge = loaded.Arrangements[0].Entries[4];
            Assert.Equal(2, bridge.Range.FromBar);
            Assert.Equal(6, bridge.Range.ToBar);
            Assert.Equal(EndingMode.RingOut, bridge.Ending);
            Assert.True(bridge.Mutes.FirstPassOnly);
            Assert.Equal(["Drums"], bridge.Mutes.Groups);
            Assert.Equal("Big bridge", bridge.GuideCallText);
            var text = File.ReadAllText(Path.Combine(root, "songs", "s.json"));
            Assert.Contains("\"kind\": \"Hold\"", text);

            // A file written before arrangements existed, or with nulls where lists belong.
            File.WriteAllText(Path.Combine(root, "songs", "old.json"), """{"id":"old","title":"Old","source":{"path":"/x"},"arrangements":null,"defaultArrangementId":"zzz"}""");
            var old = store.LoadSong("old")!;
            Assert.Empty(old.Arrangements);
            Assert.Null(old.DefaultArrangementId);
            Assert.Null(old.ActiveArrangement);

            File.WriteAllText(Path.Combine(root, "songs", "odd.json"), """{"id":"odd","title":"Odd","source":{"path":"/x"},"arrangements":[{"id":"a","name":"A","entries":null},{"id":"b","name":"B","entries":[{"id":"e","kind":"Section","sectionId":"x","mutes":null,"range":null,"repeat":null,"hold":null}]}],"defaultArrangementId":"b"}""");
            var odd = store.LoadSong("odd")!;
            Assert.Empty(odd.Arrangements[0].Entries);
            Assert.Single(odd.Arrangements[1].Entries);
            Assert.Equal("b", odd.ActiveArrangement!.Id);
            Assert.True(odd.Arrangements[1].Entries[0].Range.IsWhole);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
