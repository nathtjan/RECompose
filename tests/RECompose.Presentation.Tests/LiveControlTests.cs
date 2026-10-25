// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// Live control against the real engine (PB-44, PB-45, PB-47, PB-52, AT-13, AT-21, AT-27, ARR-90). The engine has no audio
// device here, so the tests render offline through it to make time pass. The song is three 8-second sections at 120 BPM
// (4 bars each) at levels 0.2, 0.4 and 0.6, so the peak says which section sounds.
public sealed class LiveControlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-live-" + Guid.NewGuid().ToString("N"));
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

    private static double Level(double sec) => sec < 8 ? 0.2 : sec < 16 ? 0.4 : 0.6;

    private static void Wav(string path, int seconds)
    {
        using var w = new BinaryWriter(File.Create(path));
        const int rate = 48000;
        var bytes = rate * seconds * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        for (var i = 0; i < rate * seconds; i++)
            w.Write((short)(Level(i / (double)rate) * 32767));
    }

    private async Task<(MainViewModel Vm, Song Song)> StartAsync(string? arrangementOrder = null)
    {
        Environment.SetEnvironmentVariable("RECOMPOSE_NO_DEVICE", "1"); // time passes only when the test renders
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
        {
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_NATIVE") == "1")
                Assert.Fail($"{EnginePaths.ExecutableFileName} not found");
            Assert.Skip($"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.");
        }

        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "live.wav");
        Wav(file, 24);
        var sections = new[] { ("Verse 1", 0.0), ("Chorus", 8.0), ("Verse 2", 16.0) }
            .Select(s => Section.Create(s.Item1, s.Item2, SectionOrigin.Manual)).ToList();
        var song = new Song
        {
            Id = "live",
            Title = "Live",
            Source = new SongSource { Path = _root },
            OriginalKey = "C",
            Bpm = 120,
            BeatsPerBar = 4,
            DurationSec = 24,
            Sections = sections,
            Tracks = [new Track { Id = "music", Name = "Music", File = file, Channels = 1, SampleRate = 48000, Frames = 48000L * 24 }],
            MixPresets = [new MixPreset { Id = "default" }],
            DefaultMixPresetId = "default",
        };

        if (arrangementOrder is not null)
        {
            var infos = SectionList.Describe(song.Sections, song.Grid, song.DurationSec);
            var parsed = QuickOrder.Parse(arrangementOrder, infos);
            Assert.False(parsed.HasProblems);
            song = song with { Arrangements = [new Arrangement { Id = "arr", Name = "Arr", Entries = [.. parsed.Entries] }] };
        }

        var store = new DataStore(Path.Combine(_root, "data"));
        store.SaveSong(song);
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(song)] });

        var vm = new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable), new LockedDispatcher(),
            new ServiceDialogs(), new SpeechModel(store.Root, allowDownload: false));
        await vm.InitializeAsync();
        await vm.LoadSongAsync("live");
        if (arrangementOrder is not null)
            vm.SelectVariant("arr");
        return (vm, song);
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

    private static async Task<double> RenderAsync(MainViewModel vm, double seconds)
    {
        var (peak, _, _) = await vm.EngineForTests!.RenderOfflineAsync((int)(seconds * 48000), 2);
        await Task.Delay(100); // the snapshot reaches the view model
        return peak[0];
    }

    private static async Task FinishAsync(MainViewModel vm)
    {
        var engine = vm.EngineForTests!;
        await engine.StopAsync();
        await engine.RenderOfflineAsync(9600, 2);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task AT21_a_jump_queues_shows_pending_and_lands_at_the_end_of_the_section_without_stopping()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync();
            await RenderAsync(vm, 2);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);

            var verse2 = vm.Song!.Plan.Items.Last(i => i.Kind == PlanItemKind.Section);
            var result = await vm.LiveAsync(LiveActionKind.JumpToItem, verse2.Id, source: ActionSource.Keys);
            Assert.Equal(LiveOutcome.Queued, result.Outcome);

            Assert.True(vm.IsPending);
            Assert.Equal("NEXT · KEYS · PENDING", vm.Perform!.Next.Tag);
            Assert.Equal("VERSE 2", vm.Perform.Next.Label);
            Assert.Equal(8.0, vm.PendingAtSec!.Value, 3);
            Assert.True(vm.CanCancel);
            Assert.Contains(vm.Strip, t => t.Label == "Chorus" && t.State == TileState.Skipped);
            Assert.Contains(vm.Strip, t => t.Label == "Verse 2" && t.State == TileState.Target);

            // Verse 1 plays to its end (0.2), then Verse 2 (0.6) follows with no Chorus (0.4) between.
            var before = await RenderAsync(vm, 5.9);
            Assert.Equal(0.2, before, 2);
            await RenderAsync(vm, 0.1 + 0.1); // the seam is a 10 ms splice (PB-55): the old audio blends out under the new
            var across = await RenderAsync(vm, 2.4);
            Assert.Equal(0.6, across, 2);
            await Eventually(() => !vm.IsPending);
            Assert.Equal(RECompose.Engine.TransportState.Playing, vm.State);
            Assert.Equal("VERSE 2", vm.NowLabel);
            Assert.DoesNotContain(vm.Strip, t => t.State == TileState.Skipped);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task PB45_cancel_keeps_the_plan_that_was_playing()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync();
            await RenderAsync(vm, 2);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);

            var verse2 = vm.Song!.Plan.Items.Last(i => i.Kind == PlanItemKind.Section);
            await vm.LiveAsync(LiveActionKind.JumpToItem, verse2.Id);
            Assert.True(vm.IsPending);

            await vm.CancelLiveAsync();
            Assert.False(vm.IsPending);
            Assert.False(vm.CanCancel);

            await RenderAsync(vm, 5.9);
            var chorus = await RenderAsync(vm, 2.5);
            Assert.Equal(0.4, chorus, 2);
            Assert.Equal("CHORUS", vm.NowLabel);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task AT13_a_hold_shows_the_timer_and_go_releases_it_and_the_play_key_is_go()
    {
        var (vm, _) = await StartAsync("V1 HOLD C");
        try
        {
            Assert.True(vm.Song!.Plan.Holds().Count == 1);
            await vm.EngineForTests!.PlayAsync();

            // Verse 1 (8 s) and its two-bar ring-out (4 s), then the hold at 12 s.
            await RenderAsync(vm, 13);
            await Eventually(() => vm.IsHolding);
            Assert.Equal("HOLD", vm.Status);
            Assert.Equal("GO", vm.PlayLabel);
            Assert.Equal(NextState.Holding, vm.Perform!.Next.State);
            Assert.Equal("CHORUS", vm.Perform.Next.Label);
            Assert.Equal("on GO", vm.CountdownText);
            Assert.StartsWith("Holding for", vm.HoldText);

            vm.PlayPauseCommand.Execute(null);
            await RenderAsync(vm, 0.5);
            await Eventually(() => !vm.IsHolding);
            Assert.Equal("PLAYING", vm.Status);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task AT23_a_vamp_repeats_until_moved_on()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync();
            await RenderAsync(vm, 10); // inside the chorus
            await Eventually(() => vm.NowLabel == "CHORUS");

            var vamp = await vm.LiveAsync(LiveActionKind.Vamp);
            Assert.Equal(LiveOutcome.Queued, vamp.Outcome);
            await RenderAsync(vm, 6.5); // past the end of the chorus at 16 s: the second pass plays, not Verse 2
            await Eventually(() => vm.NextState == NextState.Repeating);
            Assert.Equal("REPEATING", vm.Perform!.Next.Label);
            Assert.Equal("until cued", vm.NowPassText);

            var move = await vm.LiveAsync(LiveActionKind.MoveOn);
            Assert.Equal(LiveOutcome.Queued, move.Outcome);
            await RenderAsync(vm, 8); // the second pass ends at 24 s of the plan, then Verse 2
            await Eventually(() => vm.NextState != NextState.Repeating);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }
}
