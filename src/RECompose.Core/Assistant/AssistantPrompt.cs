// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;

namespace RECompose.Core.Assistant;

/// <summary>One tool the assistant may call (AS-30): its JSON schema properties and whether it changes anything.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject Properties, IReadOnlyList<string> Required, bool Mutates);

/// <summary>The system prompt (AS-11), the tools (AS-30) and the error bubbles (AS-50) of the assistant.</summary>
public static class AssistantPrompt
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public static IReadOnlyList<string> SuggestedModels { get; } = [DefaultModel, "claude-opus-5-5", "claude-haiku-5-5"];

    /// <summary>The last messages sent with each request (AS-20).</summary>
    public const int HistoryLimit = 40;

    /// <summary>Tool rounds per user message (AS-10).</summary>
    public const int MaxRounds = 10;

    public const string PrivacyNote = "The assistant sends song titles, sections, arrangements, service details and app state to Anthropic. Audio is never sent.";
    public const string KeyRejected = "The Anthropic API key was rejected. Update it in Settings > Assistant.";

    public static string ApiError(string message) => $"Assistant error: {message}";

    public static string Offline(string detail) => $"Assistant error: Couldn't reach the Anthropic API ({detail}).";

    /// <summary>
    /// The system prompt (AS-11). It stays the same for a whole conversation: the mode and the state snapshot change, so they go
    /// with each message instead (<see cref="State"/>); editing the system prompt mid-conversation would invalidate the model's
    /// earlier thinking.
    /// </summary>
    public const string System = """
        You are the assistant inside RECompose, used by a church music director to prepare and run the Sunday service.
        Make changes only through tools; never claim a change you did not make.
        - Users often dictate; fix obvious transcription errors ("he change" = "hey, change").
        - "Song N" = entry N of the current service; "the opener" = entry 1.
        - Sections have labels; repeated names are numbered by occurrence (Chorus 1, Chorus 2). Choose instances deliberately and say which you used.
        - Arrangement entries support: bar ranges, repeats (n or until cued), key lifts, mutes (groups/tracks), fades, endings (continue/ringOut/cut), guide-call overrides, MD notes, and click/pad/hold entries.
        - Ask before overwriting a song's shared variant; prefer changing only this service's entry unless the user says otherwise.
        - After changes, review affected songs and mention real problems briefly with an offer to fix (note clashes, missing endings, open repeats at the end, key lifts > ±5, service length vs. any target the user gave, failing service-check items).
        - Every message ends with the mode and the app state as it is now; the latest one is current. In Perform, every change you propose is shown to the MD for confirmation; keep proposals small and never alter the item currently playing.
        - Reply in short plain prose, no headings or lists unless asked.
        """;

    /// <summary>The mode line and the state snapshot (AS-11, AS-12), sent after every user message and every round of tool results.</summary>
    public static string State(string mode, string? lockMode, string stateJson) =>
        $"Mode is {mode} / lock {lockMode ?? "none"}.\n<state>{stateJson}</state>";

    private static JsonObject Props(params (string Name, JsonNode Schema)[] props)
    {
        var o = new JsonObject();
        foreach (var (name, schema) in props)
            o[name] = schema;
        return o;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) };

    private static JsonObject Entry => Int("Entry number in the current service, 1 = the opener.");

    private static JsonObject SongId => Str("Song id from the state, search_library or get_service.");

    private static JsonObject CueAction => new()
    {
        ["type"] = "object",
        ["description"] = "What the cue sends.",
        ["properties"] = Props(
            ("kind", Enum("Default note.", "note", "cc", "program", "speak")),
            ("note", Str("MIDI note name such as C4 or F#3.")),
            ("channel", Int("MIDI channel 1–16.")),
            ("controller", Int("CC number 0–127.")),
            ("value", Int("CC value 0–127.")),
            ("program", Int("Program 0–127.")),
            ("text", Str("Words to speak on the Cue output."))),
    };

    private static JsonObject ArrangementItems => new()
    {
        ["type"] = "array",
        ["description"] = "The new order. Each item is one section (by label such as \"Chorus 2\", shorthand such as \"C\" or \"V2\", or section id), or click/pad bars, or a hold.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = Props(
                ("section", Str("Section label, shorthand or id. An unnumbered type picks the first one after the previous item.")),
                ("click", Int("Bars of click instead of a section.")),
                ("pad", Int("Bars of pad instead of a section.")),
                ("hold", Bool("A hold that waits for GO.")),
                ("bars", Str("Bar range within the section, e.g. \"1-4\".")),
                ("repeat", Int("Plays this many times, 1–8.")),
                ("untilCued", Bool("Repeats until the MD moves on.")),
                ("transpose", Int("Key lift in semitones, −6…+6.")),
                ("mute", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Groups (Drums, Bass, Keys, Electric Gtr, Vocals…) or track names that drop out." }),
                ("firstPassOnly", Bool("Mutes only in the first pass of a repeat.")),
                ("fadeIn", Int("Bars of fade in, 0–4.")),
                ("fadeOut", Int("Bars of fade out, 0–4.")),
                ("ending", Enum("What happens at the end of this item.", "continue", "ringOut", "cut")),
                ("guideCall", Str("\"off\", \"auto\", or the words the guide should call instead.")),
                ("note", Str("MD note, at most 80 characters."))),
        },
    };

    public static IReadOnlyList<ToolSpec> Tools { get; } =
    [
        new("search_library", "Find songs in the library by title or artist.", Props(("query", Str("Words of the title or artist.")), ("limit", Int("At most this many, default 10."))), ["query"], false),
        new("get_song", "Sections, variants, cues, automation and track groups of a song.", Props(("songId", SongId)), ["songId"], false),
        new("get_service", "A service with its entries; the current one when serviceId is left out.", Props(("serviceId", Str("Service id."))), [], false),
        new("set_entry_key", "Plays an entry in another key.", Props(("entry", Entry), ("key", Str("Key name such as F, Bb or C#m."))), ["entry", "key"], true),
        new("set_entry_tempo", "Plays an entry faster or slower, 50–150 %.", Props(("entry", Entry), ("percent", Int("Tempo in percent, 100 = as recorded."))), ["entry", "percent"], true),
        new("set_entry_arrangement", "Sets the order an entry plays. scope \"service\" changes only this service, \"variant\" overwrites the variant the entry plays (ask first), \"newVariant\" saves it as a new variant named newVariantName.",
            Props(("entry", Entry), ("entries", ArrangementItems), ("scope", Enum("Where the change goes.", "service", "variant", "newVariant")), ("newVariantName", Str("Name of the new variant."))), ["entry", "entries", "scope"], true),
        new("set_entry_variant", "Makes an entry play one of its song's variants, or the song as written.", Props(("entry", Entry), ("variant", Str("Variant name or id, or \"asWritten\"."))), ["entry", "variant"], true),
        new("create_variant", "Saves a new named variant on a song.", Props(("songId", SongId), ("name", Str("Variant name.")), ("entries", ArrangementItems)), ["songId", "name", "entries"], true),
        new("set_entry_count_in", "How an entry starts.", Props(("entry", Entry), ("mode", Enum("track = the stems' own lead-in, generated = click bars, none = on the downbeat.", "track", "generated", "none")), ("bars", Int("Generated count-in bars, 1 or 2."))), ["entry", "mode"], true),
        new("set_transition", "What happens after an entry.", Props(("entry", Entry),
            ("type", Enum("The transition.", "stop", "segue", "crossfade", "pad", "carryOver")),
            ("gapBeats", Int("Segue: beats of silence, 0–8.")), ("seconds", Int("Crossfade length, 1–30 s.")),
            ("padKey", Str("Pad key; the next song's key when left out.")), ("click", Bool("Pad: click during the transition."))), ["entry", "type"], true),
        new("add_entry", "Adds a library song to the current service.", Props(("songId", SongId), ("position", Int("Entry number it becomes; the end when left out."))), ["songId"], true),
        new("remove_entry", "Removes an entry from the current service.", Props(("entry", Entry)), ["entry"], true),
        new("move_entry", "Moves an entry to another position.", Props(("from", Entry), ("to", Int("The entry number it becomes."))), ["from", "to"], true),
        new("add_cue", "Adds a cue to a song at a chart bar. Without an action it sends the first free MIDI note from C4.",
            Props(("songId", SongId), ("name", Str("Cue name; Cue N when left out.")), ("chartBar", Int("Chart bar.")), ("beat", Int("Beat in the bar, default 1.")), ("action", CueAction)), ["songId", "chartBar"], true),
        new("update_cue", "Changes a cue.", Props(("songId", SongId), ("cue", Str("Cue name or id.")), ("name", Str("New name.")), ("chartBar", Int("New chart bar.")), ("beat", Int("Beat in the bar.")), ("enabled", Bool("Whether it fires.")), ("action", CueAction)), ["songId", "cue"], true),
        new("delete_cue", "Deletes a cue.", Props(("songId", SongId), ("cue", Str("Cue name or id."))), ["songId", "cue"], true),
        new("update_section", "Renames a section or changes its type.", Props(("songId", SongId), ("section", Str("Section label or id.")), ("label", Str("New name.")), ("type", Str("New type: Intro, Verse, Pre-Chorus, Chorus, Bridge, Tag, Outro, Ending…"))), ["songId", "section"], true),
        new("set_section_automation", "Turns a song's MIDI section notes or spoken guide on or off.", Props(("songId", SongId), ("midiSectionNotes", Bool("A MIDI note at every section start.")), ("spokenGuide", Bool("The computer calls each section a bar ahead."))), ["songId"], true),
        new("create_service", "Creates a service, optionally with songs.", Props(("name", Str("Service name.")), ("date", Str("Local date and time, e.g. 2026-11-02T09:00.")), ("songIds", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } })), ["name"], true),
        new("duplicate_service", "Copies a service with all its entries.", Props(("serviceId", Str("The service to copy; the current one when left out.")), ("name", Str("Name of the copy.")), ("date", Str("Local date and time."))), ["name"], true),
        new("run_service_check", "Runs the service check of the current service and returns its items.", Props(), [], false),
    ];

    public static ToolSpec? Tool(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
