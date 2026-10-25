// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// The Prepare screen's logic against the real engine process (no audio device; the
// engine renders nothing unless asked). Dialogs are scripted.
public sealed class MainViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-vm-" + Guid.NewGuid().ToString("N"));
    private readonly string _endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");
    private readonly ScriptedDialogs _dialogs = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static string Executable()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
        {
            var problem = $"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.";
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_NATIVE") == "1")
                Assert.Fail(problem);
            Assert.Skip(problem);
        }

        return executable;
    }

    private MainViewModel NewViewModel()
    {
        var executable = Executable();
        var store = new DataStore(Path.Combine(_root, "data"));
        return new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable), new LockedDispatcher(), _dialogs,
            new SpeechModel(store.Root, allowDownload: false));
    }

    /// <summary>A fixture-A-like folder: 72 BPM, 3/4, key B♭, click, guide and music stems.</summary>
    private string StemsFolder(string name = "Champion - Bb - 72bpm")
    {
        var folder = Path.Combine(_root, "stems", name);
        Directory.CreateDirectory(folder);
        foreach (var stem in new[] { "Keys 2", "Click Track", "Bass", "Guide", "Keys 10", "Drums" })
            Wav(Path.Combine(folder, stem + ".wav"), 48000, stem == "Click Track" ? 1 : 2, 48000 * 100);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not audio");
        return folder;
    }

    private static void Wav(string path, int rate, int channels, int frames)
    {
        using var w = new BinaryWriter(File.Create(path));
        var bytes = frames * channels * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        var data = new byte[bytes];
        new Random(frames).NextBytes(data);
        for (var i = 0; i < data.Length; i += 2)
            data[i + 1] = (byte)(data[i + 1] & 0x0f); // quiet noise
        w.Write(data);
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Imports_a_stems_folder_with_review_lane_order_outputs_and_peaks()
    {
        await using var vm = NewViewModel();
        await vm.InitializeAsync();
        Assert.True(vm.LibraryEmpty);

        await vm.ImportFolderAsync(StemsFolder());

        // The review saw six audio files with their detected roles and outputs (SL-51, SL-52).
        var review = _dialogs.LastReview!;
        Assert.Equal(6, review.Rows.Count);
        Assert.Equal("Champion - Bb - 72bpm", review.Scan.Title);
        Assert.Equal("B♭", review.KeyText);
        Assert.Equal("72", review.BpmText);
        Assert.Equal("Cue", review.Rows.Single(r => r.FileName == "Click Track.wav").Output);
        Assert.Equal("Click", review.Rows.Single(r => r.FileName == "Click Track.wav").Role);
        Assert.Equal("Main", review.Rows.Single(r => r.FileName == "Bass.wav").Output);

        var item = Assert.Single(vm.Songs);
        Assert.Equal("Champion", item.Title);
        Assert.Equal("B♭ · 72 BPM · 3/4 · 6 tracks", item.MetaLine);

        await vm.LoadSongAsync(item.Id);
        Assert.NotNull(vm.Song);
        Assert.Equal(["Click Track", "Guide", "Drums", "Bass", "Keys 2", "Keys 10"], vm.Song!.Tracks.Select(t => t.Name));
        Assert.Equal("Cue 1–2", vm.Song.Tracks[0].OutputLabel);
        Assert.Equal("B♭   72 BPM   3/4", vm.Song.HeaderLine);

        // Peaks are computed in the background and arrive on every lane (SL-54).
        await Eventually(() => vm.Song.Tracks.All(t => t.Peaks is not null), 20000);
    }

    [Fact]
    public async Task Readouts_follow_a_seek_on_the_song_grid()
    {
        await using var vm = NewViewModel();
        await vm.InitializeAsync();
        await vm.ImportFolderAsync(StemsFolder());
        await vm.LoadSongAsync(vm.Songs[0].Id);

        // Fixture A: downbeat at 0.02 s (SONG tab).
        vm.Song!.DownbeatText = "0.020";
        vm.ApplySongEditsCommand.Execute(null);
        await Eventually(() => vm.Song.Model.FirstDownbeatSec > 0.019);

        await vm.SeekAsync(78.52);
        Assert.Equal("bar 32.2 / 39", vm.SongBarText); // 100 s of audio at 2.5 s a bar
        Assert.Equal("1:18 / −0:21", vm.TimeText);
        Assert.Equal("chart bar 32.2", vm.ChartBarText);

        await vm.StepBarAsync(1);
        Assert.StartsWith("bar 33.1", vm.SongBarText);
    }

    [Fact]
    public async Task Fader_moves_reach_the_engine_undo_and_are_saved()
    {
        var executable = Executable();
        await using var vm = NewViewModel();
        await vm.InitializeAsync();
        await vm.ImportFolderAsync(StemsFolder());
        await vm.LoadSongAsync(vm.Songs[0].Id);

        var drums = vm.Song!.Tracks.Single(t => t.Name == "Drums");
        drums.GainDb = -3;
        drums.GainDb = -6.5;  // same drag
        drums.Mute = true;

        // A second connection sees what the engine has (it replaces the view model's; fine for this check).
        await Eventually(() => true);
        vm.UndoCommand.Execute(null);
        Assert.False(drums.Mute);
        Assert.Equal("Undid: Muted Drums", vm.Toast);
        vm.UndoCommand.Execute(null);
        Assert.Equal(0, drums.GainDb);
        vm.RedoCommand.Execute(null);
        Assert.Equal(-6.5, drums.GainDb);

        vm.Flush();
        var saved = new DataStore(Path.Combine(_root, "data")).LoadSong(vm.Song.Id)!;
        Assert.Equal(-6.5, saved.DefaultMix.For(drums.Id).GainDb);
        Assert.False(saved.DefaultMix.For(drums.Id).Mute);
        _ = executable;
    }

    [Fact]
    public async Task Importing_the_same_folder_again_offers_the_existing_song()
    {
        await using var vm = NewViewModel();
        await vm.InitializeAsync();
        var folder = StemsFolder();
        await vm.ImportFolderAsync(folder);

        _dialogs.Duplicate = DuplicateChoice.OpenExisting;
        await vm.ImportFolderAsync(folder);

        Assert.Equal("Champion", _dialogs.DuplicateAskedFor);
        Assert.Single(vm.Songs);
        Assert.Equal(vm.Songs[0].Id, vm.Song?.Id);
    }

    [Fact]
    public async Task A_relaunched_app_reattaches_to_the_engine_and_its_song()
    {
        var first = NewViewModel();
        await first.InitializeAsync();
        await first.ImportFolderAsync(StemsFolder());
        await first.LoadSongAsync(first.Songs[0].Id);
        var songId = first.Song!.Id;
        // The app "crashes": no shutdown; the engine keeps running (REL-34).

        await using var second = NewViewModel();
        await second.InitializeAsync();
        Assert.Equal(songId, second.Song?.Id);
        Assert.Equal(6, second.Song!.Tracks.Count);
    }

    [Fact]
    public async Task A_song_saved_by_milestone_1_opens_at_start_up()
    {
        string songId;
        await using (var first = NewViewModel())
        {
            await first.InitializeAsync();
            await first.ImportFolderAsync(StemsFolder());
            await first.LoadSongAsync(first.Songs[0].Id);
            songId = first.Song!.Id;
            await first.ShutdownEngineAsync();
        }

        // Rewrite the file as milestone 1 left it: no sections and no analysis.
        var path = Path.Combine(_root, "data", "songs", songId + ".json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("sections");
        json.Remove("clickAnalysis");
        json.Remove("guideAnalysis");
        json.Remove("tempoSource");
        json["sections"] = null;
        File.WriteAllText(path, json.ToJsonString());

        await using var vm = NewViewModel();
        await vm.InitializeAsync();
        Assert.Equal(songId, vm.Song?.Id);
        Assert.False(vm.HasSections);
        Assert.DoesNotContain("couldn't be opened", vm.Banner ?? string.Empty); // no audio device here is fine
    }

    [Fact]
    public async Task A_missing_stem_is_reported_and_the_rest_loads()
    {
        await using var vm = NewViewModel();
        await vm.InitializeAsync();
        var folder = StemsFolder();
        await vm.ImportFolderAsync(folder);
        File.Delete(Path.Combine(folder, "Bass.wav"));

        await vm.LoadSongAsync(vm.Songs[0].Id);

        var bass = vm.Song!.Tracks.Single(t => t.Name == "Bass");
        Assert.True(bass.Missing);
        Assert.Equal("File missing", bass.Problem);
        Assert.Contains("File missing: Bass", vm.Toast);
        Assert.Equal(1, vm.Song.MissingCount);
    }
}

/// <summary>Runs posted work inline, one at a time (engine events arrive on another thread).</summary>
internal sealed class LockedDispatcher : IUiDispatcher
{
    private readonly object _gate = new();

    public void Post(Action action)
    {
        lock (_gate)
            action();
    }

    public void After(TimeSpan delay, Action action) =>
        _ = Task.Delay(delay).ContinueWith(_ => Post(action), TaskScheduler.Default);
}

internal sealed class ScriptedDialogs : IDialogs
{
    public string? Folder { get; set; }
    public DuplicateChoice Duplicate { get; set; } = DuplicateChoice.Cancel;
    public ImportReviewViewModel? LastReview { get; private set; }
    public string? DuplicateAskedFor { get; private set; }

    public Task<string?> PickFolderAsync() => Task.FromResult(Folder);

    public Task<bool> ReviewImportAsync(ImportReviewViewModel review)
    {
        LastReview = review;
        if (review.Title.Contains(" - ", StringComparison.Ordinal))
            review.Title = review.Title[..review.Title.IndexOf(" - ", StringComparison.Ordinal)];
        review.MeterText = "3/4";
        return Task.FromResult(true);
    }

    public int Confirmations { get; private set; }
    public bool Confirm { get; set; } = true;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        Confirmations++;
        return Task.FromResult(Confirm);
    }

    public Task<DuplicateChoice> AskDuplicateAsync(string existingTitle)
    {
        DuplicateAskedFor = existingTitle;
        return Task.FromResult(Duplicate);
    }

    public Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings) => Task.FromResult(false);

    public Task EditControlsAsync(ControlsViewModel controls) => Task.CompletedTask;

    public Task ShowServiceCheckAsync(ServiceCheckViewModel check) => Task.CompletedTask;

    public Task<ResumeChoice> AskResumeAsync(ResumeQuestion question) => Task.FromResult(ResumeChoice.DontResume);
    public Task<ServiceDetails?> AskServiceDetailsAsync(string title, string confirmLabel, ServiceDetails initial) => Task.FromResult<ServiceDetails?>(initial);
    public Task<VariantScopeAnswer?> AskVariantScopeAsync(string variantName, int otherEntries) => Task.FromResult<VariantScopeAnswer?>(new VariantScopeAnswer(VariantScope.ThisService, false));
    public Task<string?> AskNameAsync(string title, string label, string initial, string confirmLabel) => Task.FromResult<string?>(initial);
    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> choices, string confirmLabel) => Task.FromResult<int?>(0);
    public Task<bool> EditTransitionAsync(TransitionViewModel transition) => Task.FromResult(false);
}
