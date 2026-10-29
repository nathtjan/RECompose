// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Services;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// Running a service against the real engine (SL-12, SL-30, PB-11, PB-23, PB-24, AT-31). The engine has no audio
// device here, so the tests render offline through it to make time pass.
public sealed class ServiceRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-run-" + Guid.NewGuid().ToString("N"));
    private readonly string _endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");

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

    private static void Wav(string path, int seconds, double level)
    {
        using var w = new BinaryWriter(File.Create(path));
        const int rate = 48000;
        var bytes = rate * seconds * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        var sample = (short)(level * 32767);
        for (var i = 0; i < rate * seconds; i++)
            w.Write(sample);
    }

    private Song MakeSong(string id, string title, int seconds, double level)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, id + ".wav");
        Wav(file, seconds, level);
        var song = new Song
        {
            Id = id,
            Title = title,
            Source = new SongSource { Path = _root },
            OriginalKey = "C",
            Bpm = 120,
            BeatsPerBar = 4,
            DurationSec = seconds,
            Tracks = [new Track { Id = "music", Name = "Music", File = file, Channels = 1, SampleRate = 48000, Frames = 48000L * seconds }],
            MixPresets = [new MixPreset { Id = "default" }],
            DefaultMixPresetId = "default",
        };
        new DataStore(Path.Combine(_root, "data")).SaveSong(song);
        return song;
    }

    private async Task<MainViewModel> StartAsync(TransitionType type, bool autoAdvance)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
        {
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_NATIVE") == "1")
                Assert.Fail($"{EnginePaths.ExecutableFileName} not found");
            Assert.Skip($"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.");
        }

        var a = MakeSong("a", "Champion", 4, 0.5);
        var b = MakeSong("b", "Goodness", 2, 0.25);
        var store = new DataStore(Path.Combine(_root, "data"));
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(a), SongSummary.From(b)] });
        var first = ServiceEdit.NewEntry(a) with { TransitionAfter = new Transition { Type = type } };
        var service = new Service { Id = "s", Name = "Sunday", AutoAdvance = autoAdvance, Entries = [first, ServiceEdit.NewEntry(b)] };
        store.SaveService(service);

        var vm = new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable), new LockedDispatcher(),
            new ServiceDialogs(), new SpeechModel(store.Root, allowDownload: false));
        await vm.InitializeAsync();
        await vm.LoadEntryAsync(first.Id);
        await Eventually(() => vm.NextSongTitle == "Goodness");
        return vm;
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    /// <summary>Plays for some seconds through the engine's offline render.</summary>
    private static async Task RenderAsync(MainViewModel vm, double seconds)
    {
        var engine = vm.EngineForTests!;
        await engine.PlayAsync();
        await engine.RenderOfflineAsync((int)(seconds * 48000), 2);
    }

    /// <summary>Stops and renders a little so the engine's stop fade finishes and it can exit.</summary>
    private static async Task FinishAsync(MainViewModel vm)
    {
        var engine = vm.EngineForTests!;
        await engine.StopAsync();
        await engine.RenderOfflineAsync(4800, 2);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task PB11_the_next_entry_is_preloaded_and_follows_edits()
    {
        var vm = await StartAsync(TransitionType.Stop, autoAdvance: false);
        try
        {
            Assert.NotNull(vm.NextServiceEntry);
            Assert.True(vm.NextSongCommand.CanExecute(null));

            // Another key for the next entry: the engine's next slot is loaded again with it.
            var next = vm.NextServiceEntry!;
            vm.SetKeyOffset(next.Id, 2);
            await Eventually(() => ReferenceEquals(vm.NextServiceEntry, vm.LoadedService!.Entry(next.Id)) && vm.NextSongTitle == "Goodness");

            // Removing it leaves nothing to preload.
            vm.RemoveEntry(next.Id);
            await Eventually(() => vm.NextSongTitle is null);
            Assert.False(vm.NextSongCommand.CanExecute(null));
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task PB11_removing_the_next_entry_while_it_preloads_leaves_nothing_preloaded()
    {
        var vm = await StartAsync(TransitionType.Stop, autoAdvance: false);
        try
        {
            // A key change reloads the next slot; the entry is removed when the engine has the reload but the app has not
            // recorded it yet. The reload finishing after the removal must not put the song back.
            var next = vm.NextServiceEntry!;
            var removed = false;
            vm.NextLoadedForTests = () =>
            {
                if (!removed)
                {
                    removed = true;
                    vm.RemoveEntry(next.Id);
                }

                return Task.CompletedTask;
            };
            vm.SetKeyOffset(next.Id, 2);

            await Eventually(() => removed && vm.NextSongTitle is null);
            await vm.EngineForTests!.Session.PingAsync(TestContext.Current.CancellationToken); // every refresh has had its replies
            await Task.Delay(100, TestContext.Current.CancellationToken); // and has gone on from them
            Assert.Null(vm.NextSongTitle);
            Assert.False(vm.NextSongCommand.CanExecute(null));
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task AUTO60_leaving_perform_after_a_song_played_writes_the_service_report()
    {
        var vm = await StartAsync(TransitionType.Stop, autoAdvance: false);
        try
        {
            vm.IsPerform = true;
            await RenderAsync(vm, 1);
            await Eventually(() => vm.IsPlaying);
            await Task.Delay(200, TestContext.Current.CancellationToken); // a snapshot while playing records the item heard
            vm.IsPerform = false;

            Assert.NotNull(vm.LastReportPath);
            var report = await File.ReadAllTextAsync(vm.LastReportPath!, TestContext.Current.CancellationToken);
            Assert.StartsWith("# Sunday", report);
            Assert.Contains("## Champion", report);
            Assert.Contains("- As played: Whole song", report);
            Assert.Contains("- Songs: 1", report);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task SL12_with_auto_advance_a_stop_transition_cues_the_next_entry()
    {
        var vm = await StartAsync(TransitionType.Stop, autoAdvance: true);
        try
        {
            var first = vm.LoadedEntry!.Id;
            await RenderAsync(vm, 4.5); // the first song is 4 s long
            await Eventually(() => vm.LoadedEntry?.Id != first);
            Assert.Equal("Goodness", vm.Song!.Title);
            Assert.True(vm.Setlist.Rows[1].IsLoaded);
            Assert.False(vm.IsPlaying);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task AT31_a_segue_starts_the_next_entry_by_itself_and_the_header_follows()
    {
        var vm = await StartAsync(TransitionType.Segue, autoAdvance: false);
        try
        {
            var first = vm.LoadedEntry!.Id;
            await RenderAsync(vm, 2); // still in the first song
            Assert.Equal(first, vm.LoadedEntry!.Id);

            await vm.EngineForTests!.RenderOfflineAsync(3 * 48000, 2, ct: TestContext.Current.CancellationToken); // the first song ends at 4 s: the next takes over
            await Eventually(() => vm.LoadedEntry?.Id != first);
            Assert.Equal("Goodness", vm.Song!.Title);
            Assert.Null(vm.NextServiceEntry); // it is the last entry
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task AT30_a_pad_transition_waits_for_play_and_starts_the_next_entry_on_a_click_downbeat()
    {
        var vm = await StartAsync(TransitionType.Pad, autoAdvance: false);
        try
        {
            var first = vm.LoadedEntry!.Id;
            await RenderAsync(vm, 4.5); // the first song ends at 4 s; the pad and the click run on
            await Eventually(() => vm.Status == "TRANSITION");
            Assert.Equal("NEXT SONG", vm.NextTag);
            Assert.Equal("GOODNESS", vm.NextLabel);
            Assert.Equal("Press Play", vm.CountdownText);
            Assert.Equal(first, vm.LoadedEntry!.Id); // still the first entry until Play

            // Play starts the next song on the next downbeat of the click (120 BPM in 4/4: a bar is 2 s from the transition's start).
            vm.PlayPauseCommand.Execute(null);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            await vm.EngineForTests!.RenderOfflineAsync(3 * 48000, 2, ct: TestContext.Current.CancellationToken);
            await Eventually(() => vm.LoadedEntry?.Id != first);
            Assert.Equal("Goodness", vm.Song!.Title);
            await Eventually(() => vm.Status != "TRANSITION");
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task PB23_PB24_previous_and_next_entries_load_from_the_header()
    {
        var vm = await StartAsync(TransitionType.Stop, autoAdvance: false);
        try
        {
            vm.NextSongCommand.Execute(null);
            await Eventually(() => vm.Song?.Title == "Goodness");

            // The previous key at the start of a stopped entry selects the previous one (PB-23).
            vm.ToStartCommand.Execute(null);
            await Eventually(() => vm.Song?.Title == "Champion");
        }
        finally
        {
            await FinishAsync(vm);
        }
    }
}
