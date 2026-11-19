// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Presentation.Mvvm;

namespace RECompose.Presentation.ViewModels;

/// <summary>How the pad review or editor closed.</summary>
public enum PadReviewChoice
{
    Cancel,
    Save,

    /// <summary>Import: the folder is a song after all.</summary>
    ImportAsSong,
}

/// <summary>A loop mode in the editor's picker.</summary>
public sealed record PadLoopModeChoice(PadLoopMode Mode, string Name)
{
    public static readonly IReadOnlyList<PadLoopModeChoice> All =
    [
        new(PadLoopMode.File, "The files' own, else 25 % to 90 %"),
        new(PadLoopMode.Default, "25 % to 90 % of each file"),
        new(PadLoopMode.Custom, "Custom"),
    ];

    public override string ToString() => Name;
}

/// <summary>One key of the pad review or editor: its file, a choice when two files spell it, and making it from another key.</summary>
public sealed class PadKeyRowViewModel : ObservableObject
{
    private readonly PadSetEditViewModel _owner;
    private string? _file;
    private int? _madeFrom;
    private bool _fill;

    internal PadKeyRowViewModel(PadSetEditViewModel owner, int pitchClass, IEnumerable<string> files, string? file, int? madeFrom)
    {
        _owner = owner;
        PitchClass = pitchClass;
        Label = new Key(pitchClass).ToDisplayString();
        Files = new ObservableCollection<string>(files);
        _file = file;
        _madeFrom = madeFrom;
        PlayCommand = new RelayCommand(() => _owner.PreviewAsync(_file!), () => _file is not null);
        LoopCommand = new RelayCommand(() => _owner.PreviewSeamAsync(_file!), () => _file is not null);
        ReplaceCommand = new RelayCommand(ReplaceAsync);
        ClearCommand = new RelayCommand(() => File = null, () => _file is not null);
    }

    public int PitchClass { get; }

    /// <summary>"D♭" with real accidentals.</summary>
    public string Label { get; }

    /// <summary>The files found for this key; more than one when two names spell it ("C#", "Db").</summary>
    public ObservableCollection<string> Files { get; }

    public bool HasChoices => Files.Count > 1;

    public string? File
    {
        get => _file;
        set
        {
            if (!Set(ref _file, value))
                return;
            _madeFrom = null; // a file the person chose, not one the app made
            if (value is not null)
                Fill = false;
            Changed();
            _owner.KeysChanged();
        }
    }

    public string FileName => _file is null ? "No file" : Path.GetFileName(_file);

    public bool IsMissing => _file is null;

    /// <summary>A key the app made by transposing another one: "Made from D".</summary>
    public int? MadeFrom => _madeFrom;

    public string? MadeFromText => _madeFrom is { } from ? $"Made from {new Key(from).ToDisplayString()}" : null;

    /// <summary>Make this missing key from the nearest key when saving (the person chooses, per key).</summary>
    public bool Fill
    {
        get => _fill;
        set
        {
            if (Set(ref _fill, value && CanFill))
                _owner.KeysChanged();
        }
    }

    /// <summary>"Make from D, a semitone down", or null when no key has a file of its own.</summary>
    public string? FillLabel => _owner.SourceFor(PitchClass) is { } s ? $"Make from {new Key(s.From).ToDisplayString()}, {Shift(s.Semitones)}" : null;

    public bool CanFill => IsMissing && FillLabel is not null;

    public string AccessibleName => $"{Label}: {(_file is null ? "no file" : FileName)}{(MadeFromText is { } made ? ", " + made.ToLowerInvariant() : string.Empty)}";

    public RelayCommand PlayCommand { get; }
    public RelayCommand LoopCommand { get; }
    public RelayCommand ReplaceCommand { get; }
    public RelayCommand ClearCommand { get; }

    internal void Changed()
    {
        foreach (var name in new[] { nameof(FileName), nameof(IsMissing), nameof(MadeFromText), nameof(FillLabel), nameof(CanFill), nameof(AccessibleName), nameof(HasChoices) })
            OnPropertyChanged(name);
        if (!CanFill && _fill)
        {
            _fill = false;
            OnPropertyChanged(nameof(Fill));
        }
        PlayCommand.Refresh();
        LoopCommand.Refresh();
        ClearCommand.Refresh();
    }

    private async Task ReplaceAsync()
    {
        if (await _owner.PickFileAsync($"Pad file for {Label}") is not { } file)
            return;
        if (!Files.Contains(file))
            Files.Add(file);
        File = file;
    }

    private static string Shift(int semitones) => Math.Abs(semitones) switch
    {
        1 => semitones > 0 ? "a semitone up" : "a semitone down",
        _ => $"{Math.Abs(semitones)} semitones {(semitones > 0 ? "up" : "down")}",
    };
}

/// <summary>
/// The pad set review on import and the pad set editor (SL-47, SL-46): the name, the file of each key, conflicts,
/// making missing keys from the nearest key, the loop points and the set's own volume.
/// </summary>
public sealed class PadSetEditViewModel : ObservableObject
{
    private readonly PadSet _set;
    private readonly Func<string, Task> _preview;
    private readonly Func<string, PadLoop?, Task> _previewSeam;
    private readonly Func<string, Task<string?>> _pickFile;
    private string _name;
    private string? _error;
    private string _gainText;
    private PadLoopModeChoice _loopMode;
    private string _loopStartText;
    private string _loopEndText;
    private string _crossfadeText;

    public PadSetEditViewModel(PadSet set, PadCandidate? candidate, bool isImport, string? guess, bool canImportAsSong,
        Func<string, Task> preview, Func<string, PadLoop?, Task> previewSeam, Func<string, Task<string?>> pickFile)
    {
        _set = set;
        _preview = preview;
        _previewSeam = previewSeam;
        _pickFile = pickFile;
        _name = set.Name;
        IsImport = isImport;
        Guess = guess;
        CanImportAsSong = canImportAsSong;
        Rows = new ObservableCollection<PadKeyRowViewModel>(Enumerable.Range(0, 12).Select(pc =>
        {
            var file = set.FileFor(pc);
            IEnumerable<string> files = candidate?.Keys.TryGetValue(pc, out var found) == true ? found : file is null ? [] : [file];
            return new PadKeyRowViewModel(this, pc, files, file, set.Made.TryGetValue(pc, out var from) ? from : null);
        }));
        Unkeyed = candidate is null ? [] : candidate.Unkeyed.Select(Path.GetFileName).OfType<string>().ToList();

        var loop = set.Loop;
        _loopMode = PadLoopModeChoice.All.First(c => c.Mode == (loop?.Mode ?? PadLoopMode.File));
        _loopStartText = Seconds(loop?.Mode == PadLoopMode.Custom ? loop.StartSec : 0);
        _loopEndText = loop?.Mode == PadLoopMode.Custom ? Seconds(loop.EndSec) : string.Empty;
        _crossfadeText = Seconds(loop?.CrossfadeSec ?? PadLoop.DefaultCrossfadeSec);
        _gainText = set.GainDb.ToString("0.#", CultureInfo.InvariantCulture);
        FillAllCommand = new RelayCommand(() =>
        {
            foreach (var row in Rows.Where(r => r.CanFill))
                row.Fill = true;
        }, () => Rows.Any(r => r.CanFill && !r.Fill));
    }

    /// <summary>Import (a new set from a folder) or edit (a set in the library).</summary>
    public bool IsImport { get; }

    public string Title => IsImport ? "Import pad set" : "Edit pad set";

    public string ConfirmLabel => IsImport ? "Import as pad set" : "Save";

    public string Folder => _set.Folder;

    /// <summary>Why the app took the folder for a pad set, or that it isn't sure; null in the editor.</summary>
    public string? Guess { get; }

    /// <summary>Import only: the person can import the folder as a song instead.</summary>
    public bool CanImportAsSong { get; }

    public string Name { get => _name; set => Set(ref _name, value); }

    public ObservableCollection<PadKeyRowViewModel> Rows { get; }

    public IEnumerable<PadKeyRowViewModel> FirstRows => Rows.Take(6);
    public IEnumerable<PadKeyRowViewModel> SecondRows => Rows.Skip(6);

    /// <summary>Audio files in the folder that aren't named by a key; they aren't imported.</summary>
    public IReadOnlyList<string> Unkeyed { get; }

    public string? UnkeyedNotice => Unkeyed.Count == 0 ? null
        : $"Not named by a key, so not imported: {string.Join(", ", Unkeyed.Take(4))}{(Unkeyed.Count > 4 ? $" and {Unkeyed.Count - 4} more" : string.Empty)}.";

    /// <summary>"12 keys", "11 keys · E missing", with the keys that will be made.</summary>
    public string Summary
    {
        get
        {
            var files = Rows.Count(r => !r.IsMissing);
            var fill = Rows.Count(r => r.Fill);
            var missing = Rows.Where(r => r.IsMissing && !r.Fill).Select(r => r.Label).ToList();
            var parts = new List<string> { $"{files} {(files == 1 ? "key" : "keys")}" };
            if (fill > 0)
                parts.Add($"{fill} to make");
            if (missing.Count > 0)
                parts.Add(missing.Count <= 3 ? $"{string.Join(", ", missing)} silent" : $"{missing.Count} silent");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Keys with two files to choose from.</summary>
    public string? ConflictNotice
    {
        get
        {
            var keys = Rows.Where(r => r.HasChoices).Select(r => r.Label).ToList();
            return keys.Count == 0 ? null : $"Two files name {string.Join(", ", keys)}. Choose the one to use.";
        }
    }

    public RelayCommand FillAllCommand { get; }

    public IReadOnlyList<PadLoopModeChoice> LoopModes => PadLoopModeChoice.All;

    public PadLoopModeChoice LoopMode
    {
        get => _loopMode;
        set
        {
            if (Set(ref _loopMode, value))
                OnPropertyChanged(nameof(IsCustomLoop));
        }
    }

    public bool IsCustomLoop => _loopMode.Mode == PadLoopMode.Custom;

    public string LoopStartText { get => _loopStartText; set => Set(ref _loopStartText, value); }
    public string LoopEndText { get => _loopEndText; set => Set(ref _loopEndText, value); }
    public string CrossfadeText { get => _crossfadeText; set => Set(ref _crossfadeText, value); }

    /// <summary>The set's own volume in dB, −20 to +6, added to the pad volume.</summary>
    public string GainText { get => _gainText; set => Set(ref _gainText, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>The nearest key with a file of its own to make <paramref name="pitchClass"/> from.</summary>
    internal (int From, int Semitones)? SourceFor(int pitchClass) =>
        PadImport.NearestSource(Rows?.Where(r => r.File is not null && r.MadeFrom is null).Select(r => r.PitchClass) ?? [], pitchClass);

    internal void KeysChanged()
    {
        foreach (var row in Rows ?? [])
            row.Changed();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ConflictNotice));
        FillAllCommand?.Refresh();
    }

    internal Task PreviewAsync(string file) => _preview(file);

    internal Task PreviewSeamAsync(string file) => TryGetLoop(out var loop) ? _previewSeam(file, loop) : Task.CompletedTask;

    internal Task<string?> PickFileAsync(string title) => _pickFile(title);

    /// <summary>The edited set and the keys to make, or false with <see cref="Error"/> set.</summary>
    public bool TryGetResult(out PadSet set, out IReadOnlyList<int> fill)
    {
        set = _set;
        fill = [];
        if (string.IsNullOrWhiteSpace(Name))
            return Fail("Give the pad set a name.");
        if (Rows.All(r => r.IsMissing))
            return Fail("Choose a file for at least one key.");
        if (!double.TryParse(GainText, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) || gain is < -20 or > 6)
            return Fail("The volume is a number of dB from −20 to +6.");
        if (!TryGetLoop(out var loop))
            return false;

        set = _set with
        {
            Name = Name.Trim(),
            Samples = Rows.Where(r => r.File is not null).ToDictionary(r => r.PitchClass, r => r.File!),
            Made = Rows.Where(r => r.File is not null && r.MadeFrom is not null).ToDictionary(r => r.PitchClass, r => r.MadeFrom!.Value),
            GainDb = Math.Round(gain, 1),
            Loop = loop,
        };
        fill = Rows.Where(r => r.Fill).Select(r => r.PitchClass).ToList();
        Error = null;
        return true;
    }

    private bool TryGetLoop(out PadLoop? loop)
    {
        loop = null;
        if (!TryParseSeconds(CrossfadeText, out var crossfade) || crossfade > 30)
            return Fail("The crossfade is a number of seconds from 0 to 30.");
        if (_loopMode.Mode == PadLoopMode.Custom)
        {
            if (!TryParseSeconds(LoopStartText, out var start) || !TryParseSeconds(LoopEndText, out var end) || end <= start)
                return Fail("The loop starts and ends at a number of seconds, the end after the start.");
            loop = new PadLoop { Mode = PadLoopMode.Custom, StartSec = start, EndSec = end, CrossfadeSec = crossfade };
        }
        else if (_loopMode.Mode != PadLoopMode.File || Math.Abs(crossfade - PadLoop.DefaultCrossfadeSec) > 1e-9)
            loop = new PadLoop { Mode = _loopMode.Mode, CrossfadeSec = crossfade };

        Error = null;
        return true;
    }

    private bool Fail(string error)
    {
        Error = error;
        return false;
    }

    private static bool TryParseSeconds(string? text, out double seconds) =>
        double.TryParse((text ?? string.Empty).Trim().TrimEnd('s').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;

    private static string Seconds(double sec) => sec.ToString("0.##", CultureInfo.InvariantCulture);
}
