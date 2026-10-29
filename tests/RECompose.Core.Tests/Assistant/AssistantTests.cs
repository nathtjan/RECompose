// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using RECompose.Core.Arrangements;
using RECompose.Core.Assistant;
using RECompose.Core.Commands;
using RECompose.Core.Library;
using RECompose.Core.Services;

namespace RECompose.Core.Tests.Assistant;

// The assistant's tools (AS-30), state snapshot (AS-12), prompt (AS-11) and undo groups (AS-40), on fixture A (Champion,
// original key E, sections Intro · Verse 1 · Chorus 1 · Vamp · Verse 2 · Chorus 2 · Interlude · Bridge · Chorus 3 · Ending).
public class AssistantTests
{
    private static Song Champion() => new()
    {
        Id = "a",
        Title = "Champion",
        Source = new SongSource { Path = "a" },
        OriginalKey = "E",
        Bpm = 72,
        BeatsPerBar = 3,
        FirstDownbeatSec = 0.02,
        DurationSec = 310.02,
        Sections = Sections.SectionTests.FixtureASections(),
        Tracks = [new Track { Id = "d", Name = "Kit", File = "d.wav", Group = TrackGroups.Drums }, new Track { Id = "v", Name = "Lead", File = "v.wav", Group = TrackGroups.Vocals }],
    };

    private static Song Gratitude() => Champion() with { Id = "g", Title = "Gratitude", OriginalKey = "B" };

    private static (AssistantWorld World, Service Service) World()
    {
        var songs = new[] { Champion(), Gratitude() }.ToDictionary(s => s.Id);
        var service = ServiceEdit.Add(ServiceEdit.Add(ServiceEdit.New("Sunday 26 Oct", new DateTime(2026, 10, 26, 9, 0, 0)), ServiceEdit.NewEntry(songs["a"])), ServiceEdit.NewEntry(songs["a"]));
        var world = new AssistantWorld
        {
            Services = [service],
            Current = service,
            FindSong = id => songs.GetValueOrDefault(id),
            Library = songs.Values.Select(SongSummary.From).ToList(),
        };
        return (world, service);
    }

    private static AssistantResult Run(AssistantWorld world, string tool, string json) =>
        AssistantCommands.Run(tool, (JsonObject)JsonNode.Parse(json)!, world);

    [Fact]
    public void AS60_set_entry_key_moves_by_the_shortest_step_and_says_so()
    {
        var (world, service) = World();
        var result = Run(world, "set_entry_key", """{"entry": 2, "key": "F"}""");

        Assert.True(result.Ok);
        Assert.Equal("Sunday 26 Oct: Champion to F.", result.Summary);
        var after = result.Change!.Services.Single();
        Assert.Equal(1, after.Entries[1].KeyOffset);
        Assert.Equal(0, after.Entries[0].KeyOffset);
        Assert.Equal("E", result.Data!["was"]!.GetValue<string>());
        Assert.Equal([service.Entries[1].Id], result.Change.EntryIds);

        Assert.Equal(-2, Run(world, "set_entry_key", """{"entry": 1, "key": "D"}""").Change!.Services[0].Entries[0].KeyOffset);
    }

    [Fact]
    public void AS60_an_arrangement_for_this_service_picks_the_chorus_after_each_verse()
    {
        var (world, _) = World();
        var result = Run(world, "set_entry_arrangement", """
            {"entry": 1, "scope": "service", "entries": [{"section": "Verse 1"}, {"section": "Chorus"}, {"section": "V2"}, {"section": "chorus"}, {"section": "End"}]}
            """);

        Assert.True(result.Ok, result.Summary);
        Assert.Equal("Champion arrangement: 1. Verse 1 · 2. Chorus 1 · 3. Verse 2 · 4. Chorus 2 · 5. Ending.", result.Summary);
        var entry = result.Change!.Services.Single().Entries[0];
        Assert.Equal(ArrangementChoiceType.Custom, entry.Arrangement.Type);
        Assert.Equal(ArrangementOrigin.Assistant, entry.Arrangement.Arrangement!.Origin);
        Assert.Null(result.Change.Song); // the song's variants are untouched
    }

    [Fact]
    public void AS60_a_key_lift_and_drums_out_are_entry_options()
    {
        var (world, _) = World();
        var result = Run(world, "set_entry_arrangement", """
            {"entry": 1, "scope": "service", "entries": [{"section": "Verse 1", "mute": ["drums"]}, {"section": "Chorus 3", "transpose": 2}]}
            """);

        var entries = result.Change!.Services[0].Entries[0].Arrangement.Arrangement!.Entries;
        Assert.Equal([TrackGroups.Drums], entries[0].Mutes.Groups);
        Assert.Equal(2, entries[1].Transpose);
        Assert.EndsWith("2. Chorus 3 (+2).", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AS30_bad_input_is_an_error_with_a_clear_message_and_no_change()
    {
        var (world, _) = World();

        var noSection = Run(world, "set_entry_arrangement", """{"entry": 1, "scope": "service", "entries": [{"section": "Tag"}]}""");
        Assert.False(noSection.Ok);
        Assert.Equal("No Tag in this song", noSection.Summary);
        Assert.Null(noSection.Change);

        Assert.Equal("Sunday 26 Oct has 2 songs; there is no song 5.", Run(world, "set_entry_tempo", """{"entry": 5, "percent": 90}""").Summary);
        Assert.Equal("Tempo goes from 50 to 150 %.", Run(world, "set_entry_tempo", """{"entry": 1, "percent": 200}""").Summary);
        Assert.Equal("Key lifts go up to ±6 semitones.", Run(world, "set_entry_arrangement", """{"entry": 1, "scope": "service", "entries": [{"section": "Chorus", "transpose": 7}]}""").Summary);
        Assert.StartsWith("Champion has no group or track \"horns\"", Run(world, "set_entry_arrangement", """{"entry": 1, "scope": "service", "entries": [{"section": "Chorus", "mute": ["horns"]}]}""").Summary, StringComparison.Ordinal);
        Assert.False(Run(world, "set_entry_arrangement", """{"entry": 1, "scope": "variant", "entries": [{"section": "Chorus"}]}""").Ok); // plays as written
    }

    [Fact]
    public void AS60_search_then_add_after_the_opener()
    {
        var (world, _) = World();
        var found = Run(world, "search_library", """{"query": "gratitude"}""");
        Assert.Equal("g", found.Data![0]!["songId"]!.GetValue<string>());

        var added = Run(world, "add_entry", """{"songId": "g", "position": 2}""");
        Assert.Equal("Sunday 26 Oct: added Gratitude as song 2.", added.Summary);
        Assert.Equal(["a", "g", "a"], added.Change!.Services[0].Entries.Select(e => e.SongId));
    }

    [Fact]
    public void AS30_a_new_variant_goes_on_the_song_and_the_entry_plays_it()
    {
        var (world, _) = World();
        var result = Run(world, "set_entry_arrangement", """{"entry": 1, "scope": "newVariant", "newVariantName": "Short", "entries": [{"section": "Intro"}, {"section": "Chorus 1"}]}""");

        var variant = result.Change!.Song!.Arrangements.Single();
        Assert.Equal("Short", variant.Name);
        Assert.Equal(variant.Id, result.Change.Services[0].Entries[0].Arrangement.ArrangementId);

        var uses = Run(world with { FindSong = id => id == "a" ? result.Change.Song : null }, "set_entry_variant", """{"entry": 2, "variant": "short"}""");
        Assert.Equal("Sunday 26 Oct: Champion uses \"Short\".", uses.Summary);
    }

    [Fact]
    public void AS30_cues_take_the_first_free_note_and_chart_bars()
    {
        var (world, _) = World();
        var result = Run(world, "add_cue", """{"songId": "a", "chartBar": 27}""");

        var cue = result.Change!.Song!.Cues.Single();
        Assert.Equal("Cue 1", cue.Name);
        Assert.Equal(72, cue.Action.Note); // C4: the first note above the section-type map
        Assert.Equal(Math.Round(new RECompose.Core.Timing.TempoGrid(72, 3, 0.02).BarToTime(27), 3), cue.SourceSec);
        Assert.Equal("Champion: Cue 1 at bar 27.1 sends MIDI note C4.", result.Summary);

        var world2 = world with { FindSong = id => id == "a" ? result.Change.Song : null };
        Assert.Equal("Champion: Cue 1 now sends MIDI note C♯4.", Run(world2, "update_cue", """{"songId": "a", "cue": "cue 1", "action": {"note": "C#4"}}""").Summary);
    }

    [Fact]
    public void AS12_the_state_snapshot_has_the_service_and_the_selected_song()
    {
        var (world, service) = World();
        var json = AssistantState.Build("prepare", null, new DateTime(2026, 10, 25, 19, 42, 0), service, world.Services, world.FindSong, "24:30", null, null, Champion(), new(), null);
        var state = JsonNode.Parse(json)!;

        Assert.Equal("Sun 19:42", state["clock"]!.GetValue<string>());
        var entry = state["service"]!["entries"]![0]!;
        Assert.Equal("Champion", entry["title"]!.GetValue<string>());
        Assert.Equal("E", entry["key"]!.GetValue<string>());
        Assert.StartsWith("1. Intro · 2. Verse 1 · 3. Chorus 1", entry["arrangement"]!["order"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Chorus 1", state["selectedSong"]!["sections"]![2]!["label"]!.GetValue<string>());
        Assert.Equal("E3", state["sectionNoteMap"]!["chorus"]!.GetValue<string>());
        Assert.Equal(["Drums", "Vocals"], state["selectedSong"]!["groups"]!.AsArray().Select(g => g!.GetValue<string>()));

        var prompt = AssistantPrompt.System("perform", "service", json);
        Assert.Contains("Mode is perform / lock service.", prompt, StringComparison.Ordinal);
        Assert.EndsWith($"<state>{json}</state>", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AS30_every_tool_has_a_schema_and_reads_do_not_mutate()
    {
        Assert.Equal(21, AssistantPrompt.Tools.Count);
        Assert.All(AssistantPrompt.Tools, t => Assert.All(t.Required, r => Assert.True(t.Properties.ContainsKey(r), $"{t.Name}.{r}")));
        Assert.Equal(["search_library", "get_song", "get_service", "run_service_check"], AssistantPrompt.Tools.Where(t => !t.Mutates).Select(t => t.Name));
        Assert.DoesNotContain(AssistantPrompt.Tools, t => t.Name.Contains("play", StringComparison.Ordinal) || t.Name.Contains("jump", StringComparison.Ordinal));
    }

    [Fact]
    public void AS40_one_undo_group_per_message()
    {
        var stack = new UndoStack();
        var log = new List<string>();
        stack.Push(new UndoableChange("manual", () => log.Add("undo manual"), () => { }));
        var mark = stack.Count;
        stack.Push(new UndoableChange("a", () => log.Add("undo a"), () => log.Add("redo a")));
        stack.Push(new UndoableChange("b", () => log.Add("undo b"), () => log.Add("redo b")));

        var group = stack.Group(mark, "Assistant: 2 changes");
        Assert.Equal(2, stack.Count);
        Assert.Same(group, stack.Top);

        stack.Undo();
        Assert.Equal(["undo b", "undo a"], log);
        stack.Redo();
        Assert.Equal(["undo b", "undo a", "redo a", "redo b"], log);
        Assert.Null(stack.Group(stack.Count, "nothing"));
    }
}
