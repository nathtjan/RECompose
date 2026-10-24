// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Tests.Sections;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Services;

// SL-1…12, DM-40…42, ARR-12, ARR-80 and the service "Sunday 26 Oct" of 15-acceptance-tests.
public class ServiceTests
{
    private static readonly TempoGrid A = new(72, 3, 0.02);
    private static readonly TempoGrid B = SectionTests.B;

    // Fixture A with its Demo variant (Intro, Chorus 1 ×2), in B♭.
    internal static Song SongA()
    {
        var sections = SectionTests.FixtureASections();
        var infos = SectionList.Describe(sections, A, 310.02);
        var demo = new Arrangement
        {
            Id = "demo",
            Name = "Demo",
            Entries =
            [
                ArrangementEntry.ForSection(infos.Single(i => i.Label == "Intro").Id),
                ArrangementEntry.ForSection(infos.Single(i => i.Label == "Chorus 1").Id) with { Repeat = new RepeatSpec { Count = 2 } },
            ],
        };
        return new Song
        {
            Id = "a",
            Title = "Champion-Revival's In The Air",
            Source = new SongSource { Path = "a" },
            OriginalKey = "Bb",
            Bpm = 72,
            BeatsPerBar = 3,
            FirstDownbeatSec = 0.02,
            DurationSec = 310.02,
            Sections = sections,
            Arrangements = [demo],
            DefaultArrangementId = "demo",
        };
    }

    // Fixture B after Detect, in E, with the AT-10 variant "Short" (V1 C V2 C O E).
    internal static Song SongB()
    {
        var sections = new (string Name, int Bar)[]
        {
            ("Verse 1", 2), ("Chorus", 11), ("Verse 2", 19), ("Verse 3", 27), ("Chorus", 35), ("Verse 4", 43),
            ("Verse 5", 51), ("Chorus", 59), ("Chorus", 67), ("Tag", 75), ("Outro", 77), ("Ending", 80),
        }.Select(s => Section.Create(s.Name, B.BarToTime(s.Bar), SectionOrigin.Guide)).ToList();
        var infos = SectionList.Describe(sections, B, SectionTests.DurationB);
        var shortVariant = new Arrangement { Id = "short", Name = "Short", Entries = [.. QuickOrder.Parse("V1 C V2 C O E", infos).Entries] };
        return new Song
        {
            Id = "b",
            Title = "Come Ye Sinners-The Worship Initiative, Vol. 22",
            Source = new SongSource { Path = "b" },
            OriginalKey = "E",
            Bpm = 72,
            BeatsPerBar = 4,
            FirstDownbeatSec = 0.0007,
            DurationSec = SectionTests.DurationB,
            Sections = sections,
            Arrangements = [shortVariant],
            DefaultArrangementId = "short",
        };
    }

    private static PlannedEntry Planned(ServiceEntry entry, Song song) => new(entry, song, EntryPlan.Build(entry, song));

    // ---- Entries and the arrangement they play (ARR-1, ARR-12) ----------------------------

    [Fact]
    public void A_new_entry_plays_the_songs_default_variant_at_the_original_key()
    {
        var song = SongA();
        var entry = ServiceEdit.NewEntry(song);
        Assert.Equal(ArrangementChoiceType.Variant, entry.Arrangement.Type);
        Assert.Equal("demo", entry.Arrangement.ArrangementId);
        Assert.Equal(0, entry.KeyOffset);
        Assert.Equal(100, entry.TempoPercent);
        Assert.Equal(CountInMode.Track, entry.CountIn.Mode);
        Assert.Equal(TransitionType.Stop, entry.TransitionAfter.Type);
        Assert.Equal("Demo", EntryArrangement.Resolve(entry, song).Name);

        var plain = song with { Arrangements = [], DefaultArrangementId = null };
        Assert.Equal(ArrangementChoiceType.AsWritten, ServiceEdit.NewEntry(plain).Arrangement.Type);
    }

    [Fact]
    public void AT15_editing_for_this_service_only_leaves_the_variant_alone()
    {
        var song = SongA();
        var entry = ServiceEdit.MakeCustom(ServiceEdit.NewEntry(song), song);

        var resolved = EntryArrangement.Resolve(entry, song);
        Assert.Equal(ArrangementChoiceType.Custom, resolved.Type);
        Assert.Equal("Custom for this service", resolved.Name);
        Assert.Equal("demo", entry.Arrangement.Arrangement!.CopiedFromId);
        Assert.NotEqual("demo", entry.Arrangement.Arrangement.Id);
        // Entry ids are kept, so the playhead stays on the same entry when this happens while playing (ARR-90).
        Assert.Equal(song.Arrangements[0].Entries.Select(e => e.Id), entry.Arrangement.Arrangement.Entries.Select(e => e.Id));

        var edited = entry with
        {
            Arrangement = ArrangementChoice.OfCustom(ArrangementEdit.Remove(entry.Arrangement.Arrangement, entry.Arrangement.Arrangement.Entries[0].Id)),
        };
        Assert.Single(edited.Arrangement.Arrangement!.Entries);
        Assert.Equal(2, song.Arrangements[0].Entries.Count); // the song's variant is unchanged

        Assert.Equal("Demo", ServiceEdit.RevertTarget(edited, song)!.Name);
        var reverted = ServiceEdit.Revert(edited, song);
        Assert.Equal(ArrangementChoice.OfVariant("demo"), reverted.Arrangement);
    }

    [Fact]
    public void Save_as_variant_adds_a_variant_and_the_entry_plays_it()
    {
        var song = SongA();
        var entry = ServiceEdit.MakeCustom(ServiceEdit.NewEntry(song), song);

        var (updated, saved, variant) = ServiceEdit.SaveAsVariant(entry, song, "Demo");
        Assert.NotNull(variant);
        Assert.Equal("Demo 2", variant.Name); // the name is taken
        Assert.Null(variant.CopiedFromId);
        Assert.Equal(["Demo", "Demo 2"], updated.Arrangements.Select(a => a.Name));
        Assert.Equal(ArrangementChoice.OfVariant(variant.Id), saved.Arrangement);

        // Nothing to save when the entry plays a variant.
        Assert.Null(ServiceEdit.SaveAsVariant(ServiceEdit.NewEntry(song), song, "x").Variant);
    }

    [Fact]
    public void A_removed_variant_or_song_plays_as_written_and_says_why()
    {
        var song = SongA();
        var gone = ServiceEdit.NewEntry(song) with { Arrangement = ArrangementChoice.OfVariant("gone") };
        var resolved = EntryArrangement.Resolve(gone, song);
        Assert.Null(resolved.Arrangement);
        Assert.Equal("As written", resolved.Name);
        Assert.Equal("Variant removed — plays as written", resolved.Problem);

        Assert.Equal("Song missing from the library", EntryArrangement.Resolve(gone, null).Problem);
        Assert.Null(EntryArrangement.Resolve(ServiceEdit.NewEntry(song), song).Problem);
    }

    [Fact]
    public void ARR11_deleting_a_used_variant_moves_its_entries_to_the_replacement()
    {
        var song = SongA();
        var variantEntry = ServiceEdit.NewEntry(song);
        var customEntry = ServiceEdit.MakeCustom(ServiceEdit.NewEntry(song), song);
        var other = ServiceEdit.NewEntry(SongB());
        var service = new Service { Id = "s", Name = "Sunday 26 Oct", Entries = [variantEntry, customEntry, other] };

        Assert.Single(ServiceEdit.UsingVariant(service, "a", "demo"));
        var after = ServiceEdit.ReplaceVariant(service, "a", "demo", ArrangementChoice.AsWritten);
        Assert.Equal(ArrangementChoiceType.AsWritten, after.Entries[0].Arrangement.Type);
        Assert.Equal(ArrangementChoiceType.Custom, after.Entries[1].Arrangement.Type);
        Assert.Null(after.Entries[1].Arrangement.Arrangement!.CopiedFromId); // nothing left to revert to
        Assert.Equal(other, after.Entries[2]);

        Assert.True(ServiceEdit.UsesSong(service, "b"));
        Assert.False(ServiceEdit.UsesSong(service, "zzz"));
    }

    [Fact]
    public void Entries_are_clamped_to_their_ranges()
    {
        var entry = new ServiceEntry
        {
            Id = "e",
            SongId = "a",
            KeyOffset = 20,
            TempoPercent = 10,
            CountIn = new CountIn { Mode = CountInMode.Generated, Bars = 5 },
            TransitionAfter = new Transition { Type = TransitionType.Crossfade, CrossfadeSec = 90, SegueGapBeats = 12 },
            Arrangement = new ArrangementChoice { Type = ArrangementChoiceType.Variant, ArrangementId = null },
        }.Clamped();

        Assert.Equal(12, entry.KeyOffset);
        Assert.Equal(50, entry.TempoPercent);
        Assert.Equal(2, entry.CountIn.Bars);
        Assert.Equal(30, entry.TransitionAfter.CrossfadeSec);
        Assert.Equal(8, entry.TransitionAfter.SegueGapBeats);
        Assert.Equal(ArrangementChoiceType.AsWritten, entry.Arrangement.Type);
    }

    // ---- Services (SL-1, SL-2, SL-4) -------------------------------------------------------

    private static Service Sunday()
    {
        var a = SongA();
        var b = SongB();
        return new Service
        {
            Id = "sunday",
            Name = "Sunday 26 Oct",
            Date = new DateTime(2026, 10, 26, 9, 0, 0),
            Entries =
            [
                ServiceEdit.NewEntry(a) with { KeyOffset = -1, TransitionAfter = new Transition { Type = TransitionType.Pad, PadKey = "Db", ClickBpm = 72 } },
                ServiceEdit.NewEntry(b) with { TransitionAfter = new Transition { Type = TransitionType.Segue } },
                ServiceEdit.MakeCustom(ServiceEdit.NewEntry(b), b) with { TempoPercent = 95, CountIn = new CountIn { Mode = CountInMode.Generated, Bars = 2 } },
                ServiceEdit.NewEntry(a) with { KeyOffset = 2, Notes = "Big ending", MixPresetId = "rehearsal" },
            ],
        };
    }

    [Fact]
    public void AT33_duplicate_copies_every_entry_with_new_ids()
    {
        var source = Sunday();
        var copy = ServiceEdit.Duplicate(source, "Sunday 2 Nov", new DateTime(2026, 11, 2, 9, 0, 0));

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Sunday 2 Nov", copy.Name);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), copy.Date);
        Assert.Equal(source.Entries.Count, copy.Entries.Count);
        for (var i = 0; i < source.Entries.Count; i++)
        {
            Assert.NotEqual(source.Entries[i].Id, copy.Entries[i].Id);
            Assert.Equal(source.Entries[i], copy.Entries[i] with { Id = source.Entries[i].Id });
        }

        Assert.Equal(ArrangementChoiceType.Custom, copy.Entries[2].Arrangement.Type);
        Assert.Null(copy.LastCheck);
    }

    [Fact]
    public void SL4_a_moved_entry_takes_its_transition_with_it()
    {
        var service = Sunday();
        var first = service.Entries[0];
        var moved = ServiceEdit.Move(service, first.Id, 2);

        Assert.Equal([service.Entries[1].Id, service.Entries[2].Id, first.Id, service.Entries[3].Id], moved.Entries.Select(e => e.Id));
        Assert.Equal(TransitionType.Pad, moved.Entries[2].TransitionAfter.Type);
        Assert.Equal(TransitionType.Segue, moved.Entries[0].TransitionAfter.Type);

        var removed = ServiceEdit.Remove(moved, first.Id);
        Assert.Equal(3, removed.Entries.Count);
        var added = ServiceEdit.Add(removed, ServiceEdit.NewEntry(SongA()), 0);
        Assert.Equal("a", added.Entries[0].SongId);
    }

    [Fact]
    public void SL1_upcoming_services_come_first_then_recent_ones()
    {
        var now = new DateTime(2026, 10, 25, 20, 0, 0);
        Service S(string name, DateTime? date) => new() { Id = name, Name = name, Date = date };
        var ordered = ServiceText.Ordered(
        [
            S("last week", new DateTime(2026, 10, 19, 9, 0, 0)),
            S("undated", null),
            S("next week", new DateTime(2026, 11, 2, 9, 0, 0)),
            S("this morning", new DateTime(2026, 10, 25, 9, 0, 0)),
            S("tomorrow", new DateTime(2026, 10, 26, 9, 0, 0)),
            S("last month", new DateTime(2026, 9, 28, 9, 0, 0)),
        ], now);

        Assert.Equal(["this morning", "tomorrow", "next week", "last week", "last month", "undated"], ordered.Select(s => s.Name));
        Assert.Equal("Sunday 26 Oct · 9:00", ServiceText.Title(Sunday()));
    }

    [Fact]
    public void Services_round_trip_through_the_data_folder_and_old_files_open()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-svc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DataStore(root);
            var service = Sunday();
            store.SaveService(service);

            var loaded = store.LoadService(service.Id)!;
            Assert.Equal(service.Name, loaded.Name);
            Assert.Equal(service.Date, loaded.Date);
            Assert.Equal(service.Entries.Select(e => e.Id), loaded.Entries.Select(e => e.Id));
            Assert.Equal(-1, loaded.Entries[0].KeyOffset);
            Assert.Equal("Db", loaded.Entries[0].TransitionAfter.PadKey);
            Assert.Equal(TransitionType.Segue, loaded.Entries[1].TransitionAfter.Type);
            Assert.Equal(CountInMode.Generated, loaded.Entries[2].CountIn.Mode);
            Assert.Equal(95, loaded.Entries[2].TempoPercent);
            var custom = loaded.Entries[2].Arrangement.Arrangement!;
            Assert.Equal("short", custom.CopiedFromId);
            Assert.Equal(service.Entries[2].Arrangement.Arrangement!.Entries.Select(e => e.SectionId), custom.Entries.Select(e => e.SectionId));
            Assert.Equal("Big ending", loaded.Entries[3].Notes);

            // A file with only the essentials, and one with a null arrangement, open with defaults.
            Directory.CreateDirectory(store.ServicesFolder);
            File.WriteAllText(Path.Combine(store.ServicesFolder, "old.json"),
                """{"id":"old","name":"Old","entries":[{"id":"e1","songId":"a"},{"id":"e2","songId":"a","arrangement":null,"transitionAfter":null}]}""");
            var old = store.LoadService("old")!;
            Assert.All(old.Entries, e =>
            {
                Assert.Equal(ArrangementChoiceType.AsWritten, e.Arrangement.Type);
                Assert.Equal(100, e.TempoPercent);
                Assert.Equal(CountInMode.Track, e.CountIn.Mode);
                Assert.Equal(TransitionType.Stop, e.TransitionAfter.Type);
            });

            Assert.Equal(2, store.LoadServices().Count);
            store.DeleteService("old");
            Assert.Single(store.LoadServices());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    // ---- Text (UI-12, UI-13, SL-20) ---------------------------------------------------------

    [Fact]
    public void UI13_the_header_line_shows_the_absolute_key_and_the_offset()
    {
        var song = SongA();
        Assert.Equal("A (−1)", ServiceText.KeyText(song, -1));
        Assert.Equal("C (+2)", ServiceText.KeyText(song, 2));
        Assert.Equal("B♭", ServiceText.KeyText(song, 0));
        Assert.Equal("Key ? (+1)", ServiceText.KeyText(song with { OriginalKey = null }, 1));

        var entry = ServiceEdit.NewEntry(song) with { KeyOffset = -1, TempoPercent = 95 };
        Assert.Equal("A (−1) · 68.4 BPM (95%) · 3/4 · Demo", ServiceText.HeaderLine(song, entry, EntryArrangement.Resolve(entry, song)));
        Assert.Equal("B♭ · 72 BPM · 3/4 · Demo", ServiceText.HeaderLine(song, entry with { KeyOffset = 0, TempoPercent = 100 }, EntryArrangement.Resolve(entry, song)));
    }

    [Fact]
    public void SL20_chips_and_explanations()
    {
        var db = RECompose.Core.Music.Key.Parse("Db");
        Assert.Equal("Stop", ServiceText.Chip(Transition.Stop, db, 72));
        Assert.Equal("Segue", ServiceText.Chip(new Transition { Type = TransitionType.Segue }, db, 72));
        Assert.Equal("Segue · 2 beats", ServiceText.Chip(new Transition { Type = TransitionType.Segue, SegueGapBeats = 2 }, db, 72));
        Assert.Equal("Crossfade 6 s", ServiceText.Chip(new Transition { Type = TransitionType.Crossfade }, db, 72));
        Assert.Equal("Pad in D♭ · click 72", ServiceText.Chip(new Transition { Type = TransitionType.Pad }, db, 72)); // AT-30 defaults
        Assert.Equal("Pad in E", ServiceText.Chip(new Transition { Type = TransitionType.Pad, PadKey = "E", ClickDuring = false }, db, 72));
        Assert.Equal("Carry pad over", ServiceText.Chip(new Transition { Type = TransitionType.CarryOver }, db, 72));

        Assert.Equal("At the end, playback stops. Goodness is cued — press Play to start it.",
            ServiceText.Explanation(Transition.Stop, "Champion", "Goodness", db));
        Assert.Equal("Goodness starts by itself 2 beats after Champion ends.",
            ServiceText.Explanation(new Transition { Type = TransitionType.Segue, SegueGapBeats = 2 }, "Champion", "Goodness", db));
        Assert.Equal("At the end, the pad fades in with the click. Press Play and the next song starts, and the pad fades out.",
            ServiceText.Explanation(new Transition { Type = TransitionType.Pad }, "Champion", "Goodness", db));
        Assert.Equal("The pad that's playing keeps going into Goodness, retuned to D♭.",
            ServiceText.Explanation(new Transition { Type = TransitionType.CarryOver }, "Champion", "Goodness", db));
    }

    // ---- Plans of entries (PB-41, SL-36, PB-33) ------------------------------------------------

    [Fact]
    public void An_entry_at_a_lower_key_and_slower_tempo_keeps_its_bars_and_stretches_its_clock()
    {
        var song = SongA();
        var entry = ServiceEdit.NewEntry(song) with { KeyOffset = -1, TempoPercent = 95 };
        var plan = EntryPlan.Build(entry, song);

        Assert.Equal(100, plan.DurationSec, 6); // plan time stays in source seconds
        Assert.Equal(100 / 0.95, plan.HeardDurationSec, 6);
        Assert.Equal("0:00 / −1:45", plan.At(0).Time);
        var r = plan.At(38.5); // AT-2's position
        Assert.Equal("bar 16.2 / 38", r.SongBar);
        Assert.Equal("bar 6 / 14", r.NowDetail);
        Assert.Equal("0:40 / −1:04", r.Time);
        Assert.Equal("38 bars · 1:45", ArrangementSummary.Totals(plan));

        var segments = plan.Segments();
        Assert.All(segments, s => Assert.Equal(-1, s.Key));
        Assert.All(segments, s => Assert.Equal(9, s.SongKey)); // B♭ − 1 = A
        Assert.All(segments, s => Assert.False(s.Click));
        Assert.Equal(95, plan.TransitionPointSec, 6); // where the tail starts (SL-30)
    }

    [Fact]
    public void AT32_a_generated_count_in_replaces_the_track_lead_in()
    {
        var song = SongB();
        var entry = ServiceEdit.NewEntry(song) with { CountIn = new CountIn { Mode = CountInMode.Generated, Bars = 2 } };
        var plan = EntryPlan.Build(entry, song);

        var countIn = plan.Items[0];
        Assert.Equal(PlanItemKind.CountIn, countIn.Kind);
        Assert.Equal(2 * B.BarLength, countIn.Length, 6);
        Assert.False(countIn.HasSource);
        Assert.DoesNotContain(plan.Items, i => i.Kind == PlanItemKind.LeadIn);
        Assert.Equal(countIn.PlanEnd, plan.P0, 6); // Verse 1 starts on the downbeat after the count-in
        Assert.Equal(39, plan.TotalBars);

        var rest = plan.At(0);
        Assert.Equal("COUNT-IN", rest.NowLabel);
        Assert.Equal("beat −8", rest.NowDetail);
        Assert.Equal("VERSE 1", rest.NextLabel);
        Assert.Equal("in 2 bars", rest.Countdown);
        Assert.Equal("0:00 / −2:16", rest.Time); // 2 bars of click instead of the 1-bar lead-in

        var first = plan.Segments()[0];
        Assert.True(first.Click);
        Assert.Null(first.Src);

        // None: the plan starts on the first downbeat.
        var none = EntryPlan.Build(entry with { CountIn = new CountIn { Mode = CountInMode.None } }, song);
        Assert.Equal(PlanItemKind.Section, none.Items[0].Kind);
        Assert.Equal(0, none.P0);
        Assert.Equal("0:00 / −2:10", none.At(0).Time);
    }

    [Fact]
    public void AT11_key_lifts_add_to_the_entry_key_in_the_segments()
    {
        var song = SongB();
        var infos = SectionList.Describe(song.Sections, song.Grid, song.DurationSec);
        var lifted = new Arrangement { Id = "lift", Name = "Lift", Entries = [.. QuickOrder.Parse("V1 C(1-4) V2 C4x2 C4+2 E", infos).Entries] };
        var withLift = song with { Arrangements = [lifted] };
        var entry = ServiceEdit.NewEntry(withLift) with { Arrangement = ArrangementChoice.OfVariant("lift"), KeyOffset = -1 };
        var plan = EntryPlan.Build(entry, withLift);

        var sections = plan.Items.Where(i => i.Kind == PlanItemKind.Section).ToList();
        Assert.Equal([-1, -1, -1, -1, -1, 1, -1], sections.Select(i => i.KeyTotal));
        Assert.Equal(5, sections[5].SongPitchClass); // E − 1 + 2 = F: pads with Follow song move up at this boundary
        Assert.Equal(-1, plan.Items[0].KeyTotal); // the lead-in plays in the entry key
    }

    [Fact]
    public void ARR39_click_bars_run_the_click_and_pad_bars_carry_their_key()
    {
        var song = SongB();
        var infos = SectionList.Describe(song.Sections, song.Grid, song.DurationSec);
        var arrangement = new Arrangement
        {
            Id = "gen",
            Name = "Gen",
            Entries =
            [
                ArrangementEntry.ClickBars(4),
                ArrangementEntry.ForSection(infos[0].Id),
                ArrangementEntry.PadBars(8),
                ArrangementEntry.PadBars(2) with { PadKey = "Db" },
                ArrangementEntry.ForSection(infos[^1].Id),
            ],
        };
        var withGen = song with { Arrangements = [arrangement] };
        var entry = ServiceEdit.NewEntry(withGen) with { Arrangement = ArrangementChoice.OfVariant("gen"), KeyOffset = 1 };
        var segments = EntryPlan.Build(entry, withGen).Segments();

        Assert.Equal([false, true, false, false, false, false], segments.Select(s => s.Click)); // lead-in, click bars, …
        Assert.Equal([null, null, null, 5, 1, null], segments.Select(s => s.Pad)); // the entry key F, then D♭
        Assert.All(segments.Where(s => s.Pad is not null || s.Click), s => Assert.Null(s.Src));
    }

    // ---- Durations (ARR-80) -----------------------------------------------------------------------

    [Fact]
    public void ARR80_the_service_length_follows_the_transitions()
    {
        var a = SongA();
        var b = SongB();
        var last = Planned(ServiceEdit.NewEntry(b), b); // 133.33 s, no tail: the Ending reaches the file end
        PlannedEntry First(Transition t, int tempo = 100) => Planned(ServiceEdit.NewEntry(a) with { TransitionAfter = t, TempoPercent = tempo }, a);

        // Stop plays the whole plan (100 s, tail included).
        Assert.Equal("3:53", ServiceDurations.Total([First(Transition.Stop), last]).Text);
        // A segue hands over when the tail starts (95 s), plus 2 beats.
        Assert.Equal("3:50", ServiceDurations.Total([First(new Transition { Type = TransitionType.Segue, SegueGapBeats = 2 }), last]).Text);
        // A crossfade at 95 %: the tail start is 100 s as heard; the overlap counts once.
        Assert.Equal("3:53", ServiceDurations.Total([First(new Transition { Type = TransitionType.Crossfade }, 95), last]).Text);
        // A pad waits for Play: open-ended.
        Assert.Equal("2 songs · 3:53+", ServiceDurations.Summary([First(new Transition { Type = TransitionType.Pad }), last]));
        // Unless the next song starts by itself after 4 bars of click at its 72 BPM, 4/4 (13.3 s after the tail starts).
        Assert.Equal("4:01", ServiceDurations.Total([First(new Transition { Type = TransitionType.Pad, AutoStartNext = true }), last]).Text);

        Assert.Equal("No songs", ServiceDurations.Summary([]));
        Assert.Equal("1 song · 2:13", ServiceDurations.Summary([last]));
    }
}
