// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia;
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
        var pads = new PadSet { Id = "ob", Name = "OB Pad", Folder = _root, Samples = Enumerable.Range(0, 12).ToDictionary(k => k, k => Path.Combine(_root, $"pad{k}.wav")) };
        store.SavePads(new PadLibrary { PadSets = [pads], CurrentId = "ob" });
        return store;
    }

    [Fact]
    public async Task The_service_sidebar_entry_settings_and_mixer_presets_render()
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

            // The sidebar pads (UI-12): twelve keys, the song's key marked, a key press without an engine doesn't throw.
            Assert.True(vm.Pads.HasSet);
            Assert.Equal(12, vm.Pads.Keys.Count);
            Assert.Equal("B♭", vm.Pads.Keys.Single(k => k.IsSongKey).Label); // the entry is back at B♭ after Ctrl+↑
            await vm.Pads.PressAsync(3);
            Assert.True(vm.Pads.Keys[3].IsPlaying);
            await vm.Pads.PressAsync(3);
            Assert.False(vm.Pads.IsPlaying);
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

    [Fact]
    public async Task Picking_a_variant_with_the_mouse_changes_the_entry_without_breaking_the_select()
    {
        await HeadlessApp.RunAsync(async () =>
        {
            var store = SaveLibrary();
            var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
            await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing),
                new AvaloniaDispatcher(), new AcceptingDialogs(), new SpeechModel(store.Root, allowDownload: false));

            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
            window.Show();
            await vm.InitializeAsync();
            await vm.LoadEntryAsync(vm.Setlist.Rows[0].Id);
            vm.SelectedTab = MainViewModel.EntryTab;
            Dispatcher.UIThread.RunJobs();

            // The select refreshes the entry from inside its own selection change (the list it shows must survive that).
            var select = window.FindControl<ComboBox>("VariantSelect")!;
            Assert.Equal("Short", vm.Entry.SelectedVariant?.Name);
            Pick(select, 0);
            Assert.Equal("As written", vm.Entry.SelectedVariant?.Name);
            Assert.Equal(ArrangementChoiceType.AsWritten, vm.LoadedEntry!.Arrangement.Type);
            Pick(select, 1);
            Assert.Equal("Short", vm.Entry.SelectedVariant?.Name);
            Assert.Equal("Short · default", (select.SelectedItem as VariantChoice)?.Display);

            // From a custom arrangement: picking a variant drops "Custom for this service" from the list it was picked in.
            vm.MakeEntryCustom(vm.LoadedEntry!.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, select.ItemCount);
            Assert.Equal(EntryArrangement.CustomName, (select.SelectedItem as VariantChoice)?.Name);
            Pick(select, 0);
            Assert.Equal(ArrangementChoiceType.AsWritten, vm.LoadedEntry!.Arrangement.Type);
            Assert.Equal(2, select.ItemCount);
            Assert.Equal("As written", (select.SelectedItem as VariantChoice)?.Name);
            window.Close();

            void Pick(ComboBox combo, int index)
            {
                combo.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                var item = (Control)combo.ContainerFromIndex(index)!;
                var root = TopLevel.GetTopLevel(item)!;
                var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
                root.MouseDown(at, MouseButton.Left);
                root.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }
        }, TestContext.Current.CancellationToken);
    }
}
