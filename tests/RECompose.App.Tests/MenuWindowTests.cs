// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using RECompose.Core.Persistence;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

// PLAT-20: the menu's structure, behind the Menu button on Windows and Linux (the macOS menu bar has the same groups).
public sealed class MenuWindowTests
{
    [Fact]
    public async Task The_menu_button_holds_the_PLAT20_groups_and_every_item_has_a_command()
    {
        var root = Path.Combine(Path.GetTempPath(), "rec-app-menu-" + Guid.NewGuid().ToString("N"));
        await HeadlessApp.RunAsync(async () =>
        {
            var store = new DataStore(Path.Combine(root, "data"));
            var missing = Path.Combine(root, "no-engine", EnginePaths.ExecutableFileName);
            await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(root, "e.sock")), new EngineTools(missing),
                new AvaloniaDispatcher(), new AcceptingDialogs(), new SpeechModel(store.Root, allowDownload: false));
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // macOS: the native menu bar; elsewhere the Menu button's flyout.
            List<(string Header, List<System.Windows.Input.ICommand?> Commands)> groups;
            if (OperatingSystem.IsMacOS())
            {
                Assert.False(window.FindControl<Button>("AppMenuButton")!.IsVisible);
                groups = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>()
                    .Select(g => (g.Header!, g.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Command).ToList())).ToList();
            }
            else
            {
                var button = window.FindControl<Button>("AppMenuButton")!;
                Assert.True(button.IsVisible);
                groups = ((MenuFlyout)button.Flyout!).ItemsSource!.Cast<MenuItem>()
                    .Select(g => ((string)g.Header!, g.ItemsSource!.OfType<MenuItem>().Select(i => i.Command).ToList())).ToList();
            }

            Assert.Equal(["File", "Edit", "Song", "Service", "Transport", "View", "Window", "Help"], groups.Select(g => g.Header));
            Assert.All(groups.SelectMany(g => g.Commands), Assert.NotNull);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
