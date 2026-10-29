// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Automation;
using RECompose.Core.Library;
using RECompose.Core.Planning;
using RECompose.Core.Settings;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Automation (AUTO-1…31, PB-71): cues, section notes, the spoken guide and guide-call replacement are built with every plan
/// (<see cref="AutomationPlan"/>) and sent with it; MIDI out and the log lines the engine reports back.
/// </summary>
public sealed partial class MainViewModel
{
    private const int NextSlotIds = 1_000_000; // the next song's MIDI ids, so the log can tell the slots apart
    private readonly Dictionary<int, string> _midiLines = [];
    private readonly HashSet<string> _speaking = []; // phrases being rendered, or that this computer cannot speak
    private AutomationEvents _automation = AutomationEvents.None;
    private (SongPlan Plan, double? SwapAt)? _sent;
    private RelayCommand? _addCue;
    private RelayCommand? _testNote;

    internal MidiOutSettings MidiOut => _settings.MidiOut ?? new MidiOutSettings();

    /// <summary>The CUES tab (AUTO-6).</summary>
    public ObservableCollection<CueRowViewModel> Cues { get; } = [];

    /// <summary>C and "+ At playhead" (AUTO-1).</summary>
    public RelayCommand AddCueCommand => _addCue ??= new RelayCommand(AddCueAtPlayhead, () => _song is not null);

    /// <summary>Settings › MIDI out: Send test note (SET-30).</summary>
    public RelayCommand TestNoteCommand => _testNote ??= new RelayCommand(() => _ = SendMidiAsync([[(byte)(0x90 | (MidiOut.Channel - 1)), 60, (byte)MidiOut.Velocity]], "Test note"));

    // ---- Automatic section cues (AUTO-20) ----------------------------------------------------------

    public bool SectionNotes
    {
        get => _song?.Model.Automation.SectionNotes ?? false;
        set => SetAutomation(a => a with { SectionNotes = value }, value ? "Section notes on" : "Section notes off");
    }

    public bool SpokenGuide
    {
        get => _song?.Model.Automation.SpokenGuide ?? false;
        set => SetAutomation(a => a with { SpokenGuide = value }, value ? "Spoken guide on" : "Spoken guide off");
    }

    private void SetAutomation(Func<SongAutomation, SongAutomation> change, string description)
    {
        if (_song is not { } song)
            return;
        var after = song.Model with { Automation = change(song.Model.Automation), UpdatedAt = DateTimeOffset.UtcNow };
        if (after.Automation == song.Model.Automation)
            return;
        Commit(description, after, []);
    }

    // ---- Cues (AUTO-1…8) -----------------------------------------------------------------------------

    private void AddCueAtPlayhead()
    {
        if (_song is not { } song)
            return;
        if (song.Plan.SourceAt(_planPosition) is not { } source)
        {
            ShowToast("Cues go where the song plays, not in click or pad bars.");
            return;
        }

        var model = song.Model;
        var cue = new Cue
        {
            Id = Cue.NewId(),
            Name = Cue.NextName(model.Cues),
            SourceSec = Math.Round(source, 3),
            Action = new CueAction { Note = MidiNotes.FirstFreeNote(model.Cues, MidiOut.Notes, MidiOut.MiddleC), Channel = MidiOut.Channel },
        };
        Commit($"Add {cue.Name}", model with { Cues = [.. model.Cues, cue], UpdatedAt = DateTimeOffset.UtcNow }, []);
        ShowToast($"{cue.Name} · {cue.Action.Summary(MidiOut.MiddleC)}");
        AppLog.Write("Cues", $"Added {cue.Name} at {source:0.000} s");
    }

    internal void EditCue(string id, Func<Cue, Cue> change, string description)
    {
        if (_song?.Model is not { } model)
            return;
        var index = model.Cues.FindIndex(c => c.Id == id);
        if (index < 0)
            return;
        var after = change(model.Cues[index]);
        if (after == model.Cues[index])
            return;
        var cues = model.Cues.ToList();
        cues[index] = after;
        Commit(description, model with { Cues = cues, UpdatedAt = DateTimeOffset.UtcNow }, []);
    }

    internal void DeleteCue(string id)
    {
        if (_song?.Model is not { } model || model.Cues.FirstOrDefault(c => c.Id == id) is not { } cue)
            return;
        Commit($"Delete {cue.Name}", model with { Cues = model.Cues.Where(c => c.Id != id).ToList(), UpdatedAt = DateTimeOffset.UtcNow }, []);
    }

    /// <summary>AUTO-6 Playhead: moves the cue to the source time under the playhead.</summary>
    internal void CueToPlayhead(string id)
    {
        if (_song?.Plan.SourceAt(_planPosition) is { } source)
            EditCue(id, c => c with { SourceSec = Math.Round(source, 3) }, "Move cue");
    }

    private void RefreshCues()
    {
        var cues = _song?.Model.Cues ?? [];
        var grid = _song?.Model.Grid ?? default;
        Cues.Clear();
        foreach (var cue in cues.OrderBy(c => c.SourceSec))
            Cues.Add(new CueRowViewModel(this, cue, grid, MidiOut));
        OnPropertyChanged(nameof(SectionNotes));
        OnPropertyChanged(nameof(SpokenGuide));
        OnPropertyChanged(nameof(HasCues));
    }

    public bool HasCues => Cues.Count > 0;

    // ---- The automation with each plan ---------------------------------------------------------------

    private static IReadOnlyList<string> GuideTrackIds(Song song) =>
        song.Tracks.Where(t => t.Role == TrackRole.Guide && !t.Hidden).Select(t => t.Id).ToList();

    /// <summary>
    /// The plan's automation for the engine: MIDI with ids for the log, the calls whose clips are ready (the rest are rendered
    /// and the plan sent again), and the segments with the guide muted over replaced calls.
    /// </summary>
    private (IReadOnlyList<PlanSegment> Segments, List<TimedMidi> Midi, List<EngineCall> Calls, AutomationEvents Events) Automate(
        SongPlan plan, Song song, double nowSec, int idBase)
    {
        var events = AutomationPlan.Build(plan, song, new AutomationOptions { Midi = MidiOut, ReplaceGuideCalls = _settings.ReplaceGuideCalls, NowSec = nowSec });
        foreach (var key in _midiLines.Keys.Where(k => idBase == 0 ? k < NextSlotIds : k >= NextSlotIds).ToList())
            _midiLines.Remove(key);
        foreach (var (id, line) in events.Lines)
            _midiLines[id + idBase] = line;

        var guide = song.GuideAnalysis is { } g ? song.Tracks.FirstOrDefault(t => t.Id == g.TrackId)?.File : null;
        var calls = new List<EngineCall>();
        var missing = new List<string>();
        foreach (var call in events.Calls)
        {
            if (call.FromSec is { } from && guide is not null)
            {
                calls.Add(new EngineCall(call.AtSec, guide, from, call.LengthSec));
                continue;
            }

            var file = Clips.PathFor(call.Text);
            if (File.Exists(file))
                calls.Add(new EngineCall(call.AtSec, file, null, call.LengthSec, Normalize: true));
            else
                missing.Add(call.Text);
        }

        if (missing.Count > 0)
            _ = SpeakThenResendAsync(missing.Distinct().ToList(), song.Id);

        var midi = events.Midi.Select(m => m.Id == 0 ? m : m with { Id = m.Id + idBase }).ToList();
        return (plan.Segments(events.GuideSilences, GuideTrackIds(song)), midi, calls, events);
    }

    /// <summary>Renders the phrases a plan needs and sends the plan again with them (PB-71: clips are made once and cached).</summary>
    private async Task SpeakThenResendAsync(List<string> phrases, string songId)
    {
        lock (_speaking)
            phrases = phrases.Where(p => _speaking.Add(p)).ToList();
        if (phrases.Count == 0)
            return;
        var made = 0;
        foreach (var phrase in phrases)
        {
            if (await Clips.GetAsync(phrase).ConfigureAwait(false) is not null)
            {
                lock (_speaking)
                    _speaking.Remove(phrase);
                made++;
            }
        }

        if (made == 0)
            return; // no voice on this computer: the plan plays without its calls
        _ui.Post(() =>
        {
            if (_song?.Id != songId || _sent is not { } sent)
                return;
            // Playing: the same plan again, taking effect when the current item ends, so nothing is heard to change (ARR-90).
            var index = sent.Plan.ItemIndexAt(_planPosition);
            _ = SendPlanAsync(null, sent.Plan, IsPlaying ? sent.SwapAt ?? sent.Plan.Items[index].PlanEnd : null);
        });
    }

    /// <summary>AUTO-30: logs the guide calls a live change replaces from now on.</summary>
    private void LogReplacements(AutomationEvents previous, AutomationEvents next)
    {
        foreach (var r in next.Replacements.Where(r => r.AtSec >= _planPosition && !previous.Replacements.Contains(r)))
            AppLog.Entry("Guide", $"Guide: \"{r.Label}\" replaces the written call");
    }

    // ---- MIDI out (SET-30) and the log ---------------------------------------------------------------

    /// <summary>Opens the MIDI output the settings name (macOS: the virtual "RECompose" source when none is chosen).</summary>
    internal async Task OpenMidiOutAsync(EngineClient engine)
    {
        try
        {
            var port = MidiOut.Port;
            var open = string.IsNullOrEmpty(port) && OperatingSystem.IsMacOS()
                ? await engine.SetMidiOutputAsync("RECompose", isVirtual: true)
                : await engine.SetMidiOutputAsync(port);
            if (!string.IsNullOrEmpty(port))
                AppLog.Write("Controls", open ? $"MIDI out: {port}" : $"MIDI out: {port} is not connected");
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Controls", ex);
        }
    }

    /// <summary>The MIDI outputs for Settings › MIDI out, "None" first.</summary>
    internal async Task<IReadOnlyList<string>> MidiOutputsAsync()
    {
        try
        {
            return _engine is { } engine ? ["None", .. await engine.ListMidiOutputsAsync()] : ["None"];
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            return ["None"];
        }
    }

    private async Task SendMidiAsync(IReadOnlyList<byte[]> messages, string what)
    {
        if (_engine is not { } engine)
            return;
        try
        {
            await engine.SendMidiAsync(messages);
            // A note sent by hand is released after the note length, as the plan's are.
            await Task.Delay(Math.Max(1, MidiOut.LengthMs));
            await engine.SendMidiAsync(messages.Where(m => m.Length == 3 && (m[0] & 0xF0) == 0x90).Select(m => new byte[] { (byte)(0x80 | (m[0] & 0x0F)), m[1], 0 }).ToList());
            AppLog.Write("Controls", what);
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Controls", ex);
        }
    }

    /// <summary>AUTO-21 Resend on Play: starting or resuming inside a section sends its note, so the lights catch up.</summary>
    private void ResendSectionNote()
    {
        if (_song is not { } song || !song.Model.Automation.SectionNotes || !MidiOut.ResendOnPlay || song.Plan.IsEmpty)
            return;
        var item = song.Plan.Items[song.Plan.ItemIndexAt(_planPosition)];
        if (item.Kind != PlanItemKind.Section || item.Section is not { } section || _planPosition <= item.PlanStart + 0.01)
            return; // at a section's start the plan sends it anyway
        var note = MidiNotes.NoteFor(section.Type, MidiOut.Notes);
        _ = SendMidiAsync([[(byte)(0x90 | (Math.Clamp(MidiOut.Channel, 1, 16) - 1)), (byte)note, (byte)MidiOut.Velocity]],
            $"Section {item.Label}: MIDI note {MidiNotes.Name(note, MidiOut.MiddleC)}");
    }

    private void OnMidiSent(IReadOnlyList<MidiSent> sent) => _ui.Post(() =>
    {
        foreach (var m in sent)
        {
            if (m.Id != 0 && _midiLines.TryGetValue(m.Id, out var line))
                AppLog.Entry(line.StartsWith("Section ", StringComparison.Ordinal) ? "Sections" : "Cues", line);
        }
    });
}

/// <summary>One row of the CUES tab (AUTO-6): name, position, action, passes, snap, enabled, Playhead and delete.</summary>
public sealed class CueRowViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string _middleC;

    internal CueRowViewModel(MainViewModel main, Cue cue, TempoGrid grid, MidiOutSettings midi)
    {
        _main = main;
        Cue = cue;
        _middleC = midi.MiddleC;
        var bar = Readouts.ChartBar(grid, cue.SourceSec);
        PositionText = $"chart bar {bar} · {Format.Clock(cue.SourceSec)}";
        Warning = MidiNotes.Clash(cue, midi.Notes) is { } type ? $"Same note as {RECompose.Core.Sections.SectionTypes.DisplayName(type)} sections" : string.Empty;
        PlayheadCommand = new RelayCommand(() => main.CueToPlayhead(cue.Id));
        DeleteCommand = new RelayCommand(() => main.DeleteCue(cue.Id));
    }

    public Cue Cue { get; }
    public string PositionText { get; }
    public string Summary => Cue.Action.Summary(_middleC);

    /// <summary>AUTO-7, shown only with section notes on.</summary>
    public string Warning { get; }

    public bool HasWarning => Warning.Length > 0 && _main.SectionNotes;
    public RelayCommand PlayheadCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public static IReadOnlyList<string> Kinds { get; } = ["MIDI note", "CC", "Program", "Speak"];
    public static IReadOnlyList<string> PassChoices { get; } = ["Every pass", "First pass only", "Last pass only"];
    public static IReadOnlyList<string> SnapChoices { get; } = ["Exact", "Beat", "Bar"];

    public string Name
    {
        get => Cue.Name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                _main.EditCue(Cue.Id, c => c with { Name = value.Trim() }, "Rename cue");
        }
    }

    public string Kind
    {
        get => Kinds[(int)Cue.Action.Kind];
        set
        {
            var index = Kinds.ToList().IndexOf(value);
            if (index >= 0)
                _main.EditCue(Cue.Id, c => c with { Action = c.Action with { Kind = (CueActionKind)index } }, "Change cue action");
        }
    }

    /// <summary>The note ("C4"), the controller and value ("64 127"), the program, or the text to speak.</summary>
    public string Value
    {
        get => Cue.Action.Kind switch
        {
            CueActionKind.MidiNote => MidiNotes.Name(Cue.Action.Note, _middleC),
            CueActionKind.ControlChange => $"{Cue.Action.Controller.ToString(CultureInfo.InvariantCulture)} {Cue.Action.Value.ToString(CultureInfo.InvariantCulture)}",
            CueActionKind.ProgramChange => Cue.Action.Program.ToString(CultureInfo.InvariantCulture),
            _ => Cue.Action.Text ?? string.Empty,
        };
        set
        {
            var a = Cue.Action;
            CueAction? next = a.Kind switch
            {
                CueActionKind.MidiNote => MidiNotes.Parse(value, _middleC) is { } note ? a with { Note = note } : null,
                CueActionKind.ControlChange => Numbers(value) is [var cc, var v] ? a with { Controller = Math.Clamp(cc, 0, 127), Value = Math.Clamp(v, 0, 127) }
                    : Numbers(value) is [var only] ? a with { Controller = Math.Clamp(only, 0, 127) } : null,
                CueActionKind.ProgramChange => Numbers(value) is [var p] ? a with { Program = Math.Clamp(p, 0, 127) } : null,
                _ => a with { Text = value },
            };
            if (next is not null)
                _main.EditCue(Cue.Id, c => c with { Action = next }, "Change cue action");
            else
                OnPropertyChanged();
        }
    }

    public string Channel
    {
        get => Cue.Action.Channel.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ch) && ch is >= 1 and <= 16)
                _main.EditCue(Cue.Id, c => c with { Action = c.Action with { Channel = ch } }, "Change cue channel");
            else
                OnPropertyChanged();
        }
    }

    public string Passes
    {
        get => PassChoices[(int)Cue.Passes];
        set
        {
            var index = PassChoices.ToList().IndexOf(value);
            if (index >= 0)
                _main.EditCue(Cue.Id, c => c with { Passes = (CuePasses)index }, "Change cue passes");
        }
    }

    public string Snap
    {
        get => SnapChoices[(int)Cue.Snap];
        set
        {
            var index = SnapChoices.ToList().IndexOf(value);
            if (index >= 0)
                _main.EditCue(Cue.Id, c => c with { Snap = (CueSnap)index }, "Change cue snap");
        }
    }

    public bool Enabled
    {
        get => Cue.Enabled;
        set => _main.EditCue(Cue.Id, c => c with { Enabled = value }, value ? "Enable cue" : "Disable cue");
    }

    private static int[]? Numbers(string? text)
    {
        var parts = (text ?? string.Empty).Split([' ', ',', '='], StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return null;
            numbers.Add(n);
        }

        return numbers.Count > 0 ? numbers.ToArray() : null;
    }
}
