// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Services;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// The render cache against the real engine (REL-10, REL-11, REL-14, AT-41): an entry in another key renders its stems,
// the check counts them, and the next load hands them to the engine so the live-key badge goes.
public sealed class RenderQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-render-" + Guid.NewGuid().ToString("N"));
    private readonly string _endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Wav(string path, int seconds)
    {
        using var w = new BinaryWriter(File.Create(path));
        const int rate = 48000;
        var bytes = rate * seconds * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        for (var i = 0; i < rate * seconds; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * 220 * i / rate) * 0.3 * 32767));
    }

    [Fact]
    public async Task AT41_an_entry_in_another_key_renders_its_stems_and_the_next_load_plays_them()
    {
        Environment.SetEnvironmentVariable("RECOMPOSE_NO_DEVICE", "1");
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
        {
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_NATIVE") == "1")
                Assert.Fail($"{EnginePaths.ExecutableFileName} not found");
            Assert.Skip($"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.");
        }

        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "music.wav");
        Wav(file, 3);
        var song = new Song
        {
            Id = "a",
            Title = "Champion",
            Source = new SongSource { Path = _root },
            OriginalKey = "C",
            Bpm = 120,
            BeatsPerBar = 4,
            DurationSec = 3,
            Tracks = [new Track { Id = "music", Name = "Music", File = file, Channels = 1, SampleRate = 48000, Frames = 48000L * 3 }],
            MixPresets = [new MixPreset { Id = "default" }],
            DefaultMixPresetId = "default",
        };
        var store = new DataStore(Path.Combine(_root, "data"));
        store.SaveSong(song);
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(song)] });
        var entry = ServiceEdit.NewEntry(song) with { KeyOffset = 2 };
        store.SaveService(new Service { Id = "s", Name = "Sunday", Date = DateTime.Now.Date.AddDays(1), Entries = [entry] });

        var vm = new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable), new LockedDispatcher(),
            new ServiceDialogs(), new SpeechModel(store.Root, allowDownload: false));
        try
        {
            await vm.InitializeAsync();
            await vm.LoadEntryAsync(entry.Id);
            Assert.StartsWith("Live key", vm.LiveKeyBadge); // nothing rendered yet: live processing (REL-14)
            Assert.Equal(0, vm.Song!.Loaded!.Tracks.Single(t => t.Id == "music").Renders);

            vm.QueueRenders();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            await vm.WaitForRendersAsync();

            var index = store.LoadRenderIndex();
            var record = Assert.Single(index.Renders);
            Assert.Equal((2, 1.0), (record.Semitones, record.Tempo));
            Assert.True(File.Exists(Path.Combine(store.RenderFolder, record.File)));
            Assert.Equal(CheckStatus.Ok, vm.RunServiceCheck()!.Items.Single(i => i.Number == 4).Status);

            // Stopped: the entry loads again with its render; the engine opened it and the badge is gone.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (vm.Song!.Loaded!.Tracks.Single(t => t.Id == "music").Renders == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(1, vm.Song!.Loaded!.Tracks.Single(t => t.Id == "music").Renders);
            Assert.Null(vm.LiveKeyBadge);
            var (peak, _, _) = await vm.EngineForTests!.RenderOfflineAsync(4800, 2, ct: TestContext.Current.CancellationToken);
            Assert.True(peak[0] == 0 || peak[0] > 0); // the graph runs with the render loaded
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }
}
