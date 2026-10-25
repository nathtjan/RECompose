// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using RECompose.App.Dialogs;
using RECompose.Core.Arrangements;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

// The service sidebar, the ENTRY tab's entry settings, the mixer presets and the transition popover (UI-11, UI-12,
// UI-16, SL-21, PB-81) render without binding errors. No engine is needed: the view model runs without audio.
public sealed class ServiceWindowTests : IDisposable
{
    private static HeadlessUnitTestSession Session => HeadlessApp.Session;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-app-svc-" + Guid.NewGuid().ToString("N"));

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
        var sections = new[] { ("Intro", 1), ("Verse 1", 11), ("Chorus", 27), ("Ending", 41) }
            .Select(s => Section.Create(s.Item1, grid.BarToTime(s.Item2), SectionOrigin.Guide)).ToList();
        var infos = SectionList.Describe(sections, grid, 110);
        var song = new Song
        {
            Id = "champion",
            Title = "Champion",
            Source = new SongSource { Path = _root },
            OriginalKey = "Bb",
            Bpm = 72,
            BeatsPerBar = 3,
            FirstDownbeatSec = 0.02,
            DurationSec = 110,
            Sections = sections,
            Arrangements = [new Arrangement { Id = "short", Name = "Short", Entries = [ArrangementEntry.ForSection(infos[0].Id), ArrangementEntry.ForSection(infos[2].Id)] }],
            DefaultArrangementId = "short",
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
        service = ServiceEdit.Add(service, ServiceEdit.NewEntry(song) with
        {
            KeyOffset = -1,
            TransitionAfter = new Transition { Type = TransitionType.Pad, PadKey = "Db" },
            Notes = "Big ending",
        });
        service = ServiceEdit.Add(service, ServiceEdit.NewEntry(other));
        store.SaveService(service);
        return store;
    }

    [Fact]
    public async Task The_service_sidebar_entry_settings_and_mixer_presets_render()
    {
        var sink = new CollectingSink();
        await Session.Dispatch(async () =>
        {
            Logger.Sink = sink;
            var store = SaveLibrary();
            var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
            await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing),
                new AvaloniaDispatcher(), new AcceptingDialogs(), new SpeechModel(store.Root, allowDownload: false));

            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
            window.Show();
            await vm.InitializeAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Sunday 26 Oct · 9:00", vm.Setlist.Title);
            Assert.Equal(2, vm.Setlist.Rows.Count);
            Assert.Equal("Pad in D♭ · click 72", vm.Setlist.Rows[0].ChipText);
            Assert.NotNull(window.CaptureRenderedFrame());

            await vm.LoadEntryAsync(vm.Setlist.Rows[0].Id);
            vm.Setlist.Selected = vm.Setlist.Rows[0];
            vm.SelectedTab = MainViewModel.EntryTab;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Editing: Champion · service entry 1", window.FindControl<TextBlock>("EditingHeader")!.Text);
            Assert.Equal("A (−1)", window.FindControl<TextBlock>("HeaderKey")!.Text);
            Assert.NotNull(window.CaptureRenderedFrame());

            // Ctrl+↑ raises the key of the selected entry (KEY-1).
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, vm.LoadedEntry!.KeyOffset);

            // The selected entry isn't the loaded one: "not loaded" and Load.
            vm.Setlist.Selected = vm.Setlist.Rows[1];
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.Setlist.IsSelectionNotLoaded);
            Assert.NotNull(window.CaptureRenderedFrame());

            vm.SelectedTab = MainViewModel.MixerTab;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Default", vm.CurrentMixPreset?.Name);
            Assert.Equal("Default", (window.FindControl<ComboBox>("MixPresetSelect")!.SelectedItem as MixPresetChoice)?.Name); // the select shows it too
            Assert.NotNull(window.CaptureRenderedFrame());

            // Library › Pads (SL-47): the switch and the empty list render.
            vm.PadsKey = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<ItemsControl>("PadSetList")!.IsEffectivelyVisible);
            Assert.NotNull(window.CaptureRenderedFrame());
            vm.SongsKey = true;

            var transition = new TransitionWindow
            {
                DataContext = new TransitionViewModel(new Transition { Type = TransitionType.Pad }, "Champion", "Goodness", RECompose.Core.Music.Key.Parse("Db"), 68),
            };
            transition.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("At the end, the pad fades in with the click. Press Play and the next song starts, and the pad fades out.",
                transition.FindControl<TextBlock>("Explanation")!.Text);
            Assert.NotNull(transition.CaptureRenderedFrame());
            ((TransitionViewModel)transition.DataContext!).Type = TransitionTypeChoice.All.Single(c => c.Type == TransitionType.Segue);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Goodness starts by itself right after Champion ends.", transition.FindControl<TextBlock>("Explanation")!.Text);
            Assert.NotNull(transition.CaptureRenderedFrame());
            transition.Close();
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
}
