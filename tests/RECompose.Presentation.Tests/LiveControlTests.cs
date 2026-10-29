// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Core.Arrangements;
using RECompose.Core.Controls;
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
    private readonly LockedDispatcher _ui = new();

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

    private async Task<(MainViewModel Vm, Song Song)> StartAsync(string? arrangementOrder = null, Func<Song, Song>? change = null)
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

        song = change?.Invoke(song) ?? song;
        var store = new DataStore(Path.Combine(_root, "data"));
        store.SaveSong(song);
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(song)] });

        var vm = Open(store, new ServiceDialogs(), executable);
        await vm.InitializeAsync();
        await vm.LoadSongAsync("live");
        if (arrangementOrder is not null)
            vm.SelectVariant("arr");
        return (vm, song);
    }

    private MainViewModel Open(DataStore store, ServiceDialogs dialogs, string? executable = null)
    {
        executable ??= Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        return new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable), _ui,
            dialogs, new SpeechModel(store.Root, allowDownload: false));
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

    private async Task<double> RenderAsync(MainViewModel vm, double seconds)
    {
        var (peak, _, s) = await vm.EngineForTests!.RenderOfflineAsync((int)(seconds * 48000), 2);
        if (s.SongId == vm.Song?.Id && s.SampleRate > 0)
        {
            // The snapshot reaches the view model; read on the dispatcher so its handling has finished too.
            var expected = (s.Holding ? s.Frame : s.HeardFrame) / s.SampleRate;
            await Eventually(() =>
            {
                var reached = false;
                _ui.Post(() => reached = vm.PlanPositionSec == expected);
                return reached;
            });
        }
        else
            await Task.Delay(100);
        return peak[0];
    }

    private async Task FinishAsync(MainViewModel vm)
    {
        if (vm.EngineForTests is not { } engine)
        {
            await vm.DisposeAsync();
            List<Exception> errors = [];
            _ui.Post(() => errors = [.. _ui.Errors]); // under the dispatcher's own lock, where errors are added
            Assert.Fail("The engine connection was lost." + string.Concat(errors.Select(e => Environment.NewLine + e)));
            return;
        }

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
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
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
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
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
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);

            // Verse 1 (8 s) and its two-bar ring-out (4 s), then the hold at 12 s.
            await RenderAsync(vm, 13);
            await Eventually(() => vm.IsHolding);
            Assert.Equal("HOLD", vm.Status);
            Assert.Equal("GO", vm.PlayLabel);
            Assert.Equal(NextState.Holding, vm.Perform!.Next.State);
            Assert.Equal("CHORUS", vm.Perform.Next.Label);
            Assert.Equal("on GO", vm.CountdownText);
            Assert.StartsWith("Holding for", vm.HoldText);

            await vm.PlayPauseAsync(); // the play key: GO reaches the engine before the render
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
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task AT25_a_learned_pedal_releases_the_hold_and_two_presses_within_250_ms_count_once()
    {
        var (vm, _) = await StartAsync("V1 HOLD C");
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 13);
            await Eventually(() => vm.IsHolding);

            // Learn: the next message from a controller binds to GO (LIVE-53).
            var row = vm.Controls.Rows.First(r => r.Action == ControlAction.Go);
            vm.Controls.BeginLearn(ControlAction.Go);
            Assert.True(row.Learning);
            await vm.EngineForTests.InjectMidiAsync("cc", 1, 64, 127, TestContext.Current.CancellationToken);
            await Eventually(() => !row.Learning && row.Inputs.Contains("CC 64 ch 1 ≥ 64", StringComparison.Ordinal));
            Assert.Contains(vm.Controls.Bindings, b => b.Action == ControlAction.Go && b.Input.Kind == ControlKind.ControlChange);

            // The pedal acts and says so.
            await vm.EngineForTests.InjectMidiAsync("cc", 1, 64, 0, TestContext.Current.CancellationToken);
            await vm.EngineForTests.InjectMidiAsync("cc", 1, 64, 127, TestContext.Current.CancellationToken);
            await Eventually(() => vm.Controls.LastInput == "Pedal: GO");
            await RenderAsync(vm, 0.5);
            await Eventually(() => !vm.IsHolding);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task LIVE50_the_forward_key_jumps_to_the_next_item_and_the_back_key_cancels()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 2);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);

            // Verse 1 → the chorus is already next at the end of the section; with Immediately the forward key moves now.
            vm.Timing = JumpTiming.Immediately;
            await vm.RunControlAsync(ControlAction.Next, ActionSource.Keys);
            Assert.True(vm.IsPending);
            Assert.Equal("NEXT · KEYS · PENDING", vm.Perform!.Next.Tag);

            await vm.RunControlAsync(ControlAction.Back, ActionSource.Keys);
            Assert.False(vm.IsPending);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task ARR70_looping_a_section_puts_a_bar_of_click_between_passes_and_move_on_ends_it()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 10); // in the chorus (8–16 s)
            await Eventually(() => vm.NowLabel == "CHORUS");

            await vm.ToggleLoopAsync();
            Assert.True(vm.IsPending);
            await RenderAsync(vm, 6.2); // past 16 s: the loop is the plan now, in its count-in bar (2 s)
            await Eventually(() => vm.NextState == NextState.Repeating);
            Assert.True(vm.IsLooping);

            // The count-in bar has only the click (peaking at 0.501 on the one output pair the test machine gives Main and Cue),
            // then the chorus (0.4) plays again, not Verse 2 (0.6).
            var countIn = await RenderAsync(vm, 1.5);
            Assert.Equal(0.501, countIn, 2);
            await RenderAsync(vm, 0.6);
            var again = await RenderAsync(vm, 1.5);
            Assert.Equal(0.4, again, 2);

            await vm.ToggleLoopAsync(); // the same key ends it after this pass
            Assert.True(vm.IsPending);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task PB64_practice_tempo_slows_the_plan_and_service_lock_puts_it_back()
    {
        var (vm, _) = await StartAsync();
        try
        {
            var normal = vm.Song!.Plan.HeardDurationSec;
            vm.PracticePercent = 85;
            Assert.Equal("Practice 85%", vm.PracticeBadge);
            Assert.Equal(0.85, vm.Song.Plan.TempoFactor, 6);
            Assert.Equal(normal / 0.85, vm.Song.Plan.HeardDurationSec, 3);

            vm.IsServiceLock = true;
            Assert.Equal(100, vm.PracticePercent);
            Assert.Equal(1.0, vm.Song.Plan.TempoFactor, 6);
            Assert.Equal(string.Empty, vm.PracticeBadge);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task ARR90_an_arrangement_edit_while_playing_takes_effect_after_the_current_item_without_a_gap()
    {
        var (vm, song) = await StartAsync("V1 C");
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 2);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);
            var framesBefore = vm.EnginePlanFrames;
            var positionBefore = vm.PlanPositionSec;
            Assert.True(framesBefore > 0);

            // Add Verse 2 to the end of the variant that is playing.
            var verse2 = vm.Song!.Sections.First(i => i.Label == "Verse 2");
            vm.Entry.AddSection(verse2.Id);

            // The plan grew, and the song went on from where it was: no seek back, still playing.
            await RenderAsync(vm, 1);
            await Eventually(() => vm.EnginePlanFrames > framesBefore);
            Assert.Equal(RECompose.Engine.TransportState.Playing, vm.State);
            Assert.InRange(vm.PlanPositionSec, positionBefore + 0.8, positionBefore + 1.3);
            Assert.Equal(0.2, await RenderAsync(vm, 1), 2); // still Verse 1
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task AT44_after_a_crash_the_dialog_resumes_the_section_with_a_count_in_and_the_live_changes()
    {
        var (vm, song) = await StartAsync();
        var store = new DataStore(Path.Combine(_root, "data"));
        string journalText;
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 10);                 // in the chorus
            await Eventually(() => vm.NowLabel == "CHORUS");
            await vm.LiveAsync(LiveActionKind.Vamp);   // the chorus repeats until moved on
            await RenderAsync(vm, 6.5);                // the vamp is the plan now (16 s), in its second pass
            await Eventually(() => vm.NextState == NextState.Repeating);

            vm.WriteJournal();
            await vm.FlushJournalAsync();
            journalText = File.ReadAllText(Path.Combine(_root, "data", "journal.json"));
            Assert.Contains("\"transport\": \"playing\"", journalText);
        }
        finally
        {
            await FinishAsync(vm);
        }

        // A kill leaves the journal and an unclean state behind; a normal quit would have removed them.
        Assert.Null(store.LoadJournal());
        File.WriteAllText(Path.Combine(_root, "data", "journal.json"), journalText);
        store.SaveState(new RECompose.Core.Recovery.AppState { CleanShutdown = false });

        var dialogs = new ServiceDialogs { ResumeAnswer = ResumeChoice.ResumeCurrent };
        var again = Open(store, dialogs);
        try
        {
            await again.InitializeAsync();
            await Eventually(() => again.Song is not null);

            Assert.NotNull(dialogs.ResumeAsked);
            Assert.Contains("closed unexpectedly", dialogs.ResumeAsked!.Message);
            Assert.Contains("Live", dialogs.ResumeAsked.Message);
            Assert.Equal("Chorus", dialogs.ResumeAsked.CurrentLabel);
            Assert.Equal("Verse 2", dialogs.ResumeAsked.NextLabel);

            // The vamp from before the crash is still in the plan, and a bar of click leads in.
            Assert.Contains(again.Song!.Plan.Items, i => i.Id.StartsWith("resume-count-in", StringComparison.Ordinal));
            Assert.True(again.Song.Plan.Items.Count(i => i.Label == "Chorus" && i.IsOpen) >= 2);

            // Playing: two seconds of click (the count-in), then the chorus.
            await Eventually(() => again.State == RECompose.Engine.TransportState.Playing);
            var click = await RenderAsync(again, 1.8);
            Assert.Equal(0.501, click, 2);
            await RenderAsync(again, 0.4);
            Assert.Equal(0.4, await RenderAsync(again, 1.5), 2);
        }
        finally
        {
            await FinishAsync(again);
        }
    }

    // ---- Device loss (protocol 10, REL-20…22, AT-43). With no device here the loss starts the silent clock at once.

    [Fact]
    public async Task AT43_with_no_fallback_the_clock_keeps_real_time_silently_and_the_banner_says_so()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 2);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);
            var before = vm.PlanPositionSec;

            var route = await vm.EngineForTests.InjectDeviceLossAsync(ct: TestContext.Current.CancellationToken);
            Assert.Equal("silent", route);
            await Eventually(() => vm.OnSilentClock);
            Assert.True(vm.AudioLost);
            Assert.Contains("was lost", vm.Banner);
            Assert.Equal("No audio device · keeping time", vm.AudioStatus);

            // Nothing renders offline now: the silent clock moves the song on in real time.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await Eventually(() => vm.PlanPositionSec - before >= 1.5);
            var moved = vm.PlanPositionSec - before;
            Assert.InRange(moved, 1.5, stopwatch.Elapsed.TotalSeconds + 0.1); // real time, never faster
            Assert.Equal(RECompose.Engine.TransportState.Playing, vm.State);

            // The device is back: the banner goes.
            await vm.EngineForTests.InjectDeviceLossAsync(present: true, ct: TestContext.Current.CancellationToken);
            await Eventually(() => !vm.OnSilentClock);
            Assert.False(vm.HasBanner);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task REL21_the_silent_clock_pauses_the_song_at_its_limit()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 1);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);

            await vm.EngineForTests.InjectDeviceLossAsync(silentLimitSec: 1, ct: TestContext.Current.CancellationToken);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Paused, 5000);
            Assert.StartsWith("No audio device for 1 s. Paused at VERSE 1", vm.Banner);
            Assert.InRange(vm.PlanPositionSec, 1.8, 2.6); // a second of rendering plus the silent second
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task REL41_panic_leaves_the_song_loaded_and_offers_to_resume_where_it_stopped()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 10);
            await Eventually(() => vm.NowLabel == "CHORUS");

            await vm.PanicAsync();
            await RenderAsync(vm, 0.5);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Stopped);
            Assert.True(vm.HasPanicResume);
            Assert.Equal("Stopped by PANIC · Resume at Chorus", vm.PanicResumeText);
            Assert.NotNull(vm.Song);

            vm.ResumeAfterPanicCommand.Execute(null);
            await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);
            Assert.False(vm.HasPanicResume);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    // ---- Voice (VC-2, VC-40, VC-45, VC-46, VC-55, VC-70) with transcripts as the recogniser would give them ----------

    private async Task PlayingAsync(MainViewModel vm)
    {
        await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
        await RenderAsync(vm, 1);
        await Eventually(() => vm.State == RECompose.Engine.TransportState.Playing);
    }

    [Fact]
    public async Task AT61_with_the_microphone_blocked_the_voice_indicator_is_an_error_that_names_the_system_setting()
    {
        var (vm, _) = await StartAsync();
        try
        {
            MicrophoneAccess.Override = () => true;
            vm.UpdateSettings(s => s with { Audio = s.Audio with { VoiceChannel = 0 } });
            await PlayingAsync(vm);
            await Eventually(() => vm.VoiceToolTip.StartsWith(MicrophoneAccess.Problem, StringComparison.Ordinal));
            Assert.Equal("Error", vm.VoiceIndicator);
            Assert.False(vm.VoiceOk);

            MicrophoneAccess.Override = () => false;
            await Eventually(() => !vm.VoiceToolTip.StartsWith(MicrophoneAccess.Problem, StringComparison.Ordinal));
        }
        finally
        {
            MicrophoneAccess.Override = null;
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task VC70_a_voice_jump_shows_on_the_next_card_with_what_was_heard_and_never_mind_vamps_instead()
    {
        var (vm, _) = await StartAsync();
        try
        {
            await vm.HearAsync("let's go to verse two", 0.9, 0.2, 0.9); // stopped: ignored (VC-2)
            Assert.False(vm.IsPending);

            await PlayingAsync(vm);
            await vm.HearAsync("Let's go to verse two", 0.9, 0.2, 0.9);
            Assert.True(vm.IsPending);
            Assert.Equal("NEXT · VOICE · PENDING", vm.Perform!.Next.Tag);
            Assert.Equal("VERSE 2", vm.Perform.Next.Label);
            Assert.Contains("heard \"let s go to verse two\"", vm.NextDetail);
            Assert.Equal("Verse 2 next", vm.VoiceCaption);

            await vm.HearAsync("Never mind, let's stay in this section", 0.9, 0.5, 1.2);
            Assert.Equal("Vamping Verse 1", vm.VoiceCaption);
            Assert.True(vm.IsPending);
            Assert.DoesNotContain(vm.Strip, t => t.Label == "Verse 2" && t.State == TileState.Target);

            await vm.HearAsync("bridge", 0.9, 0.5, 1.2); // no such section
            Assert.Equal("didn't catch a section", vm.VoiceCaption);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task VC40_the_guide_saying_chorus_is_ignored_and_another_section_is_not()
    {
        var (vm, _) = await StartAsync(change: s => s with
        {
            GuideAnalysis = new GuideAnalysis { TrackId = "music", Words = [new GuideWord("Chorus", 1.0, 1.4, 0.9)] },
        });
        try
        {
            await PlayingAsync(vm);
            await vm.HearAsync("chorus", 0.9, 1.2, 1.8);
            Assert.Equal("ignored — guide talking", vm.VoiceCaption);
            Assert.False(vm.IsPending);

            await vm.HearAsync("verse two", 0.9, 1.2, 1.8);
            Assert.True(vm.IsPending);
            Assert.Equal("VERSE 2", vm.Perform!.Next.Label);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task VC46_confirm_mode_proposes_and_yes_or_go_confirms_and_low_confidence_is_ignored()
    {
        var (vm, _) = await StartAsync();
        try
        {
            vm.UpdateSettings(s => s with { Audio = s.Audio with { VoiceConfirm = true } });
            await PlayingAsync(vm);

            await vm.HearAsync("verse two", 0.3, 0.2, 0.8);
            Assert.Equal("low confidence", vm.VoiceCaption);

            await vm.HearAsync("verse two", 0.9, 0.2, 0.8);
            Assert.True(vm.IsProposing);
            Assert.Equal("VOICE? Verse 2 next · say \"yes\" or press GO", vm.ProposalText);
            Assert.False(vm.IsPending);

            await vm.HearAsync("yes", 0.9, 1.0, 1.3);
            Assert.False(vm.IsProposing);
            Assert.True(vm.IsPending);

            await vm.CancelLiveAsync();
            await vm.HearAsync("skip next", 0.9, 1.5, 2.0); // Chorus is next: Verse 2 after it
            Assert.True(vm.IsProposing);
            await vm.RunControlAsync(ControlAction.Go, ActionSource.Pedal);
            Assert.True(vm.IsPending);
            Assert.Equal("VERSE 2", vm.Perform!.Next.Label);
        }
        finally
        {
            await FinishAsync(vm);
        }
    }

    [Fact]
    public async Task AUTO1_AUTO21_section_notes_and_a_cue_fire_on_the_audio_clock_and_are_logged()
    {
        var (vm, _) = await StartAsync();
        var lines = new List<string>();
        void OnAdded(RECompose.Core.Logging.LogEntry e)
        {
            lock (lines)
                lines.Add(e.Text);
        }

        AppLog.Added += OnAdded;
        try
        {
            vm.SectionNotes = true;
            await vm.SeekPlanAsync(4);
            await Eventually(() => Math.Abs(vm.PlanPositionSec - 4) < 0.01);
            vm.AddCueCommand.Execute(null); // C at 4 s, in Verse 1
            var cue = Assert.Single(vm.Cues);
            Assert.Equal("Cue 1", cue.Name);
            Assert.Equal("MIDI note C4", cue.Summary);
            await vm.PlanSend; // the plan with the cue reaches the engine

            await vm.SeekPlanAsync(0);
            await vm.EngineForTests!.PlayAsync(TestContext.Current.CancellationToken);
            await RenderAsync(vm, 9);
            bool Has(string line)
            {
                lock (lines)
                    return lines.Contains(line);
            }

            await Eventually(() => Has("Section Verse 1: MIDI note D3") && Has("Cue 1: MIDI note C4") && Has("Section Chorus: MIDI note E3"));
            lock (lines)
                Assert.DoesNotContain("Section Verse 2: MIDI note D3", lines); // not reached yet

            // The LOG tab (AUTO-50): newest first, the Sections chip shows only section notes, Clear view empties it.
            await Eventually(() => vm.ShownLog.Any(e => e.Text == "Section Chorus: MIDI note E3"));
            vm.LogChip = "Sections";
            Assert.All(vm.ShownLog, e => Assert.Equal("Sections", e.Chip)); // the log is process-wide: other tests' lines may be there too
            Assert.DoesNotContain(vm.ShownLog, e => e.Text == "Cue 1: MIDI note C4");
            Assert.Equal("Section Chorus: MIDI note E3", vm.ShownLog.First(e => e.Text.StartsWith("Section ", StringComparison.Ordinal)).Text); // newest first
            vm.LogChip = "All";
            vm.LogSearch = "cue 1";
            Assert.Contains(vm.ShownLog, e => e.Text == "Cue 1: MIDI note C4");
            vm.ClearLogCommand.Execute(null);
            Assert.Empty(vm.ShownLog);
        }
        finally
        {
            AppLog.Added -= OnAdded;
            await FinishAsync(vm);
        }
    }
}
