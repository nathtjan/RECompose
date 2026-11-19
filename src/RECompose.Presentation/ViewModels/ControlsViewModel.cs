// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Controls;
using RECompose.Core.Library;
using RECompose.Core.Sections;
using RECompose.Core.Planning;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>One action (with its target: a section type, a fader, a pad key) in Settings › Controls with its inputs (LIVE-51, LIVE-53).</summary>
public sealed class ControlRow : ObservableObject
{
    private readonly ControlsViewModel _owner;
    private bool _learning;
    private string _inputs = string.Empty;
    private string _learnLabel = "Learn";
    private bool _option;

    public ControlRow(ControlAction action, ControlsViewModel owner, string? target = null, string header = "", bool more = false)
    {
        _owner = owner;
        Action = action;
        Target = target;
        Header = header;
        IsMore = more;
        _option = action is ControlAction.Fader; // soft takeover on, hold and Perform only off
        LearnCommand = new RelayCommand(() => owner.BeginLearn(this));
        ClearCommand = new RelayCommand(() => owner.Clear(this));
    }

    public ControlAction Action { get; }
    public string? Target { get; }
    public string Name => ControlDefaults.Name(Action, Target);

    /// <summary>"Faders" above the first row of a group; empty otherwise.</summary>
    public string Header { get; }
    public bool HasHeader => Header.Length > 0;

    /// <summary>Shown only with More (UI: the common rows first).</summary>
    public bool IsMore { get; }

    public RelayCommand LearnCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>"PageDown · Right", "CC 64 ch 1 ≥ 64", or "Not bound".</summary>
    public string Inputs { get => _inputs; set => Set(ref _inputs, value); }

    public bool Learning
    {
        get => _learning;
        set
        {
            if (Set(ref _learning, value))
                LearnLabel = value ? LearnPrompt(false) : "Learn";
        }
    }

    public string LearnLabel { get => _learnLabel; set => Set(ref _learnLabel, value); }

    internal string LearnPrompt(bool second) => (Action, Target) switch
    {
        (ControlAction.PadKey, null) => second ? "Press the highest note…" : "Press the lowest note…",
        (ControlAction.Fader, _) => "Move a fader or knob…",
        _ => "Press a key or a pedal…",
    };

    /// <summary>"Hold", "Perform only", "Soft takeover", or empty when the row has no option.</summary>
    public string OptionLabel => Action switch
    {
        ControlAction.PadKey => "Hold",
        ControlAction.JumpToSection or ControlAction.JumpToTile => "Perform only",
        ControlAction.Fader => "Soft takeover",
        _ => string.Empty,
    };

    public bool HasOption => OptionLabel.Length > 0;

    /// <summary>The option of this row's bindings; new bindings take it too.</summary>
    public bool Option
    {
        get => _option;
        set
        {
            if (Set(ref _option, value))
                _owner.SetOption(this, value);
        }
    }

    internal void ShowOption(bool value) => Set(ref _option, value, nameof(Option));

    internal bool Is(ControlBinding b) => b.Action == Action && b.Target == Target;

    internal ControlBinding With(ControlBinding b, bool option) => Action switch
    {
        ControlAction.PadKey => b with { Hold = option },
        ControlAction.JumpToSection or ControlAction.JumpToTile => b with { PerformOnly = option },
        ControlAction.Fader => b with { SoftTakeover = option },
        _ => b,
    };

    internal bool OptionOf(ControlBinding b) => Action switch
    {
        ControlAction.PadKey => b.Hold,
        ControlAction.JumpToSection or ControlAction.JumpToTile => b.PerformOnly,
        ControlAction.Fader => b.SoftTakeover,
        _ => false,
    };
}

/// <summary>
/// Settings › Controls and › Playback (LIVE-50…55, SET-20, SET-28): the bindings, MIDI Learn, the debounce, and the jump
/// timing. The same object routes the keys and the pedal while the app runs; the dialog edits it.
/// </summary>
public sealed class ControlsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DateTime _t0 = DateTime.UtcNow;
    private readonly List<ControlProfile> _profiles;
    private List<ControlBinding> _global;
    private string _active;
    private ControlRouter _router;
    private List<ControlRow> _allRows = [];
    private ControlRow? _learning;
    private MidiMessage? _rangeLow;
    private string _lastInput = string.Empty;
    private string _overlap = string.Empty;
    private bool _showMore;
    private readonly Dictionary<(ControlKind Kind, int Channel, int Number), int> _sent = []; // feedback last sent
    private double _lastFeedback;

    public ControlsViewModel(MainViewModel main)
    {
        _main = main;
        var settings = main.Settings;
        (_profiles, _global) = ControlProfiles.From(settings.Bindings, settings.ControlProfiles, settings.GlobalBindings);
        _active = _profiles.Any(p => p.Name == settings.ActiveControlProfile) ? settings.ActiveControlProfile! : _profiles[0].Name;
        _router = new ControlRouter(Bindings);
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        NewProfileCommand = new RelayCommand(NewProfileAsync);
        RenameProfileCommand = new RelayCommand(RenameProfileAsync);
        DeleteProfileCommand = new RelayCommand(DeleteProfileAsync, () => _profiles.Count > 1);
        BuildRows();
    }

    public ObservableCollection<ControlRow> Rows { get; } = [];
    public RelayCommand RestoreDefaultsCommand { get; }

    /// <summary>"Pedal: GO" for a moment after an input acted (LIVE-55).</summary>
    public string LastInput { get => _lastInput; private set => Set(ref _lastInput, value); }

    /// <summary>The warning after Learn when the new input is shared with another binding; empty when it is not.</summary>
    public string Overlap { get => _overlap; private set => Set(ref _overlap, value); }

    /// <summary>The bindings in force: the global layer and the active profile's.</summary>
    public IReadOnlyList<ControlBinding> Bindings => [.. _global, .. Active.Bindings];

    /// <summary>Waiting for the next input to bind (LIVE-53).</summary>
    public bool IsLearning => _learning is not null;

    /// <summary>Shows the less common rows: the other section types, tiles, single pad keys, the other faders, each profile.</summary>
    public bool ShowMore
    {
        get => _showMore;
        set
        {
            if (Set(ref _showMore, value))
                FillRows();
        }
    }

    /// <summary>Seconds on a clock the router can use.</summary>
    private double Now => (DateTime.UtcNow - _t0).TotalSeconds;

    private ControlProfile Active => _profiles.First(p => p.Name == _active);

    private List<ControlBinding> Layer(ControlAction action) => ControlProfiles.IsGlobal(action) ? _global : Active.Bindings;

    // ---- Profiles ----------------------------------------------------------------------------

    public IReadOnlyList<string> ProfileNames => [.. _profiles.Select(p => p.Name)];

    public string ActiveProfile
    {
        get => _active;
        set
        {
            if (value is not null)
                SelectProfile(value);
        }
    }

    public RelayCommand NewProfileCommand { get; }
    public RelayCommand RenameProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }

    /// <summary>Puts a profile in force, from Settings or a MIDI button.</summary>
    public void SelectProfile(string name)
    {
        if (name == _active || _profiles.All(p => p.Name != name))
            return;
        CancelLearn();
        _active = name;
        AppLog.Write("Controls", $"Profile {name}");
        _main.ShowToast($"Controls: {name}");
        Commit();
        OnPropertyChanged(nameof(ActiveProfile));
    }

    private async Task NewProfileAsync()
    {
        if (await _main.Dialogs.AskNameAsync("New control profile", "Name", "Band", "Create") is not { } name || name.Trim().Length == 0)
            return;
        name = Core.Arrangements.ArrangementEdit.UniqueName(name.Trim(), _profiles.Select(p => p.Name));
        _profiles.Add(new ControlProfile(name, [.. Active.Bindings])); // starts as a copy of the one in force
        ProfilesChanged();
        SelectProfile(name);
    }

    private async Task RenameProfileAsync()
    {
        if (await _main.Dialogs.AskNameAsync("Rename control profile", "Name", _active, "Rename") is not { } name || name.Trim().Length == 0 || name.Trim() == _active)
            return;
        name = Core.Arrangements.ArrangementEdit.UniqueName(name.Trim(), _profiles.Where(p => p.Name != _active).Select(p => p.Name));
        var index = _profiles.IndexOf(Active);
        _profiles[index] = _profiles[index] with { Name = name };
        foreach (var i in Enumerable.Range(0, _global.Count).Where(i => _global[i].Action == ControlAction.SelectProfile && _global[i].Target == _active))
            _global[i] = _global[i] with { Target = name };
        _active = name;
        ProfilesChanged();
        Commit();
    }

    private async Task DeleteProfileAsync()
    {
        if (_profiles.Count <= 1 || !await _main.Dialogs.ConfirmAsync("Delete control profile", $"Delete the control profile {_active}?", "Delete"))
            return;
        var gone = _active;
        _profiles.Remove(Active);
        _global.RemoveAll(b => b.Action == ControlAction.SelectProfile && b.Target == gone);
        _active = _profiles[0].Name;
        ProfilesChanged();
        Commit();
    }

    private void ProfilesChanged()
    {
        OnPropertyChanged(nameof(ProfileNames));
        OnPropertyChanged(nameof(ActiveProfile));
        DeleteProfileCommand.Refresh();
        BuildRows();
    }

    // ---- Playback (SET-20) ------------------------------------------------------------------

    public IReadOnlyList<TimingChoice> TimingChoices => _main.TimingChoices;

    public TimingChoice SelectedTiming
    {
        get => _main.SelectedTiming;
        set
        {
            if (value is not null)
            {
                _main.SelectedTiming = value;
                OnPropertyChanged();
            }
        }
    }

    public bool ContinueAsWritten
    {
        get => _main.CurrentSettings.ContinueAsWritten;
        set
        {
            if (_main.CurrentSettings.ContinueAsWritten != value)
            {
                _main.UpdateSettings(s => s with { ContinueAsWritten = value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Settings › Playback "Replace guide calls" (AUTO-30).</summary>
    public bool ReplaceGuideCalls
    {
        get => _main.CurrentSettings.ReplaceGuideCalls;
        set
        {
            if (_main.CurrentSettings.ReplaceGuideCalls != value)
            {
                _main.UpdateSettings(s => s with { ReplaceGuideCalls = value });
                OnPropertyChanged();
            }
        }
    }

    // ---- MIDI out (SET-30) --------------------------------------------------------------------

    private IReadOnlyList<string> _midiPorts = ["None"];

    /// <summary>"None", then the outputs the engine can open; filled when the dialog opens.</summary>
    public IReadOnlyList<string> MidiPorts { get => _midiPorts; private set => Set(ref _midiPorts, value); }

    public static IReadOnlyList<string> Channels { get; } = Enumerable.Range(1, 16).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
    public static IReadOnlyList<string> MiddleCChoices { get; } = ["C3", "C4"];

    internal async Task LoadMidiPortsAsync()
    {
        var ports = await _main.MidiOutputsAsync();
        if (_main.MidiOut.Port is { } port && !ports.Contains(port))
            ports = [.. ports, port]; // chosen before, not connected now
        if (_main.CurrentSettings.ControlOutput is { } control && !ports.Contains(control))
            ports = [.. ports, control];
        MidiPorts = ports;
        OnPropertyChanged(nameof(MidiPort));
        OnPropertyChanged(nameof(ControlOutput));
    }

    public string MidiPort
    {
        get => _main.MidiOut.Port ?? "None";
        set
        {
            var port = value is null or "None" ? null : value;
            if (port != _main.MidiOut.Port)
            {
                _main.UpdateSettings(s => s with { MidiOut = _main.MidiOut with { Port = port } });
                _main.ReopenMidiOut();
                OnPropertyChanged();
            }
        }
    }

    public string MidiChannel
    {
        get => _main.MidiOut.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, out var channel) && channel != _main.MidiOut.Channel)
            {
                _main.UpdateSettings(s => s with { MidiOut = _main.MidiOut with { Channel = channel } });
                OnPropertyChanged();
            }
        }
    }

    public string MiddleC
    {
        get => _main.MidiOut.MiddleC;
        set
        {
            if (value is "C3" or "C4" && value != _main.MidiOut.MiddleC)
            {
                _main.UpdateSettings(s => s with { MidiOut = _main.MidiOut with { MiddleC = value } });
                OnPropertyChanged();
            }
        }
    }

    public RelayCommand TestNoteCommand => _main.TestNoteCommand;

    // ---- Controller feedback ---------------------------------------------------------------------

    private string _controlOutputStatus = string.Empty;

    /// <summary>The controller's output for lit buttons and moving faders; "None" for no feedback.</summary>
    public string ControlOutput
    {
        get => _main.CurrentSettings.ControlOutput ?? "None";
        set
        {
            var port = value is null or "None" ? null : value;
            if (port != _main.CurrentSettings.ControlOutput)
            {
                _main.UpdateSettings(s => s with { ControlOutput = port });
                _main.ReopenControlOutput();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>"Can't open nanoKONTROL2. Another app may be using it." Empty when it opened or none is chosen.</summary>
    public string ControlOutputStatus { get => _controlOutputStatus; internal set => Set(ref _controlOutputStatus, value); }

    /// <summary>Sends what changed since the last call: a lit button for the section, tile, pad and profile in force, a fader's level.</summary>
    internal void Feedback(bool all = false)
    {
        if (all)
            _sent.Clear();
        if (!_main.HasControlOutput)
            return;
        var messages = new List<byte[]>();
        foreach (var b in Bindings)
        {
            if (b.Input.Kind is not (ControlKind.Note or ControlKind.ControlChange) || b.Input.NumberTo is { } to && to != b.Input.Number)
                continue; // a key range is a keyboard: lighting it would play notes
            if (FeedbackValue(b) is not { } value)
                continue;
            var key = (b.Input.Kind, Math.Max(b.Input.Channel, 1) - 1, b.Input.Number);
            if (_sent.TryGetValue(key, out var last) && last == value)
                continue;
            _sent[key] = value;
            messages.Add([(byte)((b.Input.Kind == ControlKind.Note ? 0x90 : 0xB0) | key.Item2), (byte)b.Input.Number, (byte)value]);
        }

        if (messages.Count > 0)
            _main.SendControlFeedback(messages);
    }

    private int? FeedbackValue(ControlBinding b)
    {
        static int Lit(bool on) => on ? 127 : 0;
        return b.Action switch
        {
            ControlAction.JumpToSection => Lit(_main.CurrentSectionType?.ToString() == b.Target),
            ControlAction.JumpToTile => Lit(_main.IsTileCurrent(b.Target)),
            ControlAction.PadKey when b.Target is not null => Lit(_main.Pads.PlayingKey?.ToString(System.Globalization.CultureInfo.InvariantCulture) == b.Target),
            ControlAction.PadPlayStop => Lit(_main.Pads.IsPlaying),
            ControlAction.SelectProfile => Lit(b.Target == _active),
            ControlAction.Fader => _main.FaderValue(b.Target),
            _ => null,
        };
    }

    // ---- Routing ----------------------------------------------------------------------------

    /// <summary>A computer key went down or up. True when a binding took it.</summary>
    public bool Key(string name, bool down)
    {
        if (_learning is { } row)
        {
            if (!down || row.Action == ControlAction.Fader || row is { Action: ControlAction.PadKey, Target: null })
                return false; // a fader or a key range needs a controller
            Learn(row, ControlInput.FromKey(name));
            return true;
        }

        var events = _router.Key(name, down, Now);
        foreach (var e in events)
            Run(e, ActionSource.Keys);
        return events.Count > 0 || _router.Bindings.Any(b => b.Input.Kind == ControlKind.Key && string.Equals(b.Input.Key, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A MIDI message from the engine.</summary>
    public void Midi(MidiMessage message)
    {
        if (_learning is { } row)
        {
            LearnFrom(row, message);
            return;
        }

        foreach (var e in _router.Midi(message, Now))
        {
            if (e.Action is not (ControlAction.Fader or ControlAction.PadKey) || e is { Action: ControlAction.PadKey, Value: > 0 })
            {
                LastInput = $"Pedal: {ControlDefaults.Name(e.Action, e.Binding?.Target)}";
                AppLog.Write("Controls", $"{LastInput} ({new MidiBindingText(message)})");
            }

            Run(e, ActionSource.Pedal);
            if (e is { Action: ControlAction.Fader, Binding: { } fader } && _main.FaderValue(fader.Target) is { } level)
                _sent[(ControlKind.ControlChange, message.Channel - 1, message.Number)] = level; // the fader is already there: no echo
        }
    }

    /// <summary>Long presses (LIVE-54) and controller feedback: called from the engine snapshot.</summary>
    public void Tick()
    {
        foreach (var e in _router.Tick(Now))
            Run(e, ActionSource.Pedal);
        if (Now - _lastFeedback >= 0.1)
        {
            _lastFeedback = Now;
            Feedback();
        }
    }

    private void Run(ControlEvent e, ActionSource source)
    {
        switch (e.Action)
        {
            case ControlAction.NextProfile:
                SelectProfile(ControlProfiles.Next(_profiles, _active));
                break;
            case ControlAction.SelectProfile:
                SelectProfile(e.Binding?.Target ?? _active);
                break;
            default:
                _main.RunControl(e, source);
                break;
        }
    }

    private readonly record struct MidiBindingText(MidiMessage Message)
    {
        public override string ToString() => ControlInput.Learned(Message).Display;
    }

    // ---- Editing ----------------------------------------------------------------------------

    /// <summary>Learn for the row with this action and no target (the transport rows).</summary>
    public void BeginLearn(ControlAction action) => BeginLearn(_allRows.First(r => r.Action == action && r.Target is null));

    public void BeginLearn(ControlRow target)
    {
        _rangeLow = null;
        foreach (var row in _allRows)
            row.Learning = ReferenceEquals(row, target);
        _learning = target;
    }

    public void CancelLearn()
    {
        foreach (var row in _allRows)
            row.Learning = false;
        _learning = null;
        _rangeLow = null;
    }

    /// <summary>
    /// A fader learns a controller; pads from a keyboard learn the lowest and then the highest note of the range (the
    /// same note twice is one note); the rest learn the first press.
    /// </summary>
    private void LearnFrom(ControlRow row, MidiMessage m)
    {
        switch (row)
        {
            case { Action: ControlAction.Fader }:
                if (m.Kind == ControlKind.ControlChange)
                    Learn(row, ControlInput.Learned(m));
                break;
            case { Action: ControlAction.PadKey, Target: null }:
                if (m.Kind != ControlKind.Note || m.Value == 0)
                    break;
                if (_rangeLow is not { } low)
                {
                    _rangeLow = m;
                    row.LearnLabel = row.LearnPrompt(true);
                    break;
                }

                var (from, to) = (Math.Min(low.Number, m.Number), Math.Max(low.Number, m.Number));
                Learn(row, new ControlInput { Kind = ControlKind.Note, Channel = m.Channel, Number = from, NumberTo = to == from ? null : to });
                break;
            default:
                if (m.IsDown(64))
                    Learn(row, ControlInput.Learned(m));
                break;
        }
    }

    private void Learn(ControlRow row, ControlInput input)
    {
        // An input does one thing in its layer: binding it here frees it from where it was.
        var layer = Layer(row.Action);
        layer.RemoveAll(b => b.Input == input);
        var binding = row.With(new ControlBinding(row.Action, input) { Target = row.Target }, row.Option);
        layer.Add(binding);
        Commit();
        Overlap = ControlProfiles.Overlap(Bindings, binding) ?? string.Empty;
        CancelLearn(); // last, so a row that stops learning already shows its new input
    }

    public void Clear(ControlAction action) => Clear(_allRows.First(r => r.Action == action && r.Target is null));

    public void Clear(ControlRow row)
    {
        Layer(row.Action).RemoveAll(row.Is);
        Overlap = string.Empty;
        Commit();
    }

    internal void SetOption(ControlRow row, bool value)
    {
        var layer = Layer(row.Action);
        for (var i = 0; i < layer.Count; i++)
        {
            if (row.Is(layer[i]))
                layer[i] = row.With(layer[i], value);
        }

        Commit();
    }

    /// <summary>The profile in force goes back to the page-turner keys, and Panic is bound again if it was cleared.</summary>
    public void RestoreDefaults()
    {
        Active.Bindings.Clear();
        Active.Bindings.AddRange(ControlDefaults.PageTurner);
        if (_global.All(b => b.Action != ControlAction.Panic))
            _global.AddRange(ControlDefaults.Global);
        Overlap = string.Empty;
        Commit();
    }

    private void Commit()
    {
        _router = new ControlRouter(Bindings);
        _main.UpdateSettings(s => s with
        {
            Bindings = null,
            ControlProfiles = [.. _profiles.Select(p => p with { Bindings = [.. p.Bindings] })],
            GlobalBindings = [.. _global],
            ActiveControlProfile = _active,
        });
        Refresh();
        Feedback(all: true);
    }

    private void BuildRows()
    {
        var rows = new List<ControlRow>();
        var first = true;
        foreach (var action in Enum.GetValues<ControlAction>().Where(a => a <= ControlAction.Talk && a != ControlAction.PlayPause))
        {
            rows.Add(new ControlRow(action, this, header: first ? "Transport" : ""));
            first = false;
        }

        SectionType[] common = [SectionType.Intro, SectionType.Verse, SectionType.PreChorus, SectionType.Chorus, SectionType.Bridge, SectionType.Ending];
        rows.AddRange(common.Select((t, i) => new ControlRow(ControlAction.JumpToSection, this, t.ToString(), i == 0 ? "Sections" : "")));
        rows.AddRange(Enum.GetValues<SectionType>().Except(common).Select(t => new ControlRow(ControlAction.JumpToSection, this, t.ToString(), more: true)));
        rows.AddRange(Enumerable.Range(1, 8).Select(n => new ControlRow(ControlAction.JumpToTile, this, n.ToString(System.Globalization.CultureInfo.InvariantCulture), more: true)));

        rows.Add(new ControlRow(ControlAction.PadKey, this, header: "Pads"));
        rows.Add(new ControlRow(ControlAction.PadPlayStop, this));
        rows.AddRange(Enumerable.Range(0, 12).Select(pc => new ControlRow(ControlAction.PadKey, this, pc.ToString(System.Globalization.CultureInfo.InvariantCulture), more: true)));

        string[] faders = ["Master", TrackGroups.Pads, TrackGroups.Drums, TrackGroups.Bass, TrackGroups.Keys, TrackGroups.ElectricGuitar, TrackGroups.AcousticGuitar, TrackGroups.Vocals];
        rows.AddRange(faders.Select((f, i) => new ControlRow(ControlAction.Fader, this, f, i == 0 ? "Faders" : "")));
        rows.AddRange(TrackGroups.LaneOrder.Except(faders).Select(f => new ControlRow(ControlAction.Fader, this, f, more: true)));

        rows.Add(new ControlRow(ControlAction.NextProfile, this, header: "Profiles"));
        rows.AddRange(_profiles.Select(p => new ControlRow(ControlAction.SelectProfile, this, p.Name, more: true)));

        _allRows = rows;
        CancelLearn();
        FillRows();
        Refresh();
    }

    private void FillRows()
    {
        Rows.Clear();
        foreach (var row in _allRows.Where(r => _showMore || !r.IsMore))
            Rows.Add(row);
    }

    private void Refresh()
    {
        var bindings = Bindings;
        foreach (var row in _allRows)
        {
            var bound = bindings.Where(row.Is).ToList();
            row.Inputs = bound.Count == 0 ? "Not bound" : string.Join(" · ", bound.Select(b => b.Display));
            if (bound.Count > 0)
                row.ShowOption(row.OptionOf(bound[0]));
        }
    }
}
