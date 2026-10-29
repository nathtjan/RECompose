// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Automation;
using RECompose.Core.Library;
using RECompose.Core.Logging;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Settings;
using RECompose.Core.Timing;

namespace RECompose.Core.Tests.Automation;

// Cues, section notes, spoken guide and counts, guide-call replacement (AUTO-2…8, AUTO-21, AUTO-30, AUTO-31, PB-71) and the log
// (AUTO-50, AUTO-60), on fixture A (Champion: 72 BPM 3/4, a bar is 2.5 s).
public class AutomationTests
{
    private static readonly TempoGrid A = new(72, 3, 0.02);
    private const double DurationA = 310.02;

    private static Song SongA(List<Section>? sections = null, SongAutomation? automation = null, List<Cue>? cues = null, GuideAnalysis? guide = null) => new()
    {
        Id = "a",
        Title = "Champion",
        Source = new SongSource { Path = "a" },
        Bpm = 72,
        BeatsPerBar = 3,
        FirstDownbeatSec = 0.02,
        DurationSec = DurationA,
        Sections = sections ?? Sections.SectionTests.FixtureASections(),
        Automation = automation ?? new SongAutomation(),
        Cues = cues ?? [],
        GuideAnalysis = guide,
    };

    private static SongPlan Plan(Song song, params string[] labels)
    {
        var infos = SectionList.Describe(song.Sections, A, DurationA);
        if (labels.Length == 0)
            return PlanBuilder.Build(A, infos, DurationA, null);
        return PlanBuilder.Build(A, infos, DurationA, new Arrangement
        {
            Id = "x",
            Name = "X",
            Entries = labels.Select(l => l.EndsWith("×2", StringComparison.Ordinal)
                ? ArrangementEntry.ForSection(infos.Single(i => i.Label == l[..^3]).Id) with { Repeat = new RepeatSpec { Count = 2 } }
                : ArrangementEntry.ForSection(infos.Single(i => i.Label == l).Id)).ToList(),
        });
    }

    [Fact]
    public void AUTO21_a_note_per_section_type_at_each_downbeat()
    {
        var song = SongA(automation: new SongAutomation { SectionNotes = true });
        var plan = Plan(song, "Intro", "Chorus 1 ×2");
        var events = AutomationPlan.Build(plan, song);

        var ons = events.Midi.Where(e => e.Id > 0).ToList();
        Assert.Equal([0, 25, 60], ons.Select(e => Math.Round(e.AtSec, 3)));
        Assert.Equal([0x90, 64, 127], ons[1].Bytes.Select(b => (int)b));
        Assert.Equal("Section Chorus 1: MIDI note E3", events.Lines[ons[1].Id]);
        Assert.Equal("Section Intro: MIDI note C3", events.Lines[ons[0].Id]);
        var off = events.Midi.Single(e => e.Id == 0 && Math.Abs(e.AtSec - 25.1) < 1e-9);
        Assert.Equal([0x80, 64, 0], off.Bytes.Select(b => (int)b));

        var once = AutomationPlan.Build(plan, song, new AutomationOptions { Midi = new MidiOutSettings { ResendOnRepeat = false, MiddleC = "C4", Channel = 2 } });
        Assert.Equal([0, 25], once.Midi.Where(e => e.Id > 0).Select(e => Math.Round(e.AtSec, 3)));
        Assert.Equal(0x91, once.Midi[0].Bytes[0]);
        Assert.Contains("Section Chorus 1: MIDI note E4", once.Lines.Values);

        Assert.Empty(AutomationPlan.Build(plan, SongA()).Midi); // off by default (AUTO-20)
    }

    [Fact]
    public void AUTO2_the_first_free_note_from_C4_and_AUTO7_the_clash_with_a_section_note()
    {
        Assert.Equal(72, MidiNotes.FirstFreeNote([], null));
        Assert.Equal("C4", MidiNotes.Name(72));
        Assert.Equal("F♯3", MidiNotes.Name(66));
        var first = new Cue { Id = "1", Name = "1", Action = new CueAction { Note = 72 } };
        Assert.Equal(73, MidiNotes.FirstFreeNote([first], null));
        Assert.Equal(72, MidiNotes.FirstFreeNote([], null, "C4")); // C4 = 60 is the Intro note, so the next free one

        Assert.Null(MidiNotes.Clash(first, null));
        Assert.Equal(SectionType.Chorus, MidiNotes.Clash(first with { Action = new CueAction { Note = 64 } }, null));
        Assert.Equal("CC 64 = 127", new CueAction { Kind = CueActionKind.ControlChange, Controller = 64 }.Summary());
    }

    [Fact]
    public void AUTO5_AUTO8_a_cue_fires_on_every_pass_or_the_first_or_last_and_never_in_a_tail()
    {
        var cue = new Cue { Id = "c", Name = "1", SourceSec = 70.02 }; // bar 3 of Chorus 1
        var plan = Plan(SongA(), "Intro", "Chorus 1 ×2");
        double[] At(Cue c) => AutomationPlan.Build(plan, SongA(cues: [c])).Midi.Where(e => e.Id > 0).Select(e => Math.Round(e.AtSec, 3)).ToArray();

        Assert.Equal([30, 65], At(cue));
        Assert.Equal([30], At(cue with { Passes = CuePasses.First }));
        Assert.Equal([65], At(cue with { Passes = CuePasses.Last }));
        Assert.Empty(At(cue with { Enabled = false }));
        Assert.Empty(At(cue with { SourceSec = 101 })); // in the ring-out after Chorus 1

        var events = AutomationPlan.Build(plan, SongA(cues: [cue]));
        Assert.Equal("Cue 1: MIDI note C4", events.Lines[events.Midi.First(e => e.Id > 0).Id]);

        var speak = AutomationPlan.Build(plan, SongA(cues: [cue with { Action = new CueAction { Kind = CueActionKind.Speak, Text = "Big chorus" } }]));
        Assert.Equal(2, speak.Calls.Count(c => c.Text == "Big chorus"));
        Assert.All(speak.Midi, e => Assert.Empty(e.Bytes));
        Assert.Equal("Cue 1: Speak “Big chorus”", speak.Lines.Values.First());
    }

    [Fact]
    public void AUTO3_snap_moves_a_cue_to_the_nearest_bar()
    {
        var cue = new Cue { Id = "c", Name = "1", SourceSec = 71.0, Snap = CueSnap.Bar };
        Assert.Equal(70.02, cue.FireSec(A), 6);
        Assert.Equal(70.853, (cue with { Snap = CueSnap.Beat }).FireSec(A), 3);
    }

    [Fact]
    public void AUTO31_the_spoken_guide_one_bar_ahead_and_Ending_before_the_tail()
    {
        var song = SongA(automation: new SongAutomation { SpokenGuide = true });
        var plan = Plan(song, "Intro", "Chorus 1 ×2");
        var calls = AutomationPlan.Build(plan, song).Calls;

        Assert.Equal(["Intro", "Chorus one", "Chorus one", "Ending"], calls.Select(c => c.Text));
        Assert.Equal([0, 22.5, 57.5, 92.5], calls.Select(c => Math.Round(c.AtSec, 3)));
        Assert.All(calls, c => Assert.Equal(2.5, c.LengthSec, 6));

        // Queued less than a bar before its section: it plays now.
        var late = AutomationPlan.Build(plan, song, new AutomationOptions { NowSec = 24 }).Calls;
        Assert.Contains(late, c => c.Text == "Chorus one" && Math.Abs(c.AtSec - 24) < 1e-9);
    }

    [Fact]
    public void PB71_a_count_in_counts_one_two_then_every_beat()
    {
        var grid = new TempoGrid(120, 4, 0);
        var sections = SectionList.Describe([Section.Create("Verse 1", 0, SectionOrigin.Manual), Section.Create("Chorus", 16, SectionOrigin.Manual)], grid, 40);
        var plan = PlanBuilder.Build(grid, sections, 40, null, options: new PlanOptions { CountIn = CountInMode.Generated, CountInBars = 2 });
        var song = new Song { Id = "s", Title = "S", Source = new SongSource { Path = "s" }, Bpm = 120, DurationSec = 40 };

        var calls = AutomationPlan.Build(plan, song).Calls;
        Assert.Equal(["one", "two", "one", "two", "three", "four"], calls.Select(c => c.Text));
        Assert.Equal([0, 1, 2, 2.5, 3, 3.5], calls.Select(c => Math.Round(c.AtSec, 3)));
    }

    private static (Song Song, GuideCall Chorus, GuideCall Bridge) WithCalls()
    {
        var sections = Sections.SectionTests.FixtureASections();
        var chorus = new GuideCall { Id = "call-c", Text = "chorus", Start = 62.0, End = 62.6, SectionName = "Chorus" };
        var bridge = new GuideCall { Id = "call-b", Text = "bridge", Start = 212.0, End = 212.5, SectionName = "Bridge" };
        sections = sections.Select(s => s.Name == "Bridge" ? s with { CallId = bridge.Id } : s).ToList();
        var firstChorus = sections.First(s => s.Name == "Chorus");
        sections = sections.Select(s => s.Id == firstChorus.Id ? s with { CallId = chorus.Id } : s).ToList();
        var guide = new GuideAnalysis { TrackId = "guide", Calls = [chorus, bridge] };
        return (SongA(sections, guide: guide), chorus, bridge);
    }

    [Fact]
    public void AUTO30_a_written_call_for_a_section_that_will_not_follow_is_replaced_by_the_guides_own_call()
    {
        var (song, chorus, bridge) = WithCalls();
        var plan = Plan(song, "Intro", "Verse 1", "Bridge");
        var events = AutomationPlan.Build(plan, song);

        // Verse 1 plays plan 25…65; the "Chorus" call at source 62.0 is at plan 62.0 − 25.02 + 25.
        var at = 62.0 - 25.02 + 25;
        var silence = Assert.Single(events.GuideSilences);
        Assert.Equal(at - 0.05, silence.Start, 6);
        Assert.Equal(at + 0.6 + 0.05, silence.End, 6);
        var call = Assert.Single(events.Calls);
        Assert.Equal(bridge.Start, call.FromSec);
        Assert.Equal(at, call.AtSec, 6);
        Assert.Equal("Bridge", Assert.Single(events.Replacements).Label);

        // The guide track is muted over the call in split segments that read on without a seam.
        var segments = plan.Segments(events.GuideSilences, ["guide"]);
        var muted = Assert.Single(segments, s => s.Mute.Contains("guide"));
        Assert.Equal(silence.Start, muted.Start, 6);
        var before = segments[segments.ToList().IndexOf(muted) - 1];
        Assert.Equal(before.Src!.Value + (before.End - before.Start), muted.Src!.Value, 6);

        // As written the call is right; with the switch off nothing changes.
        Assert.Empty(AutomationPlan.Build(Plan(song, "Intro", "Verse 1", "Chorus 1"), song).GuideSilences);
        Assert.Empty(AutomationPlan.Build(plan, song, new AutomationOptions { ReplaceGuideCalls = false }).GuideSilences);
        _ = chorus;
    }

    [Fact]
    public void AUTO30_off_silences_and_custom_speaks_the_text()
    {
        var (song, _, _) = WithCalls();
        var infos = SectionList.Describe(song.Sections, A, DurationA);
        SongPlan With(GuideCallMode mode, string? text = null) => PlanBuilder.Build(A, infos, DurationA, new Arrangement
        {
            Id = "x",
            Name = "X",
            Entries =
            [
                ArrangementEntry.ForSection(infos.Single(i => i.Label == "Verse 1").Id),
                ArrangementEntry.ForSection(infos.Single(i => i.Label == "Verse 2").Id) with { GuideCall = mode, GuideCallText = text },
            ],
        });

        var off = AutomationPlan.Build(With(GuideCallMode.Off), song);
        Assert.Single(off.GuideSilences);
        Assert.Empty(off.Calls);

        var custom = AutomationPlan.Build(With(GuideCallMode.Custom, "Big verse"), song);
        Assert.Equal("Big verse", Assert.Single(custom.Calls).Text);
        Assert.Null(custom.Calls[0].FromSec);

        // No recorded call for Verse 2: its spoken name.
        Assert.Equal("Verse two", Assert.Single(AutomationPlan.Build(With(GuideCallMode.Auto), song).Calls).Text);
    }

    [Fact]
    public void AUTO50_chips_search_and_export()
    {
        var t = new DateTime(2026, 10, 26, 10, 15, 3);
        var entries = new[]
        {
            new LogEntry(t, LogChips.Of("Voice"), "Voice: Verse 2, at the end of this section"),
            new LogEntry(t, "Sections", "Section Chorus 2: MIDI note E3"),
            new LogEntry(t, LogChips.Of("Library"), "Library: Imported, with a comma"),
        };

        Assert.Equal(["Voice: Verse 2, at the end of this section"], entries.Where(e => LogChips.Shows(e, "Voice", null)).Select(e => e.Text));
        Assert.Equal(3, entries.Count(e => LogChips.Shows(e, LogChips.All, null)));
        Assert.Single(entries, e => LogChips.Shows(e, LogChips.All, "midi"));
        Assert.Equal("10:15:03  Section Chorus 2: MIDI note E3", entries[1].Line);
        Assert.Contains("2026-10-26 10:15:03,,\"Library: Imported, with a comma\"", LogChips.Export(entries, csv: true));
        Assert.Equal("Pedal", LogChips.Of("Controls"));
    }

    [Fact]
    public void AUTO60_the_report_shows_planned_and_played_order_and_live_changes()
    {
        var t = new DateTime(2026, 10, 26, 10, 0, 0);
        var song = new ReportSong
        {
            Title = "Champion",
            Key = "B♭",
            Tempo = "72 BPM",
            Arrangement = "Demo",
            Started = t,
            Planned = ["Intro", "Chorus 1", "Chorus 1"],
            Played = [(t, "Intro"), (t.AddSeconds(25), "Chorus 1"), (t.AddSeconds(60), "Verse 2")],
            Entries =
            [
                new LogEntry(t.AddSeconds(30), "Voice", "Voice: Verse 2, at the end of this section"),
                new LogEntry(t.AddSeconds(31), "Jumps", "Live: Jump (voice) → Verse 2 at 60.000"),
                new LogEntry(t.AddSeconds(40), "Voice", "Voice ignored: Chorus (the guide track was talking)"),
            ],
        };

        var md = ServiceReport.Build("Sunday 26 Oct", t, t.AddMinutes(5), [song]);
        Assert.Contains("# Sunday 26 Oct", md);
        Assert.Contains("- Planned: Intro, Chorus 1, Chorus 1", md);
        Assert.Contains("- As played: Intro, Chorus 1, Verse 2", md);
        Assert.Contains("  - 10:00:31  Live: Jump (voice) → Verse 2 at 60.000", md);
        Assert.Contains("- Ignored voice commands:\n  - 10:00:40  Voice ignored", md);
        Assert.Contains("- Live changes: 1", md);
    }
}
