// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RECompose.App.Controls;
using RECompose.Core.Persistence;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

// The window draws its own title bar: the top bars carry the window keys (macOS keeps its traffic lights).
public sealed class WindowChromeTests
{
    [Fact]
    public async Task The_top_bar_has_window_keys_that_minimise_maximise_and_restore()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-app-chrome-" + Guid.NewGuid().ToString("N"));
        await HeadlessApp.RunAsync(async () =>
        {
            var store = new DataStore(Path.Combine(root, "data"));
            var missing = Path.Combine(root, "no-engine", EnginePaths.ExecutableFileName);
            await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(root, "e.sock")), new EngineTools(missing),
                new AvaloniaDispatcher(), new AcceptingDialogs(), new SpeechModel(store.Root, allowDownload: false));
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.ExtendClientAreaToDecorationsHint);
            var keys = window.GetVisualDescendants().OfType<CaptionButtons>().ToList();
            Assert.NotEmpty(keys);
            if (OperatingSystem.IsMacOS())
            {
                Assert.All(keys, k => Assert.False(k.IsVisible));
                window.Close();
                return;
            }

            var buttons = keys.First(k => k.IsEffectivelyVisible).Children.Cast<Button>().ToList();
            Assert.Equal([WindowDecorationsElementRole.MinimizeButton, WindowDecorationsElementRole.MaximizeButton, WindowDecorationsElementRole.CloseButton],
                buttons.Select(WindowDecorationProperties.GetElementRole));

            void Press(Button b)
            {
                b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
            }

            Press(buttons[1]);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal("Restore", AutomationProperties.GetName(buttons[1]));
            Press(buttons[1]);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal("Maximise", AutomationProperties.GetName(buttons[1]));
            Press(buttons[0]);
            Assert.Equal(WindowState.Minimized, window.WindowState);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
