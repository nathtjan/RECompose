// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Library;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>A pad set in Library › Pads (SL-47): "OB Pad · 12 keys".</summary>
public sealed record PadSetItem(string Id, string Name, string MetaLine);

// Pad sets (SL-47, DM-60): a folder of twelve key-named files imports as a pad set and lists under Library › Pads,
// not as a song. Playing pads arrives with the pad player in the engine (SL-40…46, PB-72).
public sealed partial class MainViewModel
{
    private PadLibrary _pads = new();
    private bool _showPads;

    public ObservableCollection<PadSetItem> PadSets { get; } = [];

    /// <summary>The library's Songs | Pads switch (UI-12).</summary>
    public bool ShowPads
    {
        get => _showPads;
        set
        {
            if (Set(ref _showPads, value))
                OnPropertyChanged(nameof(ShowSongs));
        }
    }

    public bool ShowSongs => !_showPads;

    /// <summary>The switch's keys: pressing one selects it; pressing the selected one again changes nothing.</summary>
    public bool SongsKey { get => !_showPads; set { if (value) ShowPads = false; OnPropertyChanged(); OnPropertyChanged(nameof(PadsKey)); } }

    public bool PadsKey { get => _showPads; set { if (value) ShowPads = true; OnPropertyChanged(); OnPropertyChanged(nameof(SongsKey)); } }

    public bool PadsEmpty => PadSets.Count == 0;

    private void LoadPads()
    {
        _pads = _store.LoadPads();
        RefreshPads();
    }

    private void RefreshPads()
    {
        PadSets.Clear();
        foreach (var set in _pads.PadSets.OrderBy(p => p.Name, Core.Import.NaturalComparer.Instance))
            PadSets.Add(new PadSetItem(set.Id, set.Name, set.IsComplete ? "12 keys" : $"{set.Samples.Count} keys"));
        OnPropertyChanged(nameof(PadsEmpty));
    }

    /// <summary>
    /// SL-47: imports <paramref name="folder"/> as a pad set when its files are named by key and cover all twelve pitch
    /// classes. Returns false when the folder is a song.
    /// </summary>
    private bool TryImportPadSet(string folder, IReadOnlyList<string> files)
    {
        if (PadSetDetector.Detect(folder, files) is not { } set)
            return false;

        var full = Path.GetFullPath(folder);
        if (_pads.PadSets.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Folder), full, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            PadsKey = true;
            ShowToast($"{existing.Name} is already in Library › Pads.");
            return true;
        }

        _pads = _pads with { PadSets = [.. _pads.PadSets, set], CurrentId = _pads.CurrentId ?? set.Id };
        _store.SavePads(_pads);
        RefreshPads();
        PadsKey = true;
        ShowToast($"Imported the pad set {set.Name} · 12 keys");
        AppLog.Write("Library", $"Imported pad set {set.Name} from {folder}");
        return true;
    }
}
