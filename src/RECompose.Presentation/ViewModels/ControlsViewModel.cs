// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Controls;
using RECompose.Core.Planning;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>One action in Settings › Controls with the inputs bound to it (LIVE-51, LIVE-53).</summary>
public sealed class ControlRow : ObservableObject
{
    private bool _learning;
    private string _inputs = string.Empty;

    public ControlRow(ControlAction action, ControlsViewModel owner)
    {
        Action = action;
        LearnCommand = new RelayCommand(() => owner.BeginLearn(action));
        ClearCommand = new RelayCommand(() => owner.Clear(action));
    }

    public ControlAction Action { get; }
    public string Name => ControlDefaults.Name(Action);
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
                OnPropertyChanged(nameof(LearnLabel));
        }
    }

    public string LearnLabel => _learning ? "Press a key or a pedal…" : "Learn";
}

/// <summary>
/// Settings › Controls and › Playback (LIVE-50…55, SET-20, SET-28): the bindings, MIDI Learn, the debounce, and the jump
/// timing. The same object routes the keys and the pedal while the app runs; the dialog edits it.
/// </summary>
public sealed class ControlsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DateTime _t0 = DateTime.UtcNow;
    private List<ControlBinding> _bindings;
    private ControlRouter _router;
    private ControlAction? _learning;
    private string _lastInput = string.Empty;

    public ControlsViewModel(MainViewModel main)
    {
        _main = main;
        _bindings = [.. main.Settings.Bindings ?? ControlDefaults.PageTurner];
        _router = new ControlRouter(_bindings);
        Rows = [.. Enum.GetValues<ControlAction>().Where(a => a is not (ControlAction.PlayPause or ControlAction.TalkEnd)).Select(a => new ControlRow(a, this))];
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        Refresh();
    }

    public ObservableCollection<ControlRow> Rows { get; }
    public RelayCommand RestoreDefaultsCommand { get; }

    /// <summary>"Pedal: GO" for a moment after an input acted (LIVE-55).</summary>
    public string LastInput { get => _lastInput; private set => Set(ref _lastInput, value); }

    public IReadOnlyList<ControlBinding> Bindings => _bindings;

    /// <summary>Waiting for the next input to bind (LIVE-53).</summary>
    public bool IsLearning => _learning is not null;

    /// <summary>Seconds on a clock the router can use.</summary>
    private double Now => (DateTime.UtcNow - _t0).TotalSeconds;

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
        MidiPorts = ports;
        OnPropertyChanged(nameof(MidiPort));
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

    // ---- Routing ----------------------------------------------------------------------------

    /// <summary>A computer key went down or up. True when a binding took it.</summary>
    public bool Key(string name, bool down)
    {
        if (_learning is { } target && down)
        {
            Learn(target, ControlInput.FromKey(name));
            return true;
        }

        var actions = _router.Key(name, down, Now);
        foreach (var action in actions)
            _main.RunControl(action, ActionSource.Keys);
        return actions.Count > 0 || _router.Bindings.Any(b => b.Input.Kind == ControlKind.Key && string.Equals(b.Input.Key, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A MIDI message from the engine.</summary>
    public void Midi(MidiMessage message)
    {
        if (_learning is { } target && message.IsDown(64))
        {
            Learn(target, ControlInput.Learned(message));
            return;
        }

        foreach (var action in _router.Midi(message, Now))
        {
            LastInput = $"Pedal: {ControlDefaults.Name(action)}";
            AppLog.Write("Controls", $"{LastInput} ({new MidiBindingText(message)})");
            _main.RunControl(action, ActionSource.Pedal);
        }
    }

    /// <summary>Long presses (LIVE-54): called from the playback tick.</summary>
    public void Tick()
    {
        foreach (var action in _router.Tick(Now))
            _main.RunControl(action, ActionSource.Pedal);
    }

    private readonly record struct MidiBindingText(MidiMessage Message)
    {
        public override string ToString() => ControlInput.Learned(Message).Display;
    }

    // ---- Editing ----------------------------------------------------------------------------

    public void BeginLearn(ControlAction action)
    {
        foreach (var row in Rows)
            row.Learning = row.Action == action;
        _learning = action;
    }

    public void CancelLearn()
    {
        foreach (var row in Rows)
            row.Learning = false;
        _learning = null;
    }

    private void Learn(ControlAction action, ControlInput input)
    {
        // An input does one thing: binding it here frees it from where it was.
        _bindings.RemoveAll(b => b.Input == input);
        _bindings.Add(new ControlBinding(action, input));
        Commit();
        CancelLearn(); // last, so a row that stops learning already shows its new input
    }

    public void Clear(ControlAction action)
    {
        _bindings.RemoveAll(b => b.Action == action);
        Commit();
    }

    public void RestoreDefaults()
    {
        _bindings = [.. ControlDefaults.PageTurner];
        Commit();
    }

    private void Commit()
    {
        _router = new ControlRouter(_bindings);
        _main.UpdateSettings(s => s with { Bindings = [.. _bindings] });
        Refresh();
    }

    private void Refresh()
    {
        foreach (var row in Rows)
        {
            var inputs = _bindings.Where(b => b.Action == row.Action).Select(b => b.Input.Display).ToList();
            row.Inputs = inputs.Count == 0 ? "Not bound" : string.Join(" · ", inputs);
        }
    }
}
