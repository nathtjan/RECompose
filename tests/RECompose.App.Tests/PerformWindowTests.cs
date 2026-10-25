// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Track = RECompose.Core.Library.Track;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using RECompose.App.Controls;
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Planning;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

// The Perform screen at the minimum window, 1280 x 760 (UI-40, UI-14, LIVE-20…22, WF-11, PB-52). No engine: the view model is
// fed snapshots as the engine would send them. Frames are saved under screenshots/ next to the test assembly.
public sealed class PerformWindowTests : IDisposable
{
    private static HeadlessUnitTestSession Session => HeadlessApp.Session;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-app-perf-" + Guid.NewGuid().ToString("N"));

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

    private DataStore SaveLibrary()
    {
        var grid = new TempoGrid(72, 3, 0.02);
        var sections = new[] { ("Intro", 1), ("Verse 1", 11), ("Chorus", 27), ("Verse 2", 41), ("Ending", 57) }
            .Select(s => Section.Create(s.Item1, grid.BarToTime(s.Item2), SectionOrigin.Guide)).ToList();
        var infos = SectionList.Describe(sections, grid, 130);
        var parsed = QuickOrder.Parse("V1 HOLD C x? V2 E", infos);
        var song = new Song
        {
            Id = "champion",
            Title = "Champion",
            Source = new SongSource { Path = _root },
            OriginalKey = "Bb",
            Bpm = 72,
            BeatsPerBar = 3,
            FirstDownbeatSec = 0.02,
            DurationSec = 130,
            Sections = sections,
            Arrangements = [new Arrangement { Id = "live", Name = "Live", Entries = [.. parsed.Entries] }],
            DefaultArrangementId = "live",
            Tracks =
            [
                new Track { Id = "click", Name = "Click", File = Path.Combine(_root, "click.wav"), Role = TrackRole.Click, Group = TrackGroups.Click },
                new Track { Id = "keys", Name = "Keys", File = Path.Combine(_root, "keys.wav"), Group = TrackGroups.Keys },
            ],
        };
        var other = song with { Id = "goodness", Title = "Goodness", OriginalKey = "Db", Arrangements = [], DefaultArrangementId = null };

        var store = new DataStore(Path.Combine(_root, "data"));
        store.SaveSong(song);
        store.SaveSong(other);
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(song), SongSummary.From(other)] });
        var service = ServiceEdit.New("Sunday 26 Oct", new DateTime(2026, 10, 26, 9, 0, 0));
        service = ServiceEdit.Add(service, ServiceEdit.NewEntry(song));
        service = ServiceEdit.Add(service, ServiceEdit.NewEntry(other));
        store.SaveService(service);
        return store;
    }

    private static EngineSnapshot Snapshot(MainViewModel vm, double planSec, bool holding = false, double holdSec = 0) =>
        new(TransportState.Playing, (long)(planSec * 48000), 0, 0, 48000, vm.Song!.Id, 48000L * 130, true, false, 0, 0, 0, true,
            new Dictionary<string, float>(), 0, 0, Holding: holding, HoldSec: holdSec);

    private static string Shot(TopLevel window, string name)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".png");
        window.CaptureRenderedFrame()!.Save(path);
        return path;
    }

    [Fact]
    public async Task Perform_shows_now_next_the_strip_and_the_deck_and_follows_a_pending_jump_a_hold_and_the_lock()
    {
        var sink = new CollectingSink();
        await HeadlessApp.RunAsync(async () =>
        {
            Logger.Sink = sink;
            var store = SaveLibrary();
            var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
            await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing),
                new AvaloniaDispatcher(), new AcceptingDialogs(), new SpeechModel(store.Root, allowDownload: false));

            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
            window.Show();
            await vm.InitializeAsync();
            await vm.LoadEntryAsync(vm.Setlist.Rows[0].Id);
            Dispatcher.UIThread.RunJobs();


            // Ctrl+P enters Perform; playback is not touched (WF-10).
            Assert.False(vm.IsPerform);
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsPerform);

            var perform = window.FindControl<PerformView>("Perform")!;
            Assert.True(perform.IsVisible);
            Assert.True(perform.Bounds.Width >= 1279 && perform.Bounds.Height >= 759);

            // During Verse 1: NOW names the section, the strip has a tile per plan item with the current one marked.
            vm.ApplySnapshotForTests(Snapshot(vm, 6));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("VERSE 1", perform.FindControl<TextBlock>("NowLabel")!.Text);
            Assert.Equal("HOLD", perform.FindControl<TextBlock>("NextLabelText")!.Text);
            Assert.Contains(vm.Tiles, t => t.Label == "Verse 1" && t.IsCurrent);
            Assert.Contains(vm.Tiles, t => t.Label == "Chorus" && t.State == TileState.Upcoming);
            Assert.Equal(1, vm.Tiles.Count(t => t.IsHold));
            Assert.Equal("Play", vm.PlayLabel.Replace("Pause", "Play"));
            Shot(window, "perform-1-playing");

            // A jump to the ending: the NEXT card turns pending, the target is outlined, the skipped tiles are struck through (LIVE-21).
            var ending = vm.Song!.Plan.Items.Last(i => i.Kind == PlanItemKind.Section);
            var result = await vm.LiveAsync(LiveActionKind.JumpToItem, ending.Id, source: ActionSource.Keys);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(LiveOutcome.Queued, result.Outcome);
            Assert.Equal("NEXT · KEYS · PENDING", perform.FindControl<TextBlock>("NextTagText")!.Text);
            Assert.True(perform.FindControl<Border>("NextCard")!.Classes.Contains("pending"));
            Assert.Contains(vm.Tiles, t => t.IsTarget && t.Label == "Ending");
            Assert.Contains(vm.Tiles, t => t.IsSkipped);
            Assert.True(vm.CanCancel);
            Assert.True(perform.FindControl<Button>("CancelKey")!.Command!.CanExecute(null));
            Shot(window, "perform-2-pending");

            // Cancel clears it.
            await vm.CancelLiveAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsPending);
            Assert.DoesNotContain(vm.Tiles, t => t.IsSkipped);

            // The song waits at the hold: HOLD in the NOW block, the timer, the NEXT card says what GO starts, the play key is GO.
            var hold = vm.Song.Plan.Holds()[0];
            vm.ApplySnapshotForTests(Snapshot(vm, hold.AtSec, holding: true, holdSec: 23.4));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("HOLD", perform.FindControl<TextBlock>("NowLabel")!.Text);
            Assert.Equal("0:23", perform.FindControl<TextBlock>("Holding")!.Text);
            Assert.Equal("Holding for 0:23", vm.HoldText);
            Assert.Equal("on GO", perform.FindControl<TextBlock>("NextCountdown")!.Text);
            Assert.Equal("GO", vm.PlayLabel);
            Assert.Equal("HOLD", vm.Status);
            Shot(window, "perform-3-hold");

            // Service lock: stop and next song need a hold; a tile needs a second tap (WF-11).
            vm.ApplySnapshotForTests(Snapshot(vm, 6));
            vm.IsServiceLock = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(perform.FindControl<HoldButton>("StopKey")!.HoldRequired);
            Assert.True(perform.FindControl<HoldButton>("NextSongKey")!.HoldRequired);
            Assert.True(perform.FindControl<MiniTimeline>("Mini")!.Locked);

            var chorus = vm.Tiles.First(t => t.Label == "Chorus");
            await vm.TapTileAsync(chorus);
            Dispatcher.UIThread.RunJobs();
            Assert.True(chorus.Armed);
            Assert.False(vm.IsPending);
            Shot(window, "perform-4-service-lock-armed");
            await vm.TapTileAsync(chorus);
            Assert.False(chorus.Armed);
            Assert.True(vm.IsPending);

            // Prepare is back with Ctrl+P and the Prepare layout is intact.
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsPerform);
            Assert.False(perform.IsVisible);
            Assert.NotNull(window.CaptureRenderedFrame());
            window.Close();
        }, TestContext.Current.CancellationToken);

        List<string> problems;
        lock (sink.Messages)
        {
            problems = sink.Messages
                .Where(m => m.StartsWith("Error", StringComparison.Ordinal)
                         || (m.Contains("Binding", StringComparison.Ordinal) && !m.EndsWith("Value is null.", StringComparison.Ordinal)))
                .ToList();
        }

        Assert.True(problems.Count == 0, "Avalonia reported:\n" + string.Join("\n", problems.Take(30)));
    }

    [Fact]
    public async Task A_hold_button_acts_only_after_it_has_been_held_for_the_hold_time()
    {
        await Session.Dispatch(() =>
        {
            var pressed = 0;
            var button = new HoldButton { Content = "Stop", HoldRequired = true, Width = 100, Height = 56 };
            button.Click += (_, _) => pressed++;
            var window = new Window { Content = button, Width = 200, Height = 100 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var center = new Point(50, 28);

            // A tap is too short.
            window.MouseDown(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(80);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, pressed);
            Assert.Equal(0, button.HoldProgress);

            // Held for the hold time: it acts once.
            window.MouseDown(center, MouseButton.Left);
            var end = DateTime.UtcNow.AddMilliseconds(900);
            while (DateTime.UtcNow < end)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, pressed);

            // Without the requirement it is an ordinary button.
            button.HoldRequired = false;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, pressed);
            window.Close();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_page_turner_keys_act_in_Perform_and_Settings_Controls_learns_a_key_and_renders()
    {
        var sink = new CollectingSink();
        await HeadlessApp.RunAsync(async () =>
        {
            Logger.Sink = sink;
            var store = SaveLibrary();
            var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
            await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing),
                new AvaloniaDispatcher(), new AcceptingDialogs(), new SpeechModel(store.Root, allowDownload: false));

            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
            window.Show();
            await vm.InitializeAsync();
            await vm.LoadEntryAsync(vm.Setlist.Rows[0].Id);
            vm.IsPerform = true;
            vm.ApplySnapshotForTests(Snapshot(vm, 6));
            Dispatcher.UIThread.RunJobs();

            // The rehearsal helpers are on the status row in Rehearsal lock and gone in Service lock.
            var perform = window.FindControl<PerformView>("Perform")!;
            Assert.True(perform.FindControl<ToggleButton>("LoopKey")!.IsVisible);
            Assert.True(perform.FindControl<ComboBox>("PracticeSelect")!.IsVisible);
            vm.PracticePercent = 85;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Practice 85%", perform.FindControl<TextBlock>("PracticeBadgeText")!.Text);
            Shot(window, "perform-5-rehearsal");

            // PageDown: the next plan item (with a change pending, as Immediately would be at once).
            vm.Timing = JumpTiming.Immediately;
            window.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsPending);

            // PageUp cancels it.
            window.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsPending);

            // Settings › Controls: the rows, Learn on a key, and the playback options render.
            var controls = new RECompose.App.Dialogs.ControlsWindow { DataContext = vm.Controls, Width = 640, Height = 640 };
            controls.Show();
            Dispatcher.UIThread.RunJobs();
            var go = vm.Controls.Rows.First(r => r.Action == RECompose.Core.Controls.ControlAction.Go);
            Assert.Contains("G", go.Inputs);
            go.LearnCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(go.Learning);
            Assert.Equal("Press a key or a pedal…", go.LearnLabel);
            Shot(controls, "controls-1-learning");

            controls.KeyPressQwerty(PhysicalKey.F9, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(go.Learning);
            Assert.Contains("F9", go.Inputs);
            Assert.Equal("Not bound", vm.Controls.Rows.First(r => r.Action == RECompose.Core.Controls.ControlAction.Vamp).Inputs.Replace("V", "Not bound"));
            controls.Close();
            window.Close();
        }, TestContext.Current.CancellationToken);

        List<string> problems;
        lock (sink.Messages)
        {
            problems = sink.Messages
                .Where(m => m.StartsWith("Error", StringComparison.Ordinal)
                         || (m.Contains("Binding", StringComparison.Ordinal) && !m.EndsWith("Value is null.", StringComparison.Ordinal)))
                .ToList();
        }

        Assert.True(problems.Count == 0, "Avalonia reported:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(30)));
    }
}
