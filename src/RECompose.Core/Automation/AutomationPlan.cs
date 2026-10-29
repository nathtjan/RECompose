// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Settings;
using RECompose.Core.Voice;

namespace RECompose.Core.Automation;

/// <summary>
/// A MIDI message at a plan second (AUTO-5, AUTO-21). <paramref name="Id"/> &gt; 0 names a log line; an empty message is only
/// reported back when the playhead passes it (a Speak cue's log line).
/// </summary>
public sealed record TimedMidi(double AtSec, byte[] Bytes, int Id = 0);

/// <summary>
/// A spoken call at a plan second (AUTO-30, AUTO-31, PB-71): text to speak with the platform voice, or, with
/// <paramref name="FromSec"/>, the guide's own recording of it (source seconds of the guide file). It is cut at
/// <paramref name="LengthSec"/> when that is above 0.
/// </summary>
public sealed record TimedCall(double AtSec, string Text, double? FromSec = null, double LengthSec = 0);

/// <summary>A written guide call that announces the wrong section and what replaces it (AUTO-30), for the log.</summary>
public sealed record GuideReplacement(double AtSec, string Label);

/// <summary>Everything the plan does besides playing the stems (AUTO-*): MIDI, spoken calls and where the guide is silenced.</summary>
public sealed record AutomationEvents(
    IReadOnlyList<TimedMidi> Midi,
    IReadOnlyList<TimedCall> Calls,
    IReadOnlyList<(double Start, double End)> GuideSilences,
    IReadOnlyList<GuideReplacement> Replacements,
    IReadOnlyDictionary<int, string> Lines)
{
    public static AutomationEvents None { get; } = new([], [], [], [], new Dictionary<int, string>());
}

/// <summary>What builds the automation besides the plan and the song.</summary>
public sealed record AutomationOptions
{
    public MidiOutSettings Midi { get; init; } = new();
    public bool ReplaceGuideCalls { get; init; } = true;

    /// <summary>The playhead: a spoken guide call that would start before it, for a section that hasn't, starts now (AUTO-31).</summary>
    public double NowSec { get; init; }
}

/// <summary>
/// Turns a plan into its automation (AUTO-5, AUTO-21, AUTO-30, AUTO-31, PB-71), in plan seconds. Pure: the app sends the
/// result with the plan, and the engine fires it on the audio clock.
/// </summary>
public static class AutomationPlan
{
    private const double CallMarginSec = 0.05;

    public static AutomationEvents Build(SongPlan plan, Song song, AutomationOptions? options = null)
    {
        options ??= new AutomationOptions();
        if (plan.IsEmpty)
            return AutomationEvents.None;

        var midi = new List<TimedMidi>();
        var calls = new List<TimedCall>();
        var lines = new Dictionary<int, string>();
        var nextId = 1;
        var m = options.Midi;
        var channel = Math.Clamp(m.Channel, 1, 16) - 1;

        void Note(double at, int ch, int note, int velocity, int lengthMs, string line)
        {
            var id = nextId++;
            lines[id] = line;
            midi.Add(new TimedMidi(at, [(byte)(0x90 | ch), (byte)Math.Clamp(note, 0, 127), (byte)Math.Clamp(velocity, 1, 127)], id));
            midi.Add(new TimedMidi(at + Math.Max(1, lengthMs) / 1000.0, [(byte)(0x80 | ch), (byte)Math.Clamp(note, 0, 127), 0]));
        }

        // AUTO-21: the type's note at the downbeat of every section item, live ones included.
        if (song.Automation.SectionNotes)
        {
            PlanItem? previous = null;
            foreach (var item in plan.Items.Where(i => i.Length > 0))
            {
                if (item.Kind == PlanItemKind.Section && item.Section is { } section
                    && (m.ResendOnRepeat || !(item.Pass > 1 && previous?.EntryId == item.EntryId && previous?.Section?.Id == section.Id)))
                {
                    var note = MidiNotes.NoteFor(section.Type, m.Notes);
                    Note(item.PlanStart, channel, note, m.Velocity, m.LengthMs, $"Section {item.Label}: MIDI note {MidiNotes.Name(note, m.MiddleC)}");
                }

                previous = item;
            }
        }

        AddCues(plan, song, m, midi, calls, lines, ref nextId, Note);
        if (song.Automation.SpokenGuide)
            AddSpokenGuide(plan, options.NowSec, calls);
        AddCounts(plan, calls);

        var silences = new List<(double, double)>();
        var replacements = new List<GuideReplacement>();
        if (options.ReplaceGuideCalls)
            AddReplacements(plan, song, calls, silences, replacements);

        return new AutomationEvents(
            midi.OrderBy(e => e.AtSec).ToList(), calls.OrderBy(c => c.AtSec).ToList(), silences, replacements, lines);
    }

    /// <summary>AUTO-5, AUTO-8: a cue fires wherever a section or lead-in item plays its source time; never in a tail.</summary>
    private static void AddCues(SongPlan plan, Song song, MidiOutSettings m, List<TimedMidi> midi, List<TimedCall> calls,
        Dictionary<int, string> lines, ref int nextId, Action<double, int, int, int, int, string> note)
    {
        var items = plan.Items;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!item.HasSource || item.Length <= 0 || item.Kind is not (PlanItemKind.Section or PlanItemKind.LeadIn or PlanItemKind.Song))
                continue;

            var next = items.Skip(index + 1).FirstOrDefault(i => i.Length > 0);
            var lastPass = !(next is not null && next.EntryId is not null && next.EntryId == item.EntryId && next.Pass == item.Pass + 1);
            foreach (var cue in song.Cues.Where(c => c.Enabled))
            {
                if ((cue.Passes == CuePasses.First && item.Pass != 1) || (cue.Passes == CuePasses.Last && !lastPass))
                    continue;
                var src = cue.FireSec(song.Grid);
                if (src < item.SrcStart - 1e-6 || src >= item.SrcEnd - 1e-6)
                    continue;

                var at = item.PlanStart + (src - item.SrcStart);
                var a = cue.Action;
                var line = $"{cue.LogName}: {a.Summary(m.MiddleC)}";
                var ch = (byte)(Math.Clamp(a.Channel, 1, 16) - 1);
                switch (a.Kind)
                {
                    case CueActionKind.MidiNote:
                        note(at, ch, a.Note, a.Velocity, a.LengthMs, line);
                        break;
                    case CueActionKind.ControlChange:
                        lines[nextId] = line;
                        midi.Add(new TimedMidi(at, [(byte)(0xB0 | ch), (byte)Math.Clamp(a.Controller, 0, 127), (byte)Math.Clamp(a.Value, 0, 127)], nextId++));
                        break;
                    case CueActionKind.ProgramChange:
                        lines[nextId] = line;
                        midi.Add(new TimedMidi(at, [(byte)(0xC0 | ch), (byte)Math.Clamp(a.Program, 0, 127)], nextId++));
                        break;
                    default:
                        if (string.IsNullOrWhiteSpace(a.Text))
                            break;
                        lines[nextId] = line;
                        midi.Add(new TimedMidi(at, [], nextId++)); // nothing to send; the log line when it fires
                        calls.Add(new TimedCall(at, a.Text.Trim()));
                        break;
                }
            }
        }
    }

    /// <summary>AUTO-31: each section's spoken name one bar before it (clamped to the plan start), "Ending" one bar before the tail.</summary>
    private static void AddSpokenGuide(SongPlan plan, double nowSec, List<TimedCall> calls)
    {
        var bar = plan.SongGrid.BarLength;
        foreach (var item in plan.Items.Where(i => i.Length > 0))
        {
            string? text = item.Kind switch
            {
                PlanItemKind.Section when item.GuideCall == GuideCallMode.Off => null,
                PlanItemKind.Section when item.GuideCall == GuideCallMode.Custom && !string.IsNullOrWhiteSpace(item.GuideCallText) => item.GuideCallText!.Trim(),
                PlanItemKind.Section => VoiceText.Spoken(item.Label),
                PlanItemKind.Tail when item.Id == "tail" => "Ending",
                _ => null,
            };
            if (text is null)
                continue;
            var at = Math.Max(0, item.PlanStart - bar);
            if (at < nowSec && item.PlanStart >= nowSec && nowSec > 0)
                at = nowSec; // queued less than a bar ahead: say it now
            calls.Add(new TimedCall(at, text, LengthSec: bar / plan.TempoFactor));
        }
    }

    /// <summary>PB-71: count-ins and click bars count aloud: "one, two" in the earlier bars, every beat of the last.</summary>
    private static void AddCounts(SongPlan plan, List<TimedCall> calls)
    {
        var grid = plan.SongGrid;
        var bpb = Math.Max(1, grid.BeatsPerBar);
        foreach (var item in plan.Items.Where(i => i.HasClick && i.SpokenCount && i.Length > 0))
        {
            var bars = Math.Max(1, item.LengthBars);
            for (var b = 0; b < bars; b++)
            {
                var barStart = item.PlanStart + b * grid.BarLength;
                if (b == bars - 1)
                {
                    for (var beat = 0; beat < bpb; beat++)
                        calls.Add(Count(barStart + beat * grid.BeatLength, beat + 1, grid.BeatLength / plan.TempoFactor));
                }
                else
                {
                    calls.Add(Count(barStart, 1, grid.BeatLength / plan.TempoFactor));
                    if (bpb >= 4 && bpb % 2 == 0)
                        calls.Add(Count(barStart + bpb / 2 * grid.BeatLength, 2, grid.BeatLength / plan.TempoFactor));
                }
            }
        }

        static TimedCall Count(double at, int n, double length) =>
            new(at, VoiceText.Spoken(n.ToString(CultureInfo.InvariantCulture)), LengthSec: length);
    }

    /// <summary>
    /// AUTO-30: at each boundary, the guide's call for the section that follows as written (in the last 2 bars of the
    /// outgoing item) is silenced when another item follows, and the guide's own call for that item, or its spoken name,
    /// plays in its place. Click, pad and hold items replace it with nothing; Guide call Off silences it; Custom speaks the text.
    /// </summary>
    private static void AddReplacements(SongPlan plan, Song song, List<TimedCall> calls, List<(double, double)> silences, List<GuideReplacement> replacements)
    {
        if (song.GuideAnalysis is not { Calls.Count: > 0 } guide || plan.Sections.Count == 0)
            return;

        var byIndex = plan.Sections.OrderBy(s => s.StartSec).ToList();
        var twoBars = 2 * plan.ChartGrid.BarLength;
        var items = plan.Items;
        for (var index = 0; index < items.Count; index++)
        {
            var outgoing = items[index];
            if (!outgoing.HasSource || outgoing.Length <= 0 || outgoing.Kind is not (PlanItemKind.Section or PlanItemKind.LeadIn))
                continue;

            var position = outgoing.Kind == PlanItemKind.LeadIn ? 0 : byIndex.FindIndex(s => s.Id == outgoing.Section?.Id) + 1;
            if ((position <= 0 && outgoing.Kind != PlanItemKind.LeadIn) || position >= byIndex.Count)
                continue;
            var written = byIndex[position];
            if (WrittenCall(guide, written) is not { } call
                || call.Start < outgoing.SrcStart - 1e-6 || call.End > outgoing.SrcEnd + CallMarginSec || call.Start < outgoing.SrcEnd - twoBars)
                continue;

            var incoming = items.Skip(index + 1).FirstOrDefault(i => i.Length > 0 || i.Kind == PlanItemKind.Hold);
            if (incoming is { Kind: PlanItemKind.Section } && incoming.Section?.Id == written.Id && incoming.GuideCall == GuideCallMode.Auto)
                continue; // the call is right

            var start = outgoing.PlanStart + (call.Start - outgoing.SrcStart);
            var end = Math.Min(outgoing.PlanEnd, outgoing.PlanStart + (call.End - outgoing.SrcStart));
            silences.Add((start - CallMarginSec, end + CallMarginSec));

            string? label = incoming switch
            {
                { Kind: PlanItemKind.Section, GuideCall: GuideCallMode.Off } => null,
                { Kind: PlanItemKind.Section, GuideCall: GuideCallMode.Custom } when !string.IsNullOrWhiteSpace(incoming.GuideCallText) => incoming.GuideCallText!.Trim(),
                { Kind: PlanItemKind.Section } => incoming.Label,
                { Kind: PlanItemKind.Tail } => "Ending",
                _ => null,
            };
            if (label is null)
                continue;

            var custom = incoming!.GuideCall == GuideCallMode.Custom;
            var own = custom ? null : OwnCall(guide, plan.Sections, incoming.Section);
            calls.Add(own is null
                ? new TimedCall(start, custom ? label : VoiceText.Spoken(label), LengthSec: Math.Max(end - start, 0) + twoBars / 2)
                : new TimedCall(start, label, own.Start, own.End - own.Start + CallMarginSec));
            replacements.Add(new GuideReplacement(start, label));
        }
    }

    private static GuideCall? WrittenCall(GuideAnalysis guide, SectionInfo section) =>
        guide.Calls.FirstOrDefault(c => c.Id == section.Section.CallId) ?? guide.Calls.FirstOrDefault(c => c.UsedForSectionId == section.Id);

    /// <summary>
    /// The guide's recorded call for a section: its own written call, else that of a section with the same name (a "Chorus"
    /// call serves every Chorus). The tail (no section) uses an Ending section's call.
    /// </summary>
    private static GuideCall? OwnCall(GuideAnalysis guide, IReadOnlyList<SectionInfo> sections, SectionInfo? target)
    {
        var candidates = target is null
            ? sections.Where(s => s.Type == SectionType.Ending)
            : sections.Where(s => s.Id == target.Id).Concat(sections.Where(s => string.Equals(s.Section.Name, target.Section.Name, StringComparison.OrdinalIgnoreCase)));
        return candidates.Select(s => WrittenCall(guide, s)).FirstOrDefault(c => c is not null);
    }
}
