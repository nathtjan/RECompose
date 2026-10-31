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
    private const uint NcDown = 0x00A1, NcUp = 0x00A2, ClientUp = 0x0202, MouseMove = 0x0200;
    private const nint OnClose = 20, OnCaption = 2;

    [Fact]
    public void A_click_on_the_close_key_closes_down_to_the_screen_corner_and_a_press_dragged_off_it_does_not()
    {
        // Pressed and released on the key, the corner pixel of a maximised window included: closes.
        var down = MainWindow.CloseKeyClick(false, NcDown, OnClose);
        Assert.Equal((true, false, true), down);
        Assert.Equal((false, true, true), MainWindow.CloseKeyClick(down.Pressed, NcUp, OnClose));

        // Pressed on it and released on the title bar or in the window: nothing closes and the release goes on as usual.
        Assert.Equal((false, false, false), MainWindow.CloseKeyClick(true, NcUp, OnCaption));
        Assert.Equal((false, false, false), MainWindow.CloseKeyClick(true, ClientUp, 0));

        // Released on it without having been pressed there: nothing closes.
        Assert.Equal((false, false, true), MainWindow.CloseKeyClick(false, NcUp, OnClose));

        // Other messages leave the press as it is.
        Assert.Equal((true, false, false), MainWindow.CloseKeyClick(true, MouseMove, 0));
    }

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
