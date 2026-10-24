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

    private MainViewModel? ViewModel => DataContext as MainViewModel;

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

        switch (e.Key)
        {
            case Key.M:
                vm.MarkCommand.Execute(null);
                break;
            case Key.S:
                vm.Sections.SplitAtPlayhead();
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
