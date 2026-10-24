// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RECompose.Presentation.ViewModels;

namespace RECompose.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Shortcuts (KEY-1) are handled before controls see the key, so Space never also
        // presses a focused button.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);

        // Drag a stems folder onto the window to import it (SL-50).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Library.DoubleTapped += (_, _) => ViewModel?.LoadSelectedCommand.Execute(null);
        Library.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ViewModel?.LoadSelectedCommand.Execute(null);
                e.Handled = true;
            }
        };

        // Double-click a fader for 0.0 dB (PB-80).
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        Timeline.SeekRequested += seconds => _ = ViewModel?.SeekAsync(seconds);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Shortcuts with Ctrl/⌘ work everywhere.
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.I:
                    vm.ImportCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Z:
                    (shift ? vm.RedoCommand : vm.UndoCommand).Execute(null);
                    e.Handled = true;
                    return;
                case Key.Y:
                    vm.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    vm.OpenSettingsCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.F:
                    Search.Focus();
                    e.Handled = true;
                    return;
            }
            return;
        }

        // Single keys are off while a text field has focus (KEY-1).
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        switch (e.Key)
        {
            case Key.Space:
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left:
                _ = vm.StepBarAsync(-1);
                break;
            case Key.Right:
                _ = vm.StepBarAsync(1);
                break;
            case Key.Home:
                vm.ToStartCommand.Execute(null);
                break;
            case Key.F:
                Timeline.Fit();
                break;
            case Key.OemPlus:
            case Key.Add:
                Timeline.ZoomIn();
                break;
            case Key.OemMinus:
            case Key.Subtract:
                Timeline.ZoomOut();
                break;
            case Key.OemPeriod:
                vm.FollowPlayhead = !vm.FollowPlayhead;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control source)
            return;
        var slider = source as Slider ?? source.FindAncestorOfType<Slider>();
        if (slider?.Tag as string == "fader" && slider.DataContext is TrackViewModel track)
        {
            track.GainDb = 0;
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || e.DataTransfer.TryGetFiles() is not { Length: > 0 } items)
            return;
        e.Handled = true;

        foreach (var item in items)
        {
            if (item.TryGetLocalPath() is not { } path)
                continue;
            // A folder imports as one song; a file imports its folder.
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is not null)
            {
                try
                {
                    await vm.ImportFolderAsync(folder);
                }
                catch (Exception ex)
                {
                    vm.Banner = ex.Message;
                }
            }
        }
    }
}
