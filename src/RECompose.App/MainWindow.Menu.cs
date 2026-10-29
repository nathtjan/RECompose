// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows.Input;
using Avalonia.Controls;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.ViewModels;

namespace RECompose.App;

/// <summary>
/// The menu (PLAT-20): the macOS menu bar, and on Windows the same structure behind the Menu button in the top bar. The
/// items run the commands the window already has; the shortcuts stay with the window's key handling.
/// </summary>
public partial class MainWindow
{
    internal static IReadOnlyList<(string Header, (string Header, ICommand? Command)[] Items)> MenuGroups(MainViewModel vm, Window window) =>
    [
        ("File", [("Import stems folder…", vm.ImportCommand), ("-", null), ("Settings…", vm.OpenSettingsCommand), ("Keys and pedals…", vm.OpenControlsCommand)]),
        ("Edit", [("Undo", vm.UndoCommand), ("Redo", vm.RedoCommand)]),
        ("Song", [("Load", vm.LoadSelectedCommand), ("Mark section", vm.MarkCommand), ("Add cue", vm.AddCueCommand), ("-", null),
            ("Key up", vm.KeyUpCommand), ("Key down", vm.KeyDownCommand), ("Tempo up", vm.TempoUpCommand), ("Tempo down", vm.TempoDownCommand), ("-", null),
            ("Re-analyse click", vm.ReanalyseClickCommand), ("Transcribe guide again", vm.RetranscribeCommand), ("Restore earlier version…", vm.RestoreSongVersionCommand)]),
        ("Service", [("New service…", vm.Setlist.NewServiceCommand), ("Duplicate service…", vm.Setlist.DuplicateServiceCommand), ("Rename…", vm.Setlist.RenameServiceCommand), ("-", null),
            ("Service check", vm.OpenCheckCommand), ("Restore earlier version…", vm.Setlist.RestoreVersionCommand), ("-", null),
            ("Export service…", vm.Setlist.ExportServiceCommand), ("Import service…", vm.Setlist.ImportServiceCommand), ("Export report", vm.ExportReportCommand)]),
        ("Transport", [("Play or pause", vm.PlayPauseCommand), ("Stop", vm.StopCommand), ("To start", vm.ToStartCommand), ("-", null), ("PANIC", vm.PanicCommand)]),
        ("View", [("Prepare or Perform", vm.TogglePerformCommand), ("Assistant", vm.ToggleAssistantCommand)]),
        ("Window", [("Minimise", new RelayCommand(() => window.WindowState = WindowState.Minimized)),
            ("Full screen", new RelayCommand(() => window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen))]),
        ("Help", [("RECompose on GitHub", new RelayCommand(() => window.Launcher.LaunchUriAsync(new Uri(Presentation.Services.AppUpdates.Repository))))]),
    ];

    private void BuildMenu(MainViewModel vm)
    {
        var groups = MenuGroups(vm, this);
        if (OperatingSystem.IsMacOS())
        {
            var bar = new NativeMenu();
            foreach (var (header, items) in groups)
            {
                var menu = new NativeMenu();
                foreach (var (text, command) in items)
                    menu.Add(text == "-" ? new NativeMenuItemSeparator() : new NativeMenuItem(text) { Command = command });
                bar.Add(new NativeMenuItem(header) { Menu = menu });
            }

            NativeMenu.SetMenu(this, bar);
            return;
        }

        AppMenuButton.Flyout = new MenuFlyout
        {
            ItemsSource = groups.Select(g => new MenuItem
            {
                Header = g.Header,
                ItemsSource = g.Items.Select(i => i.Header == "-" ? (Control)new Separator() : new MenuItem { Header = i.Header, Command = i.Command }).ToList(),
            }).ToList(),
        };
        AppMenuButton.IsVisible = true;
    }
}
