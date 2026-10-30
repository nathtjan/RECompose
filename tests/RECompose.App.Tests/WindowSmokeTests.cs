// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using RECompose.App.Controls;
using RECompose.App.Dialogs;
using RECompose.Core.Persistence;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

/// <summary>The real App on the headless platform with Skia rendering.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>One session for every window test: Avalonia can be set up only once per process.</summary>
    public static HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    /// <summary>
    /// Runs a test body on the UI thread and lets its exceptions out. Session.Dispatch returns as soon as an async body first
    /// suspends on real work (a socket, a file), and an exception in it is lost, so a test can pass having checked nothing.
    /// This keeps running the UI thread's jobs until the body ends, and fails if it has not ended in time or threw.
    /// </summary>
    public static async Task RunAsync(Func<Task> body, CancellationToken ct, int timeoutMs = 30000)
    {
        Exception? failure = null;
        var finished = false;
        await Session.Dispatch(() =>
        {
            _ = Run();

            // On a busy machine an await (the engine connect) may not complete at once: its continuation is a UI job.
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!finished && failure is null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
        }, ct);

        async Task Run()
        {
            try
            {
                await body();
                finished = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(finished, "The test body stopped before its end (an await that never resumed).");
    }
}

/// <summary>Collects warnings and errors that Avalonia logs (binding errors among them).</summary>
internal sealed class CollectingSink : ILogSink
{
    public List<string> Messages { get; } = [];

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Add(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Add(level, area, source, messageTemplate, propertyValues);

    private void Add(LogEventLevel level, string area, object? source, string template, object?[] values)
    {
        lock (Messages)
            Messages.Add($"{level} {area} [{source?.GetType().Name}] {template} | {string.Join(" | ", values.Select(v => v?.ToString()))}");
    }
}

public sealed class WindowSmokeTests : IDisposable
{

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-app-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// Errors, and binding warnings other than "Value is null": bindings such as Song.Title
    /// legitimately see a null Song while no song is loaded.
    /// </summary>
    private static List<string> Problems(CollectingSink sink)
    {
        lock (sink.Messages)
        {
            return sink.Messages
                .Where(m => m.StartsWith("Error", StringComparison.Ordinal)
                         || (m.Contains("Binding", StringComparison.Ordinal) && !m.EndsWith("Value is null.", StringComparison.Ordinal)))
                .ToList();
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

    private string Stems()
    {
        var folder = Path.Combine(_root, "Champion - Bb - 72bpm");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "Click", "Guide", "Drums", "Bass", "Keys" })
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(folder, name + ".wav")));
            const int rate = 48000, frames = 48000 * 20;
            var bytes = frames * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
            for (var i = 0; i < frames; i++)
                w.Write((short)(Math.Sin(i * 0.05) * 8000));
        }
        return folder;
    }

    [Fact]
    public async Task The_main_window_imports_loads_and_renders_a_song()
    {
        var executable = Executable();
        var sink = new CollectingSink();

        await HeadlessApp.RunAsync(async () =>
        {
            Logger.Sink = sink;
            var store = new DataStore(Path.Combine(_root, "data"));
            var dialogs = new AcceptingDialogs();
            await using var vm = new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable), new AvaloniaDispatcher(), dialogs,
                new SpeechModel(store.Root, allowDownload: false));

            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            await vm.InitializeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.CaptureRenderedFrame()); // the empty state renders

            await vm.ImportFolderAsync(Stems());
            Assert.Single(vm.Songs);
            await vm.LoadSongAsync(vm.Songs[0].Id);
            await vm.SeekAsync(10.02);

            // Let peaks arrive and the layout settle.
            for (var i = 0; i < 100 && vm.Song!.Tracks.Any(t => t.Peaks is null); i++)
            {
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
            }

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(1440, frame!.PixelSize.Width);

            var timeline = window.FindControl<TimelineView>("Timeline")!;
            Assert.Equal(TimelineView.TracksTop + 5 * TimelineView.LaneHeight, timeline.Bounds.Height, 1);
            Assert.True(timeline.Bounds.Width > 400);
            Assert.Equal("bar 4.1 / 6", vm.SongBarText); // 72 BPM 4/4: 3.33 s a bar, 20 s of audio

            // Shortcuts: F fits, + zooms, Space toggles play (no device here, so it stays put).
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.CaptureRenderedFrame());

            // M marks a section at the playhead (SEC-52); the lane, the table and NOW/NEXT render.
            Assert.False(vm.HasSections);
            window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(vm.Sections.Rows);
            Assert.Equal("SECTION 1", vm.NowLabel);
            Assert.Equal("END OF SONG", vm.NextLabel);
            Assert.NotNull(window.CaptureRenderedFrame());

            await vm.SeekAsync(3.4);
            window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, vm.Sections.Rows.Count);
            Assert.Equal("SECTION 2", vm.NowLabel);
            Assert.Equal("SECTION 1", vm.NextLabel);
            Assert.NotNull(window.CaptureRenderedFrame());

            vm.SelectedTab = MainViewModel.MixerTab;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.CaptureRenderedFrame());
            vm.SelectedTab = MainViewModel.SongTab;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.CaptureRenderedFrame());
            window.Close();
        }, TestContext.Current.CancellationToken);

        var problems = Problems(sink);
        Assert.True(problems.Count == 0, "Avalonia reported:\n" + string.Join("\n", problems.Take(30)));
    }

    [Fact]
    public async Task The_dialogs_open_and_render()
    {
        var executable = Executable();
        var sink = new CollectingSink();

        await HeadlessApp.RunAsync(async () =>
        {
            Logger.Sink = sink;
            var tools = new EngineTools(executable);
            var scan = await new ImportService(new DataStore(Path.Combine(_root, "data")), tools).ScanAsync(Stems(), []);

            var review = new ImportReviewWindow { DataContext = new ImportReviewViewModel(scan, 4) };
            review.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(review.CaptureRenderedFrame());
            review.Close();

            var audio = new AudioSettingsWindow
            {
                DataContext = new AudioSettingsViewModel(
                    [new DeviceTypeInfo("Windows Audio", ["Speakers", "Focusrite USB"]), new DeviceTypeInfo("ASIO", ["Focusrite USB ASIO"])],
                    new DeviceInfo("ASIO", "Focusrite USB ASIO", 48000, 256, ["Out 1", "Out 2", "Out 3", "Out 4"], [44100, 48000], [128, 256, 512], 312),
                    new Core.Settings.AudioSettings()),
                MaxHeight = 480, // a short screen: the settings scroll, Apply stays in view
            };
            audio.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(audio.CaptureRenderedFrame());
            var apply = audio.FindControl<Button>("ApplyButton")!;
            Assert.True(apply.TranslatePoint(new Avalonia.Point(0, apply.Bounds.Height), audio)!.Value.Y <= audio.Bounds.Height);
            audio.Close();
        }, TestContext.Current.CancellationToken);

        var problems = Problems(sink);
        Assert.True(problems.Count == 0, "Avalonia reported:\n" + string.Join("\n", problems.Take(30)));
    }

    [Fact]
    public async Task A_dialog_has_our_title_bar_with_its_title_and_a_close_key()
    {
        if (OperatingSystem.IsMacOS())
            Assert.Skip("macOS keeps the system title bar.");

        await HeadlessApp.RunAsync(() =>
        {
            var dialog = new DialogWindow { Title = "Delete song", Width = 460, Content = new TextBlock { Text = "Body" } };
            var closed = false;
            dialog.Closed += (_, _) => closed = true;
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            var parts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).ToList();
            Assert.Contains(parts.OfType<TextBlock>(), t => t.Text == "Delete song");
            Assert.Contains(parts.OfType<TextBlock>(), t => t.Text == "Body");
            var close = parts.OfType<Button>().Single(b => b.Name == "CloseButton");
            Assert.DoesNotContain(parts.OfType<Button>(), b => b.Name is "MinimiseButton" or "MaximiseButton");
            close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(closed);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }
}

internal sealed class AcceptingDialogs : IDialogs
{
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);
    public Task<string?> PickOpenFileAsync(string title, string extension) => Task.FromResult<string?>(null);
    public Task<bool> ReviewImportAsync(ImportReviewViewModel review) => Task.FromResult(true);
    public Task<DuplicateChoice> AskDuplicateAsync(string existingTitle) => Task.FromResult(DuplicateChoice.Cancel);
    public Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings) => Task.FromResult(false);

    public Task EditControlsAsync(ControlsViewModel controls) => Task.CompletedTask;

    public Task ShowServiceCheckAsync(ServiceCheckViewModel check) => Task.CompletedTask;

    public Task<ResumeChoice> AskResumeAsync(ResumeQuestion question) => Task.FromResult(ResumeChoice.DontResume);
    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(true);
    public Task<ServiceDetails?> AskServiceDetailsAsync(string title, string confirmLabel, ServiceDetails initial) => Task.FromResult<ServiceDetails?>(initial);
    public Task<VariantScopeAnswer?> AskVariantScopeAsync(string variantName, int otherEntries) => Task.FromResult<VariantScopeAnswer?>(new VariantScopeAnswer(VariantScope.ThisService, false));
    public Task<string?> AskNameAsync(string title, string label, string initial, string confirmLabel) => Task.FromResult<string?>(initial);
    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> choices, string confirmLabel) => Task.FromResult<int?>(0);
    public Task<TrackEdit?> EditTrackAsync(TrackEdit initial, IReadOnlyList<string> outputs) => Task.FromResult<TrackEdit?>(null);
    public Task<bool> EditTransitionAsync(TransitionViewModel transition) => Task.FromResult(false);
}
