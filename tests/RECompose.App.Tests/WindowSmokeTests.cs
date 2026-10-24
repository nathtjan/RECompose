// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
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
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

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

        await Session.Dispatch(async () =>
        {
            Logger.Sink = sink;
            var store = new DataStore(Path.Combine(_root, "data"));
            var dialogs = new AcceptingDialogs();
            await using var vm = new MainViewModel(store, new EngineHostOptions(executable, _endpoint) { IdleExitSeconds = 3 }, new EngineTools(executable), new AvaloniaDispatcher(), dialogs);

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
            Assert.Equal(TimelineView.RulerHeight + 5 * TimelineView.LaneHeight, timeline.Bounds.Height, 1);
            Assert.True(timeline.Bounds.Width > 400);
            Assert.Equal("bar 4.1 / 6", vm.SongBarText); // 72 BPM 4/4: 3.33 s a bar, 20 s of audio

            // Shortcuts: F fits, + zooms, Space toggles play (no device here, so it stays put).
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.CaptureRenderedFrame());

            vm.SelectedTab = 1; // SONG
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.CaptureRenderedFrame());
            window.Close();
        }, TestContext.Current.CancellationToken);

        var problems = sink.Messages.Where(m => m.StartsWith("Error", StringComparison.Ordinal) || m.Contains("Binding", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, "Avalonia reported:\n" + string.Join("\n", problems.Take(30)));
    }

    [Fact]
    public async Task The_dialogs_open_and_render()
    {
        var executable = Executable();
        var sink = new CollectingSink();

        await Session.Dispatch(async () =>
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
            };
            audio.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(audio.CaptureRenderedFrame());
            audio.Close();
        }, TestContext.Current.CancellationToken);

        var problems = sink.Messages.Where(m => m.StartsWith("Error", StringComparison.Ordinal) || m.Contains("Binding", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, "Avalonia reported:\n" + string.Join("\n", problems.Take(30)));
    }
}

internal sealed class AcceptingDialogs : IDialogs
{
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    public Task<bool> ReviewImportAsync(ImportReviewViewModel review) => Task.FromResult(true);
    public Task<DuplicateChoice> AskDuplicateAsync(string existingTitle) => Task.FromResult(DuplicateChoice.Cancel);
    public Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings) => Task.FromResult(false);
}
