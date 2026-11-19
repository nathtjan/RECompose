// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Commands;
using RECompose.Core.Library;
using RECompose.Core.Music;
using RECompose.Core.Settings;
using RECompose.Engine;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>A pad set in Library › Pads (SL-47): "OB Pad · 12 keys".</summary>
public sealed record PadSetItem(string Id, string Name, string MetaLine);

// Pad sets (SL-47, DM-60): a folder of key-named files imports as a pad set and lists under Library › Pads, not as a
// song. The person reviews every import and can import either way; a set's keys, loop and volume can be edited later.
public sealed partial class MainViewModel
{
    /// <summary>Above this many pad sets Library › Pads shows a search field.</summary>
    public const int PadSearchFrom = 6;

    private PadLibrary _pads = new();
    private bool _showPads;
    private string _padSearch = string.Empty;

    /// <summary>The pad sets of Library › Pads, alphabetical, filtered by <see cref="PadSearch"/>.</summary>
    public ObservableCollection<PadSetItem> PadSets { get; } = [];

    /// <summary>Every pad set, alphabetical: the sidebar's picker.</summary>
    public ObservableCollection<PadSetItem> AllPadSets { get; } = [];

    public string PadSearch
    {
        get => _padSearch;
        set
        {
            if (Set(ref _padSearch, value ?? string.Empty))
                RefreshPads();
        }
    }

    public bool ShowPadSearch => _pads.PadSets.Count >= PadSearchFrom;

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

    public bool PadsEmpty => _pads.PadSets.Count == 0;

    /// <summary>The compact pads of the sidebar (UI-12, SL-40…46).</summary>
    public PadsViewModel Pads { get; }

    public string? CurrentPadSetId => _pads.Current?.Id;

    internal PadSet? CurrentPadSet => _pads.Current;

    /// <summary>The engine's pad gain: the pad volume plus the current set's own, silent at the bottom (SL-45).</summary>
    internal double PadGainDb(double volumeDb) => volumeDb <= -40 ? -100 : volumeDb + (_pads.Current?.GainDb ?? 0);

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
            await e.SetPadAsync(PadGainDb(pads.GainDb), pads.FadeSec, pads.Follow, _settings.Audio.ResolvePad());
            if (_pads.Current is { } set)
            {
                var missing = (await e.LoadPadSetAsync(set.Id, set.Samples, default, LoopRequest(set.Loop))).Where(set.Samples.ContainsKey).ToList();
                if (missing.Count > 0)
                    _ui.Post(() => ShowToast($"{set.Name}: {missing.Count} pad files are missing"));
            }
        });
    }

    /// <summary>Renames a pad set in Library › Pads; undo puts the old name back.</summary>
    public async Task RenamePadSetAsync(string id)
    {
        if (_pads.PadSets.FirstOrDefault(p => p.Id == id) is not { } set
            || await _dialogs.AskNameAsync("Rename pad set", "Name", set.Name, "Rename") is not { } name
            || string.IsNullOrWhiteSpace(name) || name.Trim() == set.Name)
            return;

        var before = _pads;
        var after = _pads with { PadSets = _pads.PadSets.Select(p => p.Id == id ? p with { Name = name.Trim() } : p).ToList() };
        await ApplyPadsAsync(after);
        _undo.Push(new UndoableChange($"Renamed {set.Name}", () => _ = ApplyPadsAsync(before), () => _ = ApplyPadsAsync(after)));
        AppLog.Write("Library", $"Renamed pad set {set.Name} to {name.Trim()}");
    }

    /// <summary>The engine's loop request for a set's loop points (protocol 19); null for each file's own with the 2 s crossfade.</summary>
    private static PadLoopRequest? LoopRequest(PadLoop? loop) => loop switch
    {
        null => null,
        { Mode: PadLoopMode.Custom } => new PadLoopRequest("custom", loop.StartSec, loop.EndSec, loop.CrossfadeSec),
        { Mode: PadLoopMode.Default } => new PadLoopRequest("default", 0, 0, loop.CrossfadeSec),
        _ => new PadLoopRequest("file", 0, 0, loop.CrossfadeSec),
    };

    /// <summary>Deletes a pad set from the library (its files stay on disk); undo puts it back.</summary>
    public async Task DeletePadSetAsync(string id)
    {
        if (_pads.PadSets.FirstOrDefault(p => p.Id == id) is not { } set
            || !await _dialogs.ConfirmAsync("Delete pad set", $"Delete {set.Name} from the library? Its files stay where they are.", "Delete"))
            return;

        var before = _pads;
        var after = _pads with { PadSets = _pads.PadSets.Where(p => p.Id != id).ToList(), CurrentId = _pads.CurrentId == id ? null : _pads.CurrentId };
        await ApplyPadsAsync(after);
        _undo.Push(new UndoableChange($"Deleted {set.Name}", () => _ = ApplyPadsAsync(before), () => _ = ApplyPadsAsync(after)));
        AppLog.Write("Library", $"Deleted pad set {set.Name}");
    }

    /// <summary>
    /// Saves and shows <paramref name="pads"/>; when the current set or its files, loop or volume change the engine opens
    /// it again, and with none left the pad stops.
    /// </summary>
    private async Task ApplyPadsAsync(PadLibrary pads)
    {
        var current = _pads.Current;
        _pads = pads;
        _store.SavePads(_pads);
        RefreshPads();
        Pads.Refresh();
        if (_pads.Current is null)
            await Pads.StopAsync();
        else if (!SamePlayback(_pads.Current, current))
            await SendPadSetAsync();
    }

    private static bool SamePlayback(PadSet a, PadSet? b) =>
        b is not null && a.Id == b.Id && a.Loop == b.Loop && Math.Abs(a.GainDb - b.GainDb) < 1e-9
        && a.Samples.Count == b.Samples.Count && a.Samples.All(k => b.Samples.TryGetValue(k.Key, out var f) && f == k.Value);

    private void ShowEnginePad(int? key) => Pads.ShowPlaying(key);

    private void LoadPads()
    {
        _pads = _store.LoadPads();
        RefreshPads();
        Pads.Refresh();
        _ = Task.Run(DeleteUnusedMadePads);
    }

    /// <summary>The folder of the keys the app made for a set (not a cache: the pad plays from there).</summary>
    private string MadePadsFolder(string setId) => Path.Combine(_store.MediaFolder, "pads", setId);

    /// <summary>At start, when no undo can bring them back: deletes made keys no pad set uses any more.</summary>
    private void DeleteUnusedMadePads()
    {
        var root = Path.Combine(_store.MediaFolder, "pads");
        try
        {
            if (!Directory.Exists(root))
                return;
            var used = _pads.PadSets.SelectMany(p => p.Samples.Values).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !used.Contains(Path.GetFullPath(f))))
                File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
                Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Pads", ex);
        }
    }

    private void RefreshPads()
    {
        var sets = _pads.PadSets.OrderBy(p => p.Name, Core.Import.NaturalComparer.Instance).ToList();
        AllPadSets.Clear();
        foreach (var set in sets)
            AllPadSets.Add(new PadSetItem(set.Id, set.Name, set.MetaLine));

        var search = ShowPadSearch ? _padSearch.Trim() : string.Empty;
        PadSets.Clear();
        foreach (var item in AllPadSets.Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            PadSets.Add(item);
        OnPropertyChanged(nameof(PadsEmpty));
        OnPropertyChanged(nameof(ShowPadSearch));
    }

    // ---- Import, review and edit (SL-47) ----------------------------------------------------------------------

    /// <summary>
    /// SL-47: the review of a folder the app takes for one or more pad sets. Each set is reviewed in turn; Cancel skips
    /// it. A single set can be imported as a song instead, which this returns.
    /// </summary>
    private async Task<PadReviewChoice> ReviewPadImportAsync(PadScan scan, bool canImportAsSong = true)
    {
        var imported = 0;
        for (var i = 0; i < scan.Sets.Count; i++)
        {
            var candidate = scan.Sets[i];
            var full = Path.GetFullPath(candidate.Folder);
            if (_pads.PadSets.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Folder), full, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                PadsKey = true;
                ShowToast($"{existing.Name} is already in Library › Pads.");
                continue;
            }

            var guess = scan.Guess == PadGuess.Unsure
                ? $"This may be a pad set or a song. {scan.Reason}"
                : scan.Sets.Count > 1 ? $"Pad set {i + 1} of {scan.Sets.Count}. {Covers(candidate)}" : $"This looks like a pad set. {scan.Reason}";
            var review = PadEditor(candidate.ToPadSet(), candidate, isImport: true, guess, canImportAsSong && scan.Sets.Count == 1);
            switch (await _dialogs.EditPadSetAsync(review))
            {
                case PadReviewChoice.ImportAsSong:
                    return PadReviewChoice.ImportAsSong;
                case PadReviewChoice.Save when review.TryGetResult(out var set, out var fill):
                    set = await MakePadKeysAsync(set, fill);
                    await ApplyPadsAsync(_pads with { PadSets = [.. _pads.PadSets, set], CurrentId = _pads.CurrentId ?? set.Id });
                    var added = set;
                    _undo.Push(new UndoableChange($"Imported {added.Name}",
                        () => _ = ApplyPadsAsync(_pads with { PadSets = _pads.PadSets.Where(p => p.Id != added.Id).ToList(), CurrentId = _pads.CurrentId == added.Id ? null : _pads.CurrentId }),
                        () => _ = ApplyPadsAsync(_pads with { PadSets = [.. _pads.PadSets, added], CurrentId = _pads.CurrentId ?? added.Id })));
                    PadsKey = true;
                    ShowToast($"Imported the pad set {set.Name} · {set.MetaLine}");
                    AppLog.Write("Library", $"Imported pad set {set.Name} from {set.Folder}: {set.MetaLine}");
                    imported++;
                    break;
            }
        }

        return imported > 0 ? PadReviewChoice.Save : PadReviewChoice.Cancel;

        static string Covers(PadCandidate c) => c.Keys.Count == 12 ? "All twelve keys have a file." : $"{c.Keys.Count} of the twelve keys have a file.";
    }

    /// <summary>Library › Import pad set…: a folder as a pad set whatever its names say (one set per subfolder of sets).</summary>
    public async Task ImportPadSetAsync()
    {
        if (await _dialogs.PickFolderAsync() is not { } folder)
            return;
        var files = ImportService.FindAudioFiles(folder);
        if (files.Count == 0)
        {
            ShowToast($"No audio files in {Path.GetFileName(folder)}.");
            return;
        }

        var scan = PadImport.Classify(folder, files);
        if (scan.Guess == PadGuess.Song || scan.Sets.Count == 0)
            scan = new PadScan(PadGuess.Unsure, [PadImport.Gather(folder, files)], "Imported as a pad set as asked.");
        await ReviewPadImportAsync(scan, canImportAsSong: false);
    }

    /// <summary>The pad set editor: each key's file, making missing keys, the loop and the set's volume; undo puts it back.</summary>
    public async Task EditPadSetAsync(string id)
    {
        if (_pads.PadSets.FirstOrDefault(p => p.Id == id) is not { } before)
            return;
        var editor = PadEditor(before, null, isImport: false, null, false);
        if (await _dialogs.EditPadSetAsync(editor) != PadReviewChoice.Save || !editor.TryGetResult(out var after, out var fill))
            return;

        after = await MakePadKeysAsync(after, fill);
        if (after == before)
            return;
        PadLibrary With(PadSet set) => _pads with { PadSets = _pads.PadSets.Select(p => p.Id == id ? set : p).ToList() };
        await ApplyPadsAsync(With(after));
        _undo.Push(new UndoableChange($"Edited {after.Name}", () => _ = ApplyPadsAsync(With(before)), () => _ = ApplyPadsAsync(With(after))));
        AppLog.Write("Library", $"Edited pad set {after.Name}: {after.MetaLine}");
        ShowToast($"Saved {after.Name} · {after.MetaLine}", canUndo: true);
    }

    /// <summary>A song's menu › Turn into pad set…: its folder reviewed as a pad set; saving deletes the song (one undo).</summary>
    public async Task TurnSongIntoPadSetAsync(SongItemViewModel? item)
    {
        if (item is null || _store.LoadSong(item.Id) is not { } song)
            return;
        if (_song?.Id == song.Id)
        {
            ShowToast($"Load another song before turning {song.Title} into a pad set.");
            return;
        }

        var files = song.Tracks.Select(t => t.File).Where(File.Exists).ToList();
        var folder = files.Select(Path.GetDirectoryName).OfType<string>().GroupBy(d => d).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        if (folder is null)
        {
            ShowToast($"The files of {song.Title} are missing.");
            return;
        }

        var mark = _undo.Count;
        var scan = new PadScan(PadGuess.Unsure, [PadImport.Gather(folder, ImportService.FindAudioFiles(folder))], $"From the song {song.Title}.");
        if (await ReviewPadImportAsync(scan, canImportAsSong: false) != PadReviewChoice.Save)
            return;
        await DeleteSongAsync(item, confirm: false);
        _undo.Group(mark, $"Turned {song.Title} into a pad set");
    }

    /// <summary>A pad set's menu › Import as song…: its folder through the song import; importing deletes the pad set (one undo).</summary>
    public async Task ImportPadSetAsSongAsync(string id)
    {
        if (_pads.PadSets.FirstOrDefault(p => p.Id == id) is not { } set)
            return;
        if (!Directory.Exists(set.Folder))
        {
            ShowToast($"The folder of {set.Name} is missing.");
            return;
        }

        var mark = _undo.Count;
        if (await ImportSongAsync(set.Folder) is not { } song)
            return;
        var summary = SongSummary.From(song);
        _undo.Push(new UndoableChange($"Imported {song.Title}", () => SetSongInLibrary(song, summary, remove: true), () => SetSongInLibrary(song, summary, remove: false)));
        var before = _pads;
        var after = _pads with { PadSets = _pads.PadSets.Where(p => p.Id != id).ToList(), CurrentId = _pads.CurrentId == id ? null : _pads.CurrentId };
        await ApplyPadsAsync(after);
        _undo.Push(new UndoableChange($"Deleted {set.Name}", () => _ = ApplyPadsAsync(before), () => _ = ApplyPadsAsync(after)));
        _undo.Group(mark, $"Imported {set.Name} as a song");
        PadsKey = false;
    }

    private PadSetEditViewModel PadEditor(PadSet set, PadCandidate? candidate, bool isImport, string? guess, bool canImportAsSong) =>
        new(set, candidate, isImport, guess, canImportAsSong,
            file => PreviewPadAsync(file, null, seamPreview: false),
            (file, loop) => PreviewPadAsync(file, loop, seamPreview: true),
            title => _dialogs.PickAudioFileAsync(title));

    /// <summary>
    /// A pad file through the pad output at the pad volume: its first 8 s, or with <paramref name="seamPreview"/> its loop
    /// seam as the pad plays it (<paramref name="seam"/> null: the file's own loop points).
    /// </summary>
    private Task PreviewPadAsync(string file, PadLoop? seam, bool seamPreview)
    {
        var (channel, width) = _settings.Audio.ResolvePad();
        var gain = Math.Max(-60, PadGainDb((_settings.Pads ?? new PadSettings()).GainDb));
        return SendToEngineAsync(e => seamPreview
            ? e.PlayClipAsync(file, channel, width, gain, null, LoopRequest(seam) ?? new PadLoopRequest("file", 0, 0, PadLoop.DefaultCrossfadeSec))
            : e.PlayClipAsync(file, channel, width, gain, (0, 8), null));
    }

    /// <summary>
    /// Makes the keys in <paramref name="keys"/> by transposing the nearest key with a file of its own (the person chose
    /// them). The files go in the media folder; a key that can't be made stays silent and the toast says so.
    /// </summary>
    private async Task<PadSet> MakePadKeysAsync(PadSet set, IReadOnlyList<int> keys)
    {
        if (keys.Count == 0)
            return set;

        var own = set.Samples.Keys.Where(k => !set.Made.ContainsKey(k)).ToList();
        var samples = new Dictionary<int, string>(set.Samples);
        var made = new Dictionary<int, int>(set.Made);
        var folder = MadePadsFolder(set.Id);
        Directory.CreateDirectory(folder);
        var failed = new List<string>();
        foreach (var key in keys)
        {
            if (PadImport.NearestSource(own, key) is not { } source)
                continue;
            var label = new Key(key).ToDisplayString();
            ShowToast($"Making {label} from {new Key(source.From).ToDisplayString()}…");
            var target = Path.Combine(folder, $"{key}-{Guid.NewGuid().ToString("N")[..6]}.flac");
            try
            {
                var result = await (_renderTool ??= new EngineTools(_engineOptions.ExecutablePath)).RenderAsync(set.Samples[source.From], target, source.Semitones, 1);
                if (!result.Ok)
                {
                    failed.Add(label);
                    AppLog.Write("Pads", $"Couldn't make {label} for {set.Name}: {result.Problem}");
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or EngineUnavailableException or InvalidOperationException)
            {
                failed.Add(label);
                AppLog.Error("Pads", ex);
                continue;
            }

            samples[key] = target;
            made[key] = source.From;
        }

        if (failed.Count > 0)
            ShowToast($"{set.Name}: couldn't make {string.Join(", ", failed)}; those keys stay silent.");
        return set with { Samples = samples, Made = made };
    }
}
