// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RECompose.Core.Arrangements;
using RECompose.Core.Automation;
using RECompose.Core.Library;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Settings;
using RECompose.Core.Timing;

namespace RECompose.Core.Assistant;

/// <summary>What is playing, as the readouts show it (AS-12 transport). Null when nothing is loaded.</summary>
public sealed record AssistantTransport(string State, int? Entry, string Now, string SongBar, string Elapsed, string Remaining, string? Next, string? Pending, bool Vamp, bool Hold);

/// <summary>The state snapshot sent with every request (AS-12).</summary>
public static class AssistantState
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(
        string mode, string? lockMode, DateTime now, Service? service, IReadOnlyList<Service> services, Func<string, Song?> findSong,
        string? totalLength, CheckResult? check, AssistantTransport? transport, Song? selected, MidiOutSettings midi, JsonObject? pads)
    {
        var state = new JsonObject
        {
            ["mode"] = mode,
            ["lock"] = lockMode,
            ["clock"] = now.ToString("ddd HH:mm", Inv),
            ["service"] = service is null ? null : ServiceNode(service, findSong, totalLength, check),
            ["transport"] = transport is null ? null : new JsonObject
            {
                ["state"] = transport.State,
                ["entry"] = transport.Entry,
                ["now"] = transport.Now,
                ["songBar"] = transport.SongBar,
                ["elapsed"] = transport.Elapsed,
                ["remaining"] = transport.Remaining,
                ["next"] = transport.Next,
                ["pending"] = transport.Pending,
                ["vamp"] = transport.Vamp,
                ["hold"] = transport.Hold,
            },
            ["selectedSong"] = selected is null ? null : SongNode(selected, midi),
            ["sectionNoteMap"] = NoteMap(midi),
            ["pads"] = pads,
            ["otherServices"] = new JsonArray([.. services.Where(s => s.Id != service?.Id).Select(s => (JsonNode)s.Name)]),
        };
        return state.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static JsonObject ServiceNode(Service service, Func<string, Song?> findSong, string? totalLength, CheckResult? check = null)
    {
        var entries = new JsonArray();
        for (var i = 0; i < service.Entries.Count; i++)
        {
            var e = service.Entries[i];
            var song = findSong(e.SongId);
            var next = service.Entries.ElementAtOrDefault(i + 1);
            var nextSong = next is null ? null : findSong(next.SongId);
            var resolved = EntryArrangement.Resolve(e, song);
            entries.Add(new JsonObject
            {
                ["n"] = i + 1,
                ["entryId"] = e.Id,
                ["songId"] = e.SongId,
                ["title"] = song?.Title ?? "(missing song)",
                ["key"] = song is null ? null : ServiceText.EntryKey(song, e.KeyOffset)?.ToDisplayString(),
                ["offset"] = e.KeyOffset,
                ["tempo"] = e.TempoPercent,
                ["arrangement"] = new JsonObject
                {
                    ["type"] = resolved.Type.ToString().ToLowerInvariant(),
                    ["name"] = resolved.Name,
                    ["order"] = song is null ? null : Order(song, resolved.Arrangement),
                },
                ["countIn"] = e.CountIn.Mode == CountInMode.Generated ? $"generated {e.CountIn.Bars.ToString(Inv)}" : e.CountIn.Mode.ToString().ToLowerInvariant(),
                ["transitionAfter"] = ServiceText.Chip(e.TransitionAfter, nextSong is null ? null : ServiceText.EntryKey(nextSong, next!.KeyOffset), nextSong?.Bpm * next?.TempoFactor),
                ["notes"] = e.Notes,
            });
        }

        var node = new JsonObject
        {
            ["id"] = service.Id,
            ["name"] = service.Name,
            ["date"] = service.Date?.ToString("yyyy-MM-ddTHH:mm", Inv),
            ["autoAdvance"] = service.AutoAdvance,
            ["totalLength"] = totalLength,
            ["entries"] = entries,
        };
        if (check is not null)
        {
            node["check"] = new JsonObject
            {
                ["result"] = check.Overall.ToString(),
                ["items"] = new JsonArray([.. check.Items.Where(c => c.Status is CheckStatus.Warning or CheckStatus.Error).Select(c => (JsonNode)$"{c.Title}: {c.Detail}")]),
            };
        }

        return node;
    }

    /// <summary>"1. Intro · 2. Chorus 1 · 3. Chorus 1"; as written when there is no arrangement.</summary>
    private static string Order(Song song, Arrangement? arrangement)
    {
        var sections = SectionList.Describe(song.Sections, song.Grid, song.DurationSec);
        arrangement ??= new Arrangement { Id = "w", Name = "w", Entries = sections.Select(s => ArrangementEntry.ForSection(s.Id)).ToList() };
        return ArrangementSummary.Labels(arrangement, sections);
    }

    public static JsonObject SongNode(Song song, MidiOutSettings midi)
    {
        var sections = SectionList.Describe(song.Sections, song.Grid, song.DurationSec);
        return new JsonObject
        {
            ["id"] = song.Id,
            ["title"] = song.Title,
            ["artist"] = song.Artist,
            ["key"] = Music.Key.TryParse(song.OriginalKey, out var key) ? key.ToDisplayString() : null,
            ["bpm"] = Math.Round(song.Bpm, 1),
            ["meter"] = $"{song.BeatsPerBar.ToString(Inv)}/{song.BeatUnit.ToString(Inv)}",
            ["sections"] = new JsonArray([.. sections.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id,
                ["label"] = s.Label,
                ["type"] = s.Type.ToString().ToLowerInvariant(),
                ["chartBar"] = s.ChartBar,
                ["bars"] = s.LengthBars,
            })]),
            ["variants"] = new JsonArray([.. song.Arrangements.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["default"] = a.Id == song.DefaultArrangementId,
                ["order"] = ArrangementSummary.Labels(a, sections),
            })]),
            ["cues"] = new JsonArray([.. song.Cues.OrderBy(c => c.SourceSec).Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["chartBar"] = Readouts.ChartBar(song.Grid, c.SourceSec).ToString(),
                ["action"] = c.Action.Summary(midi.MiddleC),
                ["enabled"] = c.Enabled,
            })]),
            ["automation"] = new JsonObject { ["midiSectionNotes"] = song.Automation.SectionNotes, ["spokenGuide"] = song.Automation.SpokenGuide },
            ["groups"] = new JsonArray([.. TrackGroups.LaneOrder.Where(g => song.Tracks.Any(t => t.Group == g && !t.Hidden)).Select(g => (JsonNode)g)]),
        };
    }

    private static JsonObject NoteMap(MidiOutSettings midi)
    {
        var map = new JsonObject();
        foreach (var type in Enum.GetValues<SectionType>())
            map[type.ToString().ToLowerInvariant()] = MidiNotes.Name(MidiNotes.NoteFor(type, midi.Notes), midi.MiddleC);
        return map;
    }
}
