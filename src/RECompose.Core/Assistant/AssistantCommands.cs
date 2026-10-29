// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Nodes;
using RECompose.Core.Arrangements;
using RECompose.Core.Automation;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Settings;
using RECompose.Core.Timing;

namespace RECompose.Core.Assistant;

/// <summary>What the assistant's tools see: the services, the songs and the library (AS-30).</summary>
public sealed record AssistantWorld
{
    public required IReadOnlyList<Service> Services { get; init; }
    public Service? Current { get; init; }
    public required Func<string, Song?> FindSong { get; init; }
    public IReadOnlyList<SongSummary> Library { get; init; } = [];
    public MidiOutSettings Midi { get; init; } = new();

    /// <summary>The service check of the current service (REL-1), or null when it can't run here.</summary>
    public Func<CheckResult>? Check { get; init; }
}

/// <summary>An edit a tool made: a song and any services, or a new service, applied by the app as one undo step.</summary>
public sealed record AssistantChange(string Description, Song? Song, IReadOnlyList<Service> Services, Service? NewService = null)
{
    /// <summary>The entries the change touches, for the Perform confirmation (AS-45).</summary>
    public IReadOnlyList<string> EntryIds { get; init; } = [];
}

/// <summary>A tool's result (AS-30): <c>{ok, summary, data?}</c>; a mutation also carries its change.</summary>
public sealed record AssistantResult(bool Ok, string Summary, JsonNode? Data = null, AssistantChange? Change = null)
{
    public static AssistantResult Error(string message) => new(false, message);

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["ok"] = Ok, ["summary"] = Summary };
        if (Data is not null)
            o["data"] = Data.DeepClone();
        return o;
    }
}

/// <summary>
/// The assistant's tools (AS-30), pure: each call reads the world and returns data or a change built with the same Core
/// edits the app uses for manual edits. Nothing here applies anything.
/// </summary>
public static class AssistantCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static AssistantResult Run(string tool, JsonObject input, AssistantWorld world)
    {
        try
        {
            return tool switch
            {
                "search_library" => Search(input, world),
                "get_song" => GetSong(input, world),
                "get_service" => GetService(input, world),
                "set_entry_key" => SetKey(input, world),
                "set_entry_tempo" => SetTempo(input, world),
                "set_entry_arrangement" => SetArrangement(input, world),
                "set_entry_variant" => SetVariant(input, world),
                "create_variant" => CreateVariant(input, world),
                "set_entry_count_in" => SetCountIn(input, world),
                "set_transition" => SetTransition(input, world),
                "add_entry" => AddEntry(input, world),
                "remove_entry" => RemoveEntry(input, world),
                "move_entry" => MoveEntry(input, world),
                "add_cue" => AddCue(input, world),
                "update_cue" => UpdateCue(input, world),
                "delete_cue" => DeleteCue(input, world),
                "update_section" => UpdateSection(input, world),
                "set_section_automation" => SetAutomation(input, world),
                "create_service" => CreateService(input, world),
                "duplicate_service" => DuplicateService(input, world),
                "run_service_check" => RunCheck(world),
                _ => AssistantResult.Error($"There is no tool {tool}."),
            };
        }
        catch (ToolException e)
        {
            return AssistantResult.Error(e.Message);
        }
    }

    private sealed class ToolException(string message) : Exception(message);

    // ---- Reading the input ----------------------------------------------------------------------------

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    private static int? Int(JsonObject o, string name)
    {
        if (o[name] is not JsonValue v)
            return null;
        if (v.TryGetValue<int>(out var i))
            return i;
        if (v.TryGetValue<double>(out var d))
            return (int)Math.Round(d);
        return v.TryGetValue<string>(out var s) && int.TryParse(s.Replace('−', '-'), NumberStyles.Integer, Inv, out var p) ? p : null;
    }

    private static bool? Bool(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static string Need(JsonObject o, string name) =>
        Str(o, name) is { Length: > 0 } s ? s : throw new ToolException($"{name} is missing.");

    private static Service CurrentService(AssistantWorld world) =>
        world.Current ?? throw new ToolException("No service is open.");

    private static (Service Service, ServiceEntry Entry, Song Song, int Number) EntryOf(JsonObject o, AssistantWorld world, string name = "entry")
    {
        var service = CurrentService(world);
        var n = Int(o, name) ?? throw new ToolException($"{name} is missing.");
        if (n < 1 || n > service.Entries.Count)
            throw new ToolException($"{service.Name} has {Songs(service.Entries.Count)}; there is no song {n}.");
        var entry = service.Entries[n - 1];
        var song = world.FindSong(entry.SongId) ?? throw new ToolException($"Song {n} is no longer in the library.");
        return (service, entry, song, n);
    }

    private static Song SongOf(JsonObject o, AssistantWorld world)
    {
        var id = Need(o, "songId");
        if (world.FindSong(id) is { } song)
            return song;
        var byTitle = world.Library.FirstOrDefault(s => s.Title.Equals(id, StringComparison.OrdinalIgnoreCase));
        return (byTitle is null ? null : world.FindSong(byTitle.Id)) ?? throw new ToolException($"No song {id} in the library.");
    }

    private static string Songs(int n) => n == 1 ? "1 song" : $"{n.ToString(Inv)} songs";

    private static List<SectionInfo> Sections(Song song) => SectionList.Describe(song.Sections, song.Grid, song.DurationSec);

    private static AssistantResult Changed(string summary, Song? song, IReadOnlyList<Service> services, params string[] entryIds) =>
        new(true, summary, Change: new AssistantChange(summary, song, services) { EntryIds = entryIds });

    // ---- Reads ------------------------------------------------------------------------------------------

    private static AssistantResult Search(JsonObject o, AssistantWorld world)
    {
        var words = (Str(o, "query") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var limit = Math.Clamp(Int(o, "limit") ?? 10, 1, 50);
        var found = world.Library
            .Where(s => words.All(w => s.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || (s.Artist?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Take(limit)
            .Select(s => (JsonNode)new JsonObject { ["songId"] = s.Id, ["title"] = s.Title, ["artist"] = s.Artist, ["meta"] = s.MetaLine })
            .ToArray();
        return new AssistantResult(true, found.Length == 0 ? "No songs found." : $"Found {Songs(found.Length)}.", new JsonArray(found));
    }

    private static AssistantResult GetSong(JsonObject o, AssistantWorld world)
    {
        var song = SongOf(o, world);
        return new AssistantResult(true, song.Title, AssistantState.SongNode(song, world.Midi));
    }

    private static AssistantResult GetService(JsonObject o, AssistantWorld world)
    {
        var service = Str(o, "serviceId") is { } id
            ? world.Services.FirstOrDefault(s => s.Id == id || s.Name.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolException($"No service {id}.")
            : CurrentService(world);
        return new AssistantResult(true, service.Name, AssistantState.ServiceNode(service, world.FindSong, null));
    }

    private static AssistantResult RunCheck(AssistantWorld world)
    {
        CurrentService(world);
        var result = world.Check?.Invoke() ?? throw new ToolException("The service check can't run right now.");
        var items = result.Items.Where(i => i.Status != CheckStatus.Info)
            .Select(i => (JsonNode)new JsonObject { ["status"] = i.Status.ToString(), ["title"] = i.Title, ["detail"] = i.Detail })
            .ToArray();
        return new AssistantResult(true, $"Service check: {result.Text}.", new JsonObject
        {
            ["result"] = result.Overall.ToString(),
            ["items"] = new JsonArray(items),
        });
    }

    // ---- Entries ------------------------------------------------------------------------------------------

    private static AssistantResult UpdateEntry(Service service, ServiceEntry entry, Func<ServiceEntry, ServiceEntry> change, string summary)
    {
        var after = ServiceEdit.Update(service, entry.Id, change);
        return Changed(summary, null, [after], entry.Id);
    }

    private static AssistantResult SetKey(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, _) = EntryOf(o, world);
        var text = Need(o, "key");
        if (!Key.TryParse(text, out var target))
            throw new ToolException($"\"{text}\" is not a key.");
        if (!Key.TryParse(song.OriginalKey, out var original))
            throw new ToolException($"{song.Title} has no known key; set its key in the song first.");
        var offset = ServiceText.KeyOffsetTo(original, target);
        var key = original.Transpose(offset).ToDisplayString();
        var from = ServiceText.EntryKey(song, entry.KeyOffset)?.ToDisplayString();
        var result = UpdateEntry(service, entry, e => e with { KeyOffset = offset }, $"{service.Name}: {song.Title} to {key}.");
        return result with { Data = new JsonObject { ["key"] = key, ["offset"] = offset, ["was"] = from, ["original"] = original.ToDisplayString() } };
    }

    private static AssistantResult SetTempo(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, _) = EntryOf(o, world);
        var percent = Int(o, "percent") ?? throw new ToolException("percent is missing.");
        if (percent < ServiceEntry.MinTempoPercent || percent > ServiceEntry.MaxTempoPercent)
            throw new ToolException($"Tempo goes from {ServiceEntry.MinTempoPercent} to {ServiceEntry.MaxTempoPercent} %.");
        return UpdateEntry(service, entry, e => e with { TempoPercent = percent }, $"{service.Name}: {song.Title} at {percent.ToString(Inv)}%.");
    }

    private static AssistantResult SetCountIn(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, _) = EntryOf(o, world);
        var mode = Need(o, "mode").ToLowerInvariant() switch
        {
            "track" => CountInMode.Track,
            "generated" => CountInMode.Generated,
            "none" => CountInMode.None,
            var m => throw new ToolException($"\"{m}\" is not a count-in; use track, generated or none."),
        };
        var bars = Math.Clamp(Int(o, "bars") ?? 1, 1, 2);
        var desc = mode switch
        {
            CountInMode.Generated => bars == 1 ? "of 1 generated bar" : "of 2 generated bars",
            CountInMode.None => "off",
            _ => "from the track",
        };
        return UpdateEntry(service, entry, e => e with { CountIn = new CountIn { Mode = mode, Bars = bars } }, $"{service.Name}: {song.Title} count-in {desc}.");
    }

    private static AssistantResult SetTransition(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, n) = EntryOf(o, world);
        var type = Need(o, "type").ToLowerInvariant() switch
        {
            "stop" => TransitionType.Stop,
            "segue" => TransitionType.Segue,
            "crossfade" => TransitionType.Crossfade,
            "pad" => TransitionType.Pad,
            "carryover" => TransitionType.CarryOver,
            var t => throw new ToolException($"\"{t}\" is not a transition."),
        };
        var padKey = Str(o, "padKey");
        if (padKey is not null && !Key.TryParse(padKey, out _))
            throw new ToolException($"\"{padKey}\" is not a key.");
        var transition = (entry.TransitionAfter with
        {
            Type = type,
            SegueGapBeats = Int(o, "gapBeats") ?? entry.TransitionAfter.SegueGapBeats,
            CrossfadeSec = Int(o, "seconds") ?? entry.TransitionAfter.CrossfadeSec,
            PadKey = padKey is null ? entry.TransitionAfter.PadKey : Key.Parse(padKey).ToString(),
            ClickDuring = Bool(o, "click") ?? entry.TransitionAfter.ClickDuring,
        }).Clamped();
        var next = service.Entries.ElementAtOrDefault(n);
        var nextSong = next is null ? null : world.FindSong(next.SongId);
        var chip = ServiceText.Chip(transition, nextSong is null ? null : ServiceText.EntryKey(nextSong, next!.KeyOffset), nextSong?.Bpm * next?.TempoFactor);
        return UpdateEntry(service, entry, e => e with { TransitionAfter = transition }, $"{service.Name}: after {song.Title} — {chip}.");
    }

    private static AssistantResult AddEntry(JsonObject o, AssistantWorld world)
    {
        var service = CurrentService(world);
        var song = SongOf(o, world);
        var position = Math.Clamp(Int(o, "position") ?? service.Entries.Count + 1, 1, service.Entries.Count + 1);
        var entry = ServiceEdit.NewEntry(song);
        var after = ServiceEdit.Add(service, entry, position - 1);
        return Changed($"{service.Name}: added {song.Title} as song {position.ToString(Inv)}.", null, [after], entry.Id);
    }

    private static AssistantResult RemoveEntry(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, _) = EntryOf(o, world);
        return Changed($"{service.Name}: removed {song.Title}.", null, [ServiceEdit.Remove(service, entry.Id)], entry.Id);
    }

    private static AssistantResult MoveEntry(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, _) = EntryOf(o, world, "from");
        var to = Math.Clamp(Int(o, "to") ?? throw new ToolException("to is missing."), 1, service.Entries.Count);
        return Changed($"{service.Name}: {song.Title} is now song {to.ToString(Inv)}.", null, [ServiceEdit.Move(service, entry.Id, to - 1)], entry.Id);
    }

    // ---- Arrangements -------------------------------------------------------------------------------------

    /// <summary>The arrangement items of a tool call as entries of <paramref name="song"/> (AS-30, ARR-30…40).</summary>
    public static List<ArrangementEntry> Entries(JsonArray? items, Song song)
    {
        if (items is null || items.Count == 0)
            throw new ToolException("entries is empty.");
        var sections = Sections(song);
        var result = new List<ArrangementEntry>();
        SectionInfo? previous = null;
        foreach (var node in items)
        {
            var item = node as JsonObject ?? new JsonObject { ["section"] = node?.DeepClone() };
            ArrangementEntry entry;
            if (Int(item, "click") is { } click)
                entry = ArrangementEntry.ClickBars(click);
            else if (Int(item, "pad") is { } pad)
                entry = ArrangementEntry.PadBars(pad);
            else if (Bool(item, "hold") == true)
                entry = ArrangementEntry.NewHold();
            else
            {
                var text = Need(item, "section");
                var section = sections.FirstOrDefault(s => s.Id == text)
                    ?? sections.FirstOrDefault(s => s.Label.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? QuickOrder.Section(text, sections, previous, out var problem)
                    ?? throw new ToolException(problem ?? $"\"{text}\" is not a section of {song.Title}.");
                previous = section;
                entry = ArrangementEntry.ForSection(section.Id) with { Range = Range(Str(item, "bars"), section) };
            }

            result.Add(WithOptions(entry, item, song).Clamped());
        }

        return result;
    }

    private static BarRange Range(string? text, SectionInfo section)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new BarRange();
        var parts = text.Replace('–', '-').Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], Inv, out var from) || !int.TryParse(parts[1], Inv, out var to) || from < 1 || to < from || to > section.LengthBars)
            throw new ToolException($"Bars \"{text}\" are not within {section.Label} (1–{section.LengthBars.ToString(Inv)}).");
        return new BarRange { FromBar = from, ToBar = to == section.LengthBars ? null : to };
    }

    private static ArrangementEntry WithOptions(ArrangementEntry entry, JsonObject item, Song song)
    {
        if (Int(item, "repeat") is { } count)
            entry = entry with { Repeat = new RepeatSpec { Count = Math.Clamp(count, 1, RepeatSpec.MaxCount) } };
        if (Bool(item, "untilCued") == true)
            entry = entry with { Repeat = new RepeatSpec { UntilCued = true } };
        if (Int(item, "transpose") is { } lift)
            entry = Math.Abs(lift) <= ArrangementEntry.MaxLift ? entry with { Transpose = lift } : throw new ToolException("Key lifts go up to ±6 semitones.");
        if (item["mute"] is JsonArray mutes)
            entry = entry with { Mutes = Mutes(mutes, song) with { FirstPassOnly = Bool(item, "firstPassOnly") == true } };
        if (Int(item, "fadeIn") is { } fadeIn)
            entry = entry with { FadeInBars = fadeIn };
        if (Int(item, "fadeOut") is { } fadeOut)
            entry = entry with { FadeOutBars = fadeOut };
        if (Str(item, "ending") is { } ending)
        {
            entry = entry with
            {
                Ending = ending.ToLowerInvariant() switch
                {
                    "continue" => EndingMode.Continue,
                    "ringout" => EndingMode.RingOut,
                    "cut" => EndingMode.Cut,
                    _ => throw new ToolException($"\"{ending}\" is not an ending; use continue, ringOut or cut."),
                },
            };
        }

        if (Str(item, "guideCall") is { } call)
        {
            entry = call.ToLowerInvariant() switch
            {
                "off" => entry with { GuideCall = GuideCallMode.Off },
                "auto" => entry with { GuideCall = GuideCallMode.Auto },
                _ => entry with { GuideCall = GuideCallMode.Custom, GuideCallText = call },
            };
        }

        if (Str(item, "note") is { } note)
            entry = entry with { Note = note.Length > ArrangementEntry.MaxNoteLength ? note[..ArrangementEntry.MaxNoteLength] : note };
        return entry;
    }

    private static MuteSpec Mutes(JsonArray names, Song song)
    {
        var groups = new List<string>();
        var tracks = new List<string>();
        foreach (var name in names.Select(n => n?.GetValue<string>()).OfType<string>())
        {
            var group = TrackGroups.LaneOrder.FirstOrDefault(g => g.Equals(name, StringComparison.OrdinalIgnoreCase)
                || g.Replace(" Gtr", " Guitar", StringComparison.Ordinal).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
                groups.Add(group);
            else if (song.Tracks.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } track)
                tracks.Add(track.Id);
            else
                throw new ToolException($"{song.Title} has no group or track \"{name}\". Groups: {string.Join(", ", song.Tracks.Select(t => t.Group).Distinct())}.");
        }

        return new MuteSpec { Groups = groups.Distinct().ToList(), TrackIds = tracks.Distinct().ToList() };
    }

    private static string OrderText(Song song, IEnumerable<ArrangementEntry> entries) =>
        ArrangementSummary.Labels(new Arrangement { Id = "x", Name = "x", Entries = entries.ToList() }, Sections(song));

    private static AssistantResult SetArrangement(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, _) = EntryOf(o, world);
        var entries = Entries(o["entries"] as JsonArray, song);
        var summary = $"{song.Title} arrangement: {OrderText(song, entries)}.";
        var scope = o["scope"] is JsonObject scoped && Str(scoped, "newVariant") is { } named ? "newvariant" : (Str(o, "scope") ?? "service").ToLowerInvariant();
        switch (scope)
        {
            case "service":
                var custom = new Arrangement { Id = Arrangement.NewId(), Name = EntryArrangement.CustomName, Entries = entries, Origin = ArrangementOrigin.Assistant };
                return Changed(summary, null, [ServiceEdit.Update(service, entry.Id, e => e with { Arrangement = ArrangementChoice.OfCustom(custom) })], entry.Id);

            case "variant":
                var variant = entry.Arrangement.Type == ArrangementChoiceType.Variant ? song.Arrangements.FirstOrDefault(a => a.Id == entry.Arrangement.ArrangementId) : null;
                if (variant is null)
                    throw new ToolException($"Song {Int(o, "entry")} doesn't play a variant; use scope \"service\" or \"newVariant\".");
                var songAfter = song with { Arrangements = ArrangementEdit.ReplaceVariant(song.Arrangements, variant with { Entries = entries }), UpdatedAt = DateTimeOffset.UtcNow };
                var users = world.Services.SelectMany(s => s.Entries).Where(e => e.SongId == song.Id && e.Arrangement.ArrangementId == variant.Id).Select(e => e.Id).ToArray();
                return Changed($"{song.Title} \"{variant.Name}\": {OrderText(song, entries)}.", songAfter, [], users);

            case "newvariant":
                var name = (o["scope"] as JsonObject)?["newVariant"]?.GetValue<string>() ?? Str(o, "newVariantName") ?? throw new ToolException("newVariantName is missing.");
                var (withVariant, made) = AddVariant(song, name, entries);
                return Changed($"{song.Title}: new variant \"{made.Name}\": {OrderText(song, entries)}.", withVariant,
                    [ServiceEdit.Update(service, entry.Id, e => e with { Arrangement = ArrangementChoice.OfVariant(made.Id) })], entry.Id);

            default:
                throw new ToolException($"\"{scope}\" is not a scope; use service, variant or newVariant.");
        }
    }

    private static (Song Song, Arrangement Variant) AddVariant(Song song, string name, List<ArrangementEntry> entries)
    {
        var variant = new Arrangement
        {
            Id = Arrangement.NewId(),
            Name = ArrangementEdit.UniqueName(name, song.Arrangements.Select(a => a.Name)),
            Entries = entries,
            Origin = ArrangementOrigin.Assistant,
        };
        return (song with { Arrangements = [.. song.Arrangements, variant], UpdatedAt = DateTimeOffset.UtcNow }, variant);
    }

    private static AssistantResult CreateVariant(JsonObject o, AssistantWorld world)
    {
        var song = SongOf(o, world);
        var (after, variant) = AddVariant(song, Need(o, "name"), Entries(o["entries"] as JsonArray, song));
        return Changed($"{song.Title}: new variant \"{variant.Name}\".", after, []);
    }

    private static AssistantResult SetVariant(JsonObject o, AssistantWorld world)
    {
        var (service, entry, song, _) = EntryOf(o, world);
        var name = Need(o, "variant");
        if (name.Replace(" ", string.Empty).Equals("asWritten", StringComparison.OrdinalIgnoreCase))
            return UpdateEntry(service, entry, e => e with { Arrangement = ArrangementChoice.AsWritten }, $"{service.Name}: {song.Title} plays as written.");
        var variant = song.Arrangements.FirstOrDefault(a => a.Id == name || a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"{song.Title} has no variant \"{name}\". Variants: {(song.Arrangements.Count == 0 ? "none" : string.Join(", ", song.Arrangements.Select(a => a.Name)))}.");
        return UpdateEntry(service, entry, e => e with { Arrangement = ArrangementChoice.OfVariant(variant.Id) }, $"{service.Name}: {song.Title} uses \"{variant.Name}\".");
    }

    // ---- Cues and sections -----------------------------------------------------------------------------------

    private static double ChartTime(JsonObject o, Song song, double? fallback = null)
    {
        if (Int(o, "chartBar") is not { } bar)
            return fallback ?? throw new ToolException("chartBar is missing.");
        var beat = Math.Clamp(Int(o, "beat") ?? 1, 1, song.BeatsPerBar);
        var sec = song.Grid.BarToTime(bar) + (beat - 1) * 60.0 / song.Bpm;
        if (bar < 1 || sec > song.DurationSec)
            throw new ToolException($"{song.Title} has {Readouts.TotalBars(song.Grid, song.DurationSec).ToString(Inv)} bars; bar {bar.ToString(Inv)} is outside it.");
        return Math.Round(sec, 3);
    }

    private static CueAction Action(JsonObject? a, CueAction current, AssistantWorld world)
    {
        if (a is null)
            return current;
        var kind = (Str(a, "kind") ?? "note").ToLowerInvariant() switch
        {
            "note" => CueActionKind.MidiNote,
            "cc" => CueActionKind.ControlChange,
            "program" => CueActionKind.ProgramChange,
            "speak" => CueActionKind.Speak,
            var k => throw new ToolException($"\"{k}\" is not a cue action; use note, cc, program or speak."),
        };
        var action = current with { Kind = kind, Channel = Math.Clamp(Int(a, "channel") ?? current.Channel, 1, 16) };
        if (Str(a, "note") is { } noteText)
            action = action with { Note = MidiNotes.Parse(noteText, world.Midi.MiddleC) ?? throw new ToolException($"\"{noteText}\" is not a MIDI note.") };
        if (Int(a, "controller") is { } cc)
            action = action with { Controller = Math.Clamp(cc, 0, 127) };
        if (Int(a, "value") is { } value)
            action = action with { Value = Math.Clamp(value, 0, 127) };
        if (Int(a, "program") is { } program)
            action = action with { Program = Math.Clamp(program, 0, 127) };
        if (Str(a, "text") is { } text)
            action = action with { Text = text };
        if (kind == CueActionKind.Speak && string.IsNullOrWhiteSpace(action.Text))
            throw new ToolException("A Speak cue needs its words in text.");
        return action;
    }

    private static Cue FindCue(Song song, string name) =>
        song.Cues.FirstOrDefault(c => c.Id == name || c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ToolException($"{song.Title} has no cue \"{name}\".");

    private static Song WithCues(Song song, List<Cue> cues) => song with { Cues = cues, UpdatedAt = DateTimeOffset.UtcNow };

    private static AssistantResult AddCue(JsonObject o, AssistantWorld world)
    {
        var song = SongOf(o, world);
        var cue = new Cue
        {
            Id = Cue.NewId(),
            Name = Str(o, "name") is { Length: > 0 } n ? n : Cue.NextName(song.Cues),
            SourceSec = ChartTime(o, song),
            Action = new CueAction { Note = MidiNotes.FirstFreeNote(song.Cues, world.Midi.Notes, world.Midi.MiddleC), Channel = world.Midi.Channel },
        };
        cue = cue with { Action = Action(o["action"] as JsonObject, cue.Action, world) };
        var bar = Readouts.ChartBar(song.Grid, cue.SourceSec);
        return Changed($"{song.Title}: {cue.Name} at bar {bar} sends {cue.Action.Summary(world.Midi.MiddleC)}.", WithCues(song, [.. song.Cues, cue]), []);
    }

    private static AssistantResult UpdateCue(JsonObject o, AssistantWorld world)
    {
        var song = SongOf(o, world);
        var cue = FindCue(song, Need(o, "cue"));
        var after = cue with
        {
            Name = Str(o, "name") is { Length: > 0 } n ? n : cue.Name,
            SourceSec = ChartTime(o, song, cue.SourceSec),
            Enabled = Bool(o, "enabled") ?? cue.Enabled,
            Action = Action(o["action"] as JsonObject, cue.Action, world),
        };
        var cues = song.Cues.Select(c => c.Id == cue.Id ? after : c).ToList();
        var what = after.Action != cue.Action ? $"now sends {after.Action.Summary(world.Midi.MiddleC)}"
            : after.SourceSec != cue.SourceSec ? $"moves to bar {Readouts.ChartBar(song.Grid, after.SourceSec)}"
            : after.Enabled != cue.Enabled ? (after.Enabled ? "is on" : "is off")
            : "is unchanged";
        return Changed($"{song.Title}: {after.Name} {what}.", WithCues(song, cues), []);
    }

    private static AssistantResult DeleteCue(JsonObject o, AssistantWorld world)
    {
        var song = SongOf(o, world);
        var cue = FindCue(song, Need(o, "cue"));
        return Changed($"{song.Title}: deleted {cue.Name}.", WithCues(song, song.Cues.Where(c => c.Id != cue.Id).ToList()), []);
    }

    private static AssistantResult UpdateSection(JsonObject o, AssistantWorld world)
    {
        var song = SongOf(o, world);
        var infos = Sections(song);
        var text = Need(o, "section");
        var section = infos.FirstOrDefault(s => s.Id == text || s.Label.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? QuickOrder.Section(text, infos, null, out var problem) ?? throw new ToolException(problem ?? $"No section {text}.");
        var sections = song.Sections;
        if (Str(o, "type") is { } typeText)
        {
            var type = SectionTypes.FromName(typeText);
            if (type == SectionType.Other && !typeText.Equals("other", StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"\"{typeText}\" is not a section type.");
            sections = SectionList.SetType(sections, section.Id, type);
        }

        if (Str(o, "label") is { Length: > 0 } label)
            sections = SectionList.Rename(sections, section.Id, label);
        var after = song with { Sections = sections, UpdatedAt = DateTimeOffset.UtcNow };
        var newLabel = Sections(after).First(s => s.Id == section.Id).Label;
        return Changed($"{song.Title}: {section.Label} is now {newLabel}.", after, []);
    }

    private static AssistantResult SetAutomation(JsonObject o, AssistantWorld world)
    {
        var song = SongOf(o, world);
        var notes = Bool(o, "midiSectionNotes");
        var guide = Bool(o, "spokenGuide");
        if (notes is null && guide is null)
            throw new ToolException("Give midiSectionNotes or spokenGuide.");
        var automation = song.Automation with { SectionNotes = notes ?? song.Automation.SectionNotes, SpokenGuide = guide ?? song.Automation.SpokenGuide };
        var parts = new List<string>();
        if (notes is { } n)
            parts.Add(n ? "section notes on" : "section notes off");
        if (guide is { } g)
            parts.Add(g ? "spoken guide on" : "spoken guide off");
        return Changed($"{song.Title}: {string.Join(", ", parts)}.", song with { Automation = automation, UpdatedAt = DateTimeOffset.UtcNow }, []);
    }

    // ---- Services ---------------------------------------------------------------------------------------------

    private static DateTime? Date(JsonObject o) =>
        Str(o, "date") is { Length: > 0 } text
            ? DateTime.TryParse(text, Inv, DateTimeStyles.AssumeLocal, out var d) ? d : throw new ToolException($"\"{text}\" is not a date.")
            : null;

    private static AssistantResult CreateService(JsonObject o, AssistantWorld world)
    {
        var service = ServiceEdit.New(Need(o, "name"), Date(o));
        foreach (var id in (o["songIds"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>())
            service = ServiceEdit.Add(service, ServiceEdit.NewEntry(world.FindSong(id) ?? throw new ToolException($"No song {id} in the library.")));
        var summary = $"Created {service.Name} with {Songs(service.Entries.Count)}.";
        return new AssistantResult(true, summary, new JsonObject { ["serviceId"] = service.Id }, new AssistantChange(summary, null, [], service));
    }

    private static AssistantResult DuplicateService(JsonObject o, AssistantWorld world)
    {
        var source = Str(o, "serviceId") is { } id
            ? world.Services.FirstOrDefault(s => s.Id == id || s.Name.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolException($"No service {id}.")
            : CurrentService(world);
        var copy = ServiceEdit.Duplicate(source, Need(o, "name"), Date(o) ?? source.Date?.AddDays(7));
        var summary = $"Created {copy.Name} with {Songs(copy.Entries.Count)}.";
        return new AssistantResult(true, summary, new JsonObject { ["serviceId"] = copy.Id }, new AssistantChange(summary, null, [], copy));
    }
}
