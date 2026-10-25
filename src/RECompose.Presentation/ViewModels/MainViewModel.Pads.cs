// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Library;
using RECompose.Core.Settings;
using RECompose.Engine;
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

    /// <summary>The compact pads of the sidebar (UI-12, SL-40…46).</summary>
    public PadsViewModel Pads { get; }

    public string? CurrentPadSetId => _pads.Current?.Id;

    /// <summary>Runs an engine request, ignoring a lost connection (pads must never stop the app, REL-50).</summary>
    internal async Task SendToEngineAsync(Func<EngineClient, Task> request)
    {
        if (_engine is not { } engine)
            return;
        try
        {
            await request(engine);
        }
        catch (Exception ex) when (ex is EngineErrorException or EngineDisconnectedException or OperationCanceledException)
        {
            AppLog.Error("Pads", ex);
        }
    }

    internal void SavePadSettings(PadSettings pads)
    {
        _settings = _settings with { Pads = pads };
        _store.SaveSettings(_settings);
    }

    /// <summary>SL-41: another pad set; a sounding pad crossfades to the same key of the new set.</summary>
    internal async Task SelectPadSetAsync(string id)
    {
        if (_pads.PadSets.All(p => p.Id != id))
            return;
        _pads = _pads with { CurrentId = id };
        _store.SavePads(_pads);
        Pads.Refresh();
        await SendPadSetAsync();
    }

    /// <summary>Opens the current pad set in the engine and sends the pad settings (SL-45, PB-85).</summary>
    private async Task SendPadSetAsync()
    {
        var pads = _settings.Pads ?? new PadSettings();
        await SendToEngineAsync(async e =>
        {
            await e.SetPadAsync(pads.GainDb <= -40 ? -100 : pads.GainDb, pads.FadeSec, pads.Follow, _settings.Audio.ResolvePad());
            if (_pads.Current is { } set)
            {
                var missing = await e.LoadPadSetAsync(set.Id, set.Samples);
                if (missing.Count > 0)
                    _ui.Post(() => ShowToast($"{set.Name}: {missing.Count} pad files are missing"));
            }
        });
    }

    private void ShowEnginePad(int? key) => Pads.ShowPlaying(key);

    private void LoadPads()
    {
        _pads = _store.LoadPads();
        RefreshPads();
        Pads.Refresh();
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
        Pads.Refresh();
        PadsKey = true;
        if (_pads.CurrentId == set.Id)
            _ = SendPadSetAsync();
        ShowToast($"Imported the pad set {set.Name} · 12 keys");
        AppLog.Write("Library", $"Imported pad set {set.Name} from {folder}");
        return true;
    }
}
