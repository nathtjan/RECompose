// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using RECompose.Core.Import;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Timing;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>The choices offered for a track's output (PB-85).</summary>
public static class OutputChoices
{
    /// <summary>"Main", "Cue", then pairs and single channels up to the device's count.</summary>
    public static IReadOnlyList<string> For(int deviceChannels)
    {
        var count = Math.Clamp(deviceChannels, 2, 16);
        var list = new List<string> { "Main", "Cue" };
        for (var c = 0; c + 1 < count; c += 2)
            list.Add(OutputRef.Direct(c, 2).Label);
        for (var c = 0; c < count; c++)
            list.Add(OutputRef.Direct(c, 1).Label);
        return list;
    }

    public static string ToOutput(string label)
    {
        if (label == "Main") return "main";
        if (label == "Cue") return "cue";
        var parts = label.Split('–');
        return parts.Length == 2 ? $"{parts[0]}-{parts[1]}" : label;
    }

    public static string ToLabel(string output) => OutputRef.Parse(output).Label;
}

/// <summary>One file row in the import review (SL-51).</summary>
public sealed class ImportRowViewModel : ObservableObject
{
    public static IReadOnlyList<string> Roles { get; } = ["Click", "Guide", "Spoken guide", "Music"];
    public static IReadOnlyList<string> Groups { get; } = TrackGroups.Music;

    private string _role;
    private string _group;
    private string _output;

    public ImportRowViewModel(ImportCandidate candidate, IReadOnlyList<string> outputOptions)
    {
        Candidate = candidate;
        OutputOptions = outputOptions;
        _role = RoleName(candidate.Detected.Role);
        _group = candidate.Detected.Role == TrackRole.Music ? candidate.Detected.Group : TrackGroups.Other;
        _output = OutputChoices.ToLabel(candidate.Detected.DefaultOutput);
    }

    public ImportCandidate Candidate { get; }
    public IReadOnlyList<string> OutputOptions { get; }
    public string FileName => Path.GetFileName(Candidate.File);
    public bool Ok => Candidate.Info.Ok;
    public bool Recognised => Candidate.Detected.Recognised;

    /// <summary>"Stereo · 48 kHz · 4:13", or the problem.</summary>
    public string Detail => Ok
        ? $"{(Candidate.Info.Channels == 1 ? "Mono" : Candidate.Info.Channels == 2 ? "Stereo" : $"{Candidate.Info.Channels} ch")} · {Candidate.Info.SampleRate / 1000:0.#} kHz · {Format.Clock(Candidate.Info.DurationSec)}"
        : Candidate.Info.Problem ?? "Can't read this file";

    public string Role
    {
        get => _role;
        set
        {
            if (Set(ref _role, value))
            {
                // Click and guide go to Cue; music to Main (PB-85), unless set by hand later.
                Output = value is "Music" ? "Main" : "Cue";
                OnPropertyChanged(nameof(IsMusic));
            }
        }
    }

    public bool IsMusic => _role == "Music";

    public string Group
    {
        get => _group;
        set => Set(ref _group, value);
    }

    public string Output
    {
        get => _output;
        set => Set(ref _output, value);
    }

    public TrackRole RoleValue => RoleOf(_role);

    public static TrackRole RoleOf(string name) => name switch
    {
        "Click" => TrackRole.Click,
        "Guide" => TrackRole.Guide,
        "Spoken guide" => TrackRole.SpokenGuide,
        _ => TrackRole.Music,
    };

    public static string RoleName(TrackRole role) => role switch
    {
        TrackRole.Click => "Click",
        TrackRole.Guide => "Guide",
        TrackRole.SpokenGuide => "Spoken guide",
        _ => "Music",
    };
}

/// <summary>The import review dialog (SL-51): title, key, BPM, meter and one row per file.</summary>
public sealed class ImportReviewViewModel : ObservableObject
{
    private string _title;
    private string _keyText;
    private string _bpmText;
    private string _meterText;
    private string? _error;

    public ImportReviewViewModel(ImportScan scan, int deviceChannels)
    {
        Scan = scan;
        _title = scan.Title;
        _keyText = scan.Key is { } k && Key.TryParse(k, out var key) ? key.ToDisplayString() : string.Empty;
        _bpmText = scan.Bpm is { } b ? Format.Bpm(b) : string.Empty;
        _meterText = scan.Meter is { } m ? $"{m.BeatsPerBar}/{m.BeatUnit}" : "4/4";
        var options = OutputChoices.For(deviceChannels);
        Rows = new ObservableCollection<ImportRowViewModel>(scan.Files.Select(f => new ImportRowViewModel(f, options)));
    }

    public ImportScan Scan { get; }
    public ObservableCollection<ImportRowViewModel> Rows { get; }
    public string Folder => Scan.Folder;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string KeyText { get => _keyText; set => Set(ref _keyText, value); }
    public string BpmText { get => _bpmText; set => Set(ref _bpmText, value); }
    public string MeterText { get => _meterText; set => Set(ref _meterText, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>"16 files · 15 recognised · 1 can't be read".</summary>
    public string Summary
    {
        get
        {
            var unreadable = Rows.Count(r => !r.Ok);
            var recognised = Rows.Count(r => r.Ok && r.Recognised);
            var text = $"{Rows.Count} files · {recognised} recognised";
            return unreadable > 0 ? $"{text} · {unreadable} can't be read" : text;
        }
    }

    public string? DuplicateNotice => Scan.Duplicate is { } d ? $"Already in the library as {d.Title}." : null;

    public string? TempoNotice => string.IsNullOrWhiteSpace(BpmText)
        ? "No BPM in the file names. Type it, or 120 BPM is assumed until click analysis arrives."
        : null;

    /// <summary>Checks the fields. Returns false and sets <see cref="Error"/> if something can't be used.</summary>
    public bool TryGetValues(out double? bpm, out string? key, out int beatsPerBar, out int beatUnit)
    {
        bpm = null;
        key = null;
        beatsPerBar = 4;
        beatUnit = 4;

        if (!string.IsNullOrWhiteSpace(BpmText))
        {
            if (!double.TryParse(BpmText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b < 20 || b > 400)
            {
                Error = "BPM must be a number between 20 and 400.";
                return false;
            }
            bpm = b;
        }

        if (!string.IsNullOrWhiteSpace(KeyText))
        {
            if (!Key.TryParse(KeyText, out var k))
            {
                Error = $"\"{KeyText}\" isn't a key. Use a name such as B♭, F♯m or E.";
                return false;
            }
            key = k.ToString();
        }

        if (ImportRules.ParseMeter(MeterText) is { } meter)
        {
            (beatsPerBar, beatUnit) = meter;
        }
        else if (!string.IsNullOrWhiteSpace(MeterText))
        {
            Error = "Meter must look like 4/4, 3/4 or 6/8.";
            return false;
        }

        if (!Rows.Any(r => r.Ok))
        {
            Error = "None of these files can be read.";
            return false;
        }

        Error = null;
        return true;
    }

    public IEnumerable<(ImportCandidate File, TrackRole Role, string Group, string Output)> Choices() =>
        Rows.Select(r => (r.Candidate, r.RoleValue, r.Group, OutputChoices.ToOutput(r.Output)));
}
