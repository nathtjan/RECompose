// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RECompose.App.Dialogs;
using RECompose.Core.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App;

public partial class MainWindow : Window
{
    // Drags inside the app: a library song onto the service (SL-5), a service row to a new place (SL-4).
    private static readonly DataFormat<string> SongFormat = DataFormat.CreateInProcessFormat<string>("recompose-song");
    private static readonly DataFormat<string> EntryFormat = DataFormat.CreateInProcessFormat<string>("recompose-entry");

    private (PointerPressedEventArgs Args, Avalonia.Point At, DataFormat<string> Format, string Id)? _dragStart;

    public MainWindow()
    {
        InitializeComponent();

        // Shortcuts (KEY-1) are handled before controls see the key, so Space never also
        // presses a focused button.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            // Releases matter for bindings with a long press (LIVE-54).
            if (ViewModel is { IsPerform: true } vm && ControlKeys.Name(e) is { } name)
                vm.Controls.Key(name, false);
        }, RoutingStrategies.Tunnel);

        // Drag a stems folder onto the window to import it (SL-50).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Library.DoubleTapped += (_, _) => ViewModel?.LoadSelectedCommand.Execute(null);
        Library.ContextRequested += OpenLibraryMenu;
        WatchDragStart(Library, SongFormat, item => (item as SongItemViewModel)?.Id);

        // The service (SL-4): double-click or Enter loads, right-click for the menu, drag to reorder, drop songs in.
        ServiceRows.DoubleTapped += (_, _) => ViewModel?.Setlist.LoadSelectedCommand.Execute(null);
        ServiceRows.KeyDown += (_, e) =>
        {
            if (ViewModel is not { } vm)
                return;
            switch (e.Key)
            {
                case Key.Enter:
                    vm.Setlist.LoadSelectedCommand.Execute(null);
                    break;
                case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                    vm.Setlist.MoveUpCommand.Execute(null);
                    break;
                case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                    vm.Setlist.MoveDownCommand.Execute(null);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        };
        ServiceRows.ContextRequested += OpenServiceRowMenu;
        WatchDragStart(ServiceRows, EntryFormat, item => (item as SetlistRowViewModel)?.Id);
        ServiceRows.AddHandler(DragDrop.DragOverEvent, OnServiceDragOver);
        ServiceRows.AddHandler(DragDrop.DropEvent, OnServiceDrop);
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

        Timeline.SeekRequested += seconds =>
        {
            if (ViewModel is { } vm)
                _ = vm.TimelineAsHeard ? vm.SeekPlanAsync(seconds) : vm.SeekAsync(seconds);
        };

        // Arrangement lane (as heard): select, open, reorder, copy, trim (ARR-24, AT-30).
        Timeline.EntrySelected += id => ViewModel?.Entry.SelectById(id);
        Timeline.EntryActivated += id =>
        {
            if (ViewModel is { } vm)
            {
                vm.Entry.SelectById(id);
                vm.SelectedTab = MainViewModel.EntryTab;
            }
        };
        Timeline.EntryMoved += (id, index, copy) =>
        {
            if (copy)
                ViewModel?.Entry.DuplicateTo(id, index);
            else
                ViewModel?.Entry.MoveEntry(id, index);
        };
        Timeline.EntryTrimmed += (id, from, to) => ViewModel?.Entry.TrimEntry(id, from, to);
        Timeline.EntryMenuRequested += OpenEntryMenu;

        // Sections lane (SEC-50).
        Timeline.SectionSelected += id => ViewModel?.Sections.Select(id);
        Timeline.SectionMoved += (id, seconds, beats) => ViewModel?.Sections.MoveStart(id, seconds, beats);
        Timeline.SectionRenameRequested += id =>
        {
            if (ViewModel is { } vm)
            {
                vm.SelectedTab = MainViewModel.SectionsTab;
                vm.Sections.RequestRename(id);
            }
        };
        Timeline.SectionMenuRequested += OpenSectionMenu;

        // Sections table: names and starts are committed on Enter or when the field loses focus (SEC-51).
        SectionRows.AddHandler(LostFocusEvent, (_, e) => CommitCell(e.Source as TextBox), RoutingStrategies.Bubble);
        SectionRows.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is TextBox box && CommitCell(box))
            {
                Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && e.Source is TextBox escaped && escaped.DataContext is SectionRowViewModel row)
            {
                row.Name = row.Info.Section.Name;
                row.StartText = row.Info.StartSec.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                Focus();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        SplitButton.Click += (_, _) => ViewModel?.Sections.SplitAtPlayhead();
        ReviewRenameButton.Click += (_, _) => ViewModel?.Sections.ReviewRename();
        ReviewTypeButton.Click += (_, _) => ViewModel?.Sections.ReviewChangeType();

        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.Sections.FocusNameRequested += id => FocusCell(id, "section-name");
                vm.Sections.FocusTypeRequested += id => FocusCell(id, "section-type");
            }
        };
    }

    private static bool CommitCell(TextBox? box)
    {
        if (box?.DataContext is not SectionRowViewModel row)
            return false;
        switch (box.Tag as string)
        {
            case "section-name":
                row.CommitName();
                return true;
            case "section-start":
                row.CommitStart();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Focuses a row's name field or opens its Type select (+ At playhead, double-click, R and T in review).</summary>
    private void FocusCell(string id, string tag)
    {
        if (ViewModel is not { } vm || vm.Sections.Rows.FirstOrDefault(r => r.Id == id) is not { } row)
            return;
        vm.SelectedTab = MainViewModel.SectionsTab;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SectionRows.ScrollIntoView(row);
            if (SectionRows.ContainerFromItem(row) is not Control container)
                return;
            var target = container.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Tag as string == tag);
            switch (target)
            {
                case TextBox box:
                    box.Focus();
                    box.SelectAll();
                    break;
                case ComboBox combo:
                    combo.Focus();
                    combo.IsDropDownOpen = true;
                    break;
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Right-click on a section block: Rename, Change type, Split at playhead, Merge with next, Delete (SEC-50).</summary>
    private void OpenSectionMenu(string id)
    {
        if (ViewModel is not { } vm)
            return;
        var sections = vm.Sections;

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        var types = new MenuItem { Header = "Change type" };
        foreach (var choice in SectionTypeChoice.All)
            types.Items.Add(Item(choice.Name, () => sections.SetTypeOf(id, choice.Type)));

        var menu = new ContextMenu();
        menu.Items.Add(Item("Rename…", () =>
        {
            vm.SelectedTab = MainViewModel.SectionsTab;
            sections.RequestRename(id);
        }));
        menu.Items.Add(types);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Split at playhead", sections.SplitAtPlayhead));
        menu.Items.Add(Item("Merge with next", () => sections.MergeWithNext(id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => sections.DeleteById(id)));
        menu.Open(Timeline);
    }

    /// <summary>Right-click on an arrangement block: duplicate, move, remove, open options.</summary>
    private void OpenEntryMenu(string id)
    {
        if (ViewModel is not { } vm)
            return;
        var entry = vm.Entry;
        entry.SelectById(id);

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Options…", () => vm.SelectedTab = MainViewModel.EntryTab));
        menu.Items.Add(Item("Duplicate", () => entry.DuplicateCommand.Execute(null)));
        menu.Items.Add(Item("Move earlier", () => entry.MoveUpCommand.Execute(null)));
        menu.Items.Add(Item("Move later", () => entry.MoveDownCommand.Execute(null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", () => entry.RemoveCommand.Execute(null)));
        menu.Open(Timeline);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>A pad key: start it, crossfade to it, or fade it out (SL-42).</summary>
    private void OnPadKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton { Tag: int pitchClass } && ViewModel is { } vm)
            _ = vm.Pads.PressAsync(pitchClass);
    }

    /// <summary>A transition chip between two service rows: the popover (SL-21).</summary>
    private void OnTransitionChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entryId } && ViewModel is { } vm)
            _ = vm.EditTransitionAsync(entryId);
    }

    // ---- Menus (SL-4, SL-56) ------------------------------------------------------------------------------------

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Right-click on a service row: Load, Edit for this service only, Variant, Key, Transition, Move, Remove (SL-4).</summary>
    private void OpenServiceRowMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext is not SetlistRowViewModel row)
            return;
        e.Handled = true;
        vm.Setlist.Selected = row;
        var entry = row.Entry;
        var song = vm.SongOf(entry.SongId);

        var variants = new MenuItem { Header = "Variant" };
        variants.Items.Add(Item("As written", () => vm.SetEntryArrangement(row.Id, ArrangementChoice.AsWritten, "As written")));
        foreach (var variant in song?.Arrangements ?? [])
            variants.Items.Add(Item(variant.Name, () => vm.SetEntryArrangement(row.Id, ArrangementChoice.OfVariant(variant.Id), variant.Name)));

        var keys = new MenuItem { Header = "Key", IsEnabled = song is not null };
        if (song is not null)
        {
            for (var offset = -6; offset <= 6; offset++)
            {
                var o = offset;
                var item = Item(ServiceText.KeyText(song, o), () => vm.SetKeyOffset(row.Id, o));
                if (o == entry.KeyOffset)
                    item.FontWeight = Avalonia.Media.FontWeight.Bold;
                keys.Items.Add(item);
            }
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Load", () => _ = vm.LoadEntryAsync(row.Id)));
        menu.Items.Add(Item("Edit for this service only", () => vm.MakeEntryCustom(row.Id), entry.Arrangement.Type == ArrangementChoiceType.Variant));
        menu.Items.Add(variants);
        menu.Items.Add(keys);
        menu.Items.Add(Item("Transition…", () => _ = vm.EditTransitionAsync(row.Id), row.HasChip));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Move earlier", () => vm.MoveServiceEntry(row.Id, row.Index - 2), row.Index > 1));
        menu.Items.Add(Item("Move later", () => vm.MoveServiceEntry(row.Id, row.Index), row.Index < vm.Setlist.Rows.Count));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", () => vm.RemoveEntry(row.Id)));
        menu.Open(ServiceRows);
    }

    /// <summary>Right-click on a library song: Load, Add to {service} (SL-5, SL-56).</summary>
    private void OpenLibraryMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext is not SongItemViewModel song)
            return;
        e.Handled = true;
        vm.SelectedSong = song;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Load", () => _ = vm.LoadSongAsync(song.Id)));
        var service = vm.CurrentService;
        menu.Items.Add(Item(service is null ? "Add to a service" : $"Add to {service.Name}", () => vm.AddSongToService(song.Id), service is not null));
        menu.Open(Library);
    }

    // ---- Drag and drop between the library and the service (SL-4, SL-5) -----------------------------------------

    /// <summary>Starts a drag of the item under the pointer once it has moved a few pixels.</summary>
    private void WatchDragStart(ListBox list, DataFormat<string> format, Func<object?, string?> idOf)
    {
        list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _dragStart = null;
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
                return;
            if ((e.Source as Control)?.FindAncestorOfType<Button>() is not null)
                return; // a chip or a button in the row, not a drag
            if (idOf((e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext) is { } id)
                _dragStart = (e, e.GetPosition(this), format, id);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (_dragStart is not { } start || start.Format != format)
                return;
            var delta = e.GetPosition(this) - start.At;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 6)
                return;
            _dragStart = null;
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(format, start.Id));
            await DragDrop.DoDragDropAsync(start.Args, data, format == SongFormat ? DragDropEffects.Copy : DragDropEffects.Move);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnServiceDragOver(object? sender, DragEventArgs e)
    {
        if (ViewModel?.CurrentService is null)
            return;
        if (e.DataTransfer.Contains(SongFormat))
            e.DragEffects = DragDropEffects.Copy;
        else if (e.DataTransfer.Contains(EntryFormat))
            e.DragEffects = DragDropEffects.Move;
        else
            return; // files: the window imports them
        e.Handled = true;
    }

    private void OnServiceDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm || vm.CurrentService is null)
            return;
        var index = DropIndex(e);
        if (e.DataTransfer.TryGetValue(SongFormat) is { } songId)
        {
            vm.AddSongToService(songId, index);
            e.Handled = true;
        }
        else if (e.DataTransfer.TryGetValue(EntryFormat) is { } entryId)
        {
            var from = vm.CurrentService.IndexOf(entryId);
            var to = index ?? vm.Setlist.Rows.Count;
            vm.MoveServiceEntry(entryId, from >= 0 && to > from ? to - 1 : to);
            e.Handled = true;
        }
    }

    /// <summary>The 0-based position a drop lands at: before the row under the pointer, or after it on its lower half.</summary>
    private int? DropIndex(DragEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: SetlistRowViewModel row } item)
            return null;
        var lower = e.GetPosition(item).Y > item.Bounds.Height / 2;
        return row.Index - 1 + (lower ? 1 : 0);
    }

    /// <summary>A section button in the ENTRY palette: add that section to the variant (ARR-20).</summary>
    private void OnPaletteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionId })
            ViewModel?.Entry.AddSection(sectionId);
    }

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
                case Key.P:
                    vm.TogglePerformCommand.Execute(null);
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
                case Key.D when vm.SelectedTab == MainViewModel.EntryTab && FocusManager?.GetFocusedElement() is not TextBox:
                    vm.Entry.DuplicateCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Up or Key.Down when FocusManager?.GetFocusedElement() is not TextBox:
                    vm.StepKey(e.Key == Key.Up ? 1 : -1);
                    e.Handled = true;
                    return;
                case Key.Left or Key.Right when FocusManager?.GetFocusedElement() is not TextBox:
                    vm.StepTempo((e.Key == Key.Right ? 1 : -1) * (shift ? 5 : 1));
                    e.Handled = true;
                    return;
            }
            return;
        }

        // Single keys are off while a text field has focus (KEY-1).
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        // The review flow takes Enter, ←, →, R, T, Delete, N and Esc (SEC-31).
        if (vm.Sections.IsReviewing && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            Action? review = e.Key switch
            {
                Key.Enter => vm.Sections.ReviewAccept,
                Key.Left => vm.Sections.ReviewEarlier,
                Key.Right => vm.Sections.ReviewLater,
                Key.R => vm.Sections.ReviewRename,
                Key.T => vm.Sections.ReviewChangeType,
                Key.Delete or Key.Back => vm.Sections.ReviewDelete,
                Key.N => vm.Sections.ReviewNext,
                Key.Escape => vm.Sections.StopReview,
                _ => null,
            };
            if (review is not null)
            {
                review();
                e.Handled = true;
                return;
            }
        }

        if (FocusManager?.GetFocusedElement() is ComboBox { IsDropDownOpen: true })
            return;

        // Perform: the page-turner keys and whatever the MD bound (LIVE-50, LIVE-51).
        if (vm.IsPerform && ControlKeys.Name(e) is { } bound && vm.Controls.Key(bound, true))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.M:
                vm.MarkCommand.Execute(null);
                break;
            case Key.S:
                vm.Sections.SplitAtPlayhead();
                break;
            case Key.Delete when ServiceRows.IsKeyboardFocusWithin && vm.Setlist.Selected is not null:
                vm.Setlist.RemoveSelectedCommand.Execute(null);
                break;
            case Key.Delete when vm.SelectedTab == MainViewModel.EntryTab && vm.Entry.Selected is not null:
                vm.Entry.RemoveCommand.Execute(null);
                break;
            case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Alt) && vm.SelectedTab == MainViewModel.EntryTab:
                vm.Entry.MoveUpCommand.Execute(null);
                break;
            case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Alt) && vm.SelectedTab == MainViewModel.EntryTab:
                vm.Entry.MoveDownCommand.Execute(null);
                break;
            case Key.Delete when vm.SelectedTab == MainViewModel.SectionsTab && vm.Sections.Selected is { } selected:
                vm.Sections.DeleteById(selected.Id);
                break;
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
