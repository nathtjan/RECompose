// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Dialogs;

public partial class ImportReviewWindow : Window
{
    public ImportReviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(false);
        this.FindControl<Button>("ImportButton")!.Click += (_, _) => Close(true);
    }
}

public partial class TransitionWindow : Window
{
    public TransitionWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(false);
        this.FindControl<Button>("SaveButton")!.Click += (_, _) => Close(true);
        this.FindControl<Button>("ClearButton")!.Click += (_, _) => (DataContext as TransitionViewModel)?.Clear();
    }
}

public partial class AudioSettingsWindow : Window
{
    public AudioSettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(false);
        this.FindControl<Button>("ApplyButton")!.Click += (_, _) => Close(true);
    }
}

public partial class ControlsWindow : Window
{
    public ControlsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();

        // Learn takes the next key pressed here (LIVE-53); pedals arrive through the engine.
        Avalonia.Input.Key? learned = null;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is ControlsViewModel controls && controls.IsLearning)
            {
                if (e.Key == Avalonia.Input.Key.Escape)
                    controls.CancelLearn();
                else if (ControlKeys.Name(e) is { } name)
                    controls.Key(name, true);
                learned = e.Key;
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Its release too, or a learned Space clicks the focused Learn button again.
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key == learned)
            {
                learned = null;
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closed += (_, _) => (DataContext as ControlsViewModel)?.CancelLearn();
    }
}

/// <summary>The app's dialogs and pickers for the view models.</summary>
public sealed class AvaloniaDialogs(Window owner) : IDialogs
{
    public async Task<string?> PickFolderAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Import a stems folder",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("RECompose service") { Patterns = ["*" + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title, string extension)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("RECompose service") { Patterns = ["*" + extension] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ReviewImportAsync(ImportReviewViewModel review)
    {
        var window = new ImportReviewWindow { DataContext = review };
        return await window.ShowDialog<bool>(owner);
    }

    /// <summary>REL-31: what happened, and four ways on.</summary>
    public async Task<ResumeChoice> AskResumeAsync(ResumeQuestion question)
    {
        var window = new Window
        {
            Title = "RECompose closed unexpectedly",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var buttons = new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        void Add(string text, ResumeChoice choice, bool primary = false, bool enabled = true)
        {
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = enabled };
            if (primary)
                button.Classes.Add("primary");
            button.Click += (_, _) => window.Close(choice);
            AutomationProperties.SetName(button, text);
            buttons.Children.Add(button);
        }

        if (question.CurrentLabel is not null)
            Add($"Resume at {question.CurrentLabel}", ResumeChoice.ResumeCurrent, primary: true);
        if (question.NextLabel is not null)
            Add($"Resume at the next section ({question.NextLabel})", ResumeChoice.ResumeNext);
        if (question.CanOpenService)
            Add("Open the service", ResumeChoice.OpenService);
        Add("Don't resume", ResumeChoice.DontResume);

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { new TextBlock { Text = question.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        return await window.ShowDialog<ResumeChoice>(owner);
    }

    /// <summary>REL-1: the service check as a list of marks, details and Fix buttons.</summary>
    public async Task ShowServiceCheckAsync(ServiceCheckViewModel check)
    {
        var window = new Window
        {
            Title = "Service check",
            Width = 720,
            Height = 560,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = check,
        };

        var verdict = new TextBlock { FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold };
        verdict.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ServiceCheckViewModel.Verdict)));
        var rows = new StackPanel { Spacing = 8 };

        void Fill()
        {
            rows.Children.Clear();
            foreach (var item in check.Items)
            {
                var line = new DockPanel { Margin = new Thickness(0, 2) };
                var mark = new TextBlock { Text = item.Mark, Width = 24, FontWeight = Avalonia.Media.FontWeight.Bold };
                DockPanel.SetDock(mark, Dock.Left);
                line.Children.Add(mark);
                if (item.HasFix)
                {
                    var fix = new Button { Content = item.FixLabel, Command = item.FixCommand, Margin = new Thickness(8, 0, 0, 0) };
                    DockPanel.SetDock(fix, Dock.Right);
                    line.Children.Add(fix);
                }

                line.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = item.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                        new TextBlock { Text = item.Detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 },
                    },
                });
                rows.Children.Add(line);
            }
        }

        check.Items.CollectionChanged += (_, _) => Fill();
        Fill();

        var run = new Button { Content = "Run check", Command = check.RunCommand };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => window.Close();
        window.Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { run, close } }.Dock(Dock.Bottom),
                verdict.Dock(Dock.Top),
                new ScrollViewer { Content = rows, Margin = new Thickness(0, 12) },
            },
        };
        await window.ShowDialog(owner);
    }

    public async Task EditControlsAsync(ControlsViewModel controls)
    {
        var window = new ControlsWindow { DataContext = controls };
        await window.ShowDialog(owner);
    }

    public async Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings)
    {
        var window = new AudioSettingsWindow { DataContext = settings };
        // Taller than a small screen: cap it there and scroll the settings, with Apply always in view.
        if (owner.Screens.ScreenFromTopLevel(owner) is { } screen)
            window.MaxHeight = screen.WorkingArea.Height / screen.Scaling - 48;
        return await window.ShowDialog<bool>(owner);
    }

    /// <summary>UI-60: a confirmation with Cancel and the action named on its button.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirm = new Button { Content = confirmLabel, Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        confirm.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);

        var heading = new TextBlock { Text = title };
        heading.Classes.Add("title");
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                heading,
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm },
                },
            },
        };

        return await window.ShowDialog<bool>(owner);
    }

    public async Task<bool> EditTransitionAsync(TransitionViewModel transition)
    {
        var window = new TransitionWindow { DataContext = transition };
        return await window.ShowDialog<bool>(owner);
    }

    /// <summary>SL-2: name, date and time of a service. An empty date makes an undated service.</summary>
    public async Task<ServiceDetails?> AskServiceDetailsAsync(string title, string confirmLabel, ServiceDetails initial)
    {
        var window = Dialog(title);
        var name = new TextBox { Text = initial.Name, PlaceholderText = "Sunday 26 Oct" };
        AutomationProperties.SetName(name, "Service name");
        var date = new CalendarDatePicker { SelectedDate = initial.Date?.Date, PlaceholderText = "No date", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(date, "Date");
        var time = new TextBox { Text = initial.Date?.ToString("H:mm", CultureInfo.InvariantCulture) ?? "9:00", Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(time, "Time");
        var problem = new TextBlock { IsVisible = false };
        problem.Classes.Add("meta");
        if (Application.Current?.FindResource("state-warning") is Avalonia.Media.IBrush warning)
            problem.Foreground = warning;

        var ok = new Button { Content = confirmLabel, Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ServiceDetails? result = null;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                name.Focus();
                return;
            }

            DateTime? when = null;
            if (date.SelectedDate is { } day)
            {
                var t = TimeSpan.FromHours(9);
                if (!string.IsNullOrWhiteSpace(time.Text) && !TimeSpan.TryParseExact(time.Text.Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out t))
                {
                    problem.Text = "Write the time as 9:00 or 18:30.";
                    problem.IsVisible = true;
                    return;
                }

                when = day.Date + t;
            }

            result = new ServiceDetails(name.Text.Trim(), when);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 10 };
        grid.Children.Add(Label("NAME", 0));
        grid.Children.Add(Cell(name, 0));
        grid.Children.Add(Label("DATE", 1));
        grid.Children.Add(Cell(date, 1));
        grid.Children.Add(Label("TIME", 2));
        grid.Children.Add(Cell(time, 2));

        window.Content = Body(title, null, [grid, problem], cancel, ok);
        window.Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>ARR-12: "Change the Short variant for every service, or only for this one?"</summary>
    public async Task<VariantScopeAnswer?> AskVariantScopeAsync(string variantName, int otherEntries)
    {
        var window = Dialog("Change a variant");
        var others = otherEntries switch
        {
            0 => "No other service entry plays it yet.",
            1 => "1 other service entry plays it.",
            _ => $"{otherEntries} other service entries play it.",
        };
        var remember = new CheckBox { Content = $"Remember this choice for {variantName}" };
        var service = new Button { Content = "This service only", Classes = { "primary" }, IsDefault = true };
        var all = new Button { Content = "All services" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        VariantScopeAnswer? result = null;
        service.Click += (_, _) => { result = new VariantScopeAnswer(VariantScope.ThisService, remember.IsChecked == true); window.Close(); };
        all.Click += (_, _) => { result = new VariantScopeAnswer(VariantScope.AllServices, remember.IsChecked == true); window.Close(); };
        cancel.Click += (_, _) => window.Close();

        window.Content = Body("Change a variant",
            $"Change the {variantName} variant for every service, or only for this service? {others}",
            [remember], cancel, all, service);
        await window.ShowDialog(owner);
        return result;
    }

    public async Task<string?> AskNameAsync(string title, string label, string initial, string confirmLabel)
    {
        var window = Dialog(title);
        var box = new TextBox { Text = initial };
        AutomationProperties.SetName(box, label);
        var ok = new Button { Content = confirmLabel, Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        string? result = null;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
                return;
            result = box.Text.Trim();
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        window.Content = Body(title, null, [box], cancel, ok);
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await window.ShowDialog(owner);
        return result;
    }

    public async Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> choices, string confirmLabel)
    {
        var window = Dialog(title);
        var list = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(list, title);
        var ok = new Button { Content = confirmLabel, Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        int? result = null;
        ok.Click += (_, _) => { result = list.SelectedIndex; window.Close(); };
        cancel.Click += (_, _) => window.Close();

        window.Content = Body(title, message, [list], cancel, ok);
        await window.ShowDialog(owner);
        return result;
    }

    private static Window Dialog(string title) => new()
    {
        Title = title,
        Width = 460,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static StackPanel Body(string title, string? message, IEnumerable<Control> content, params Button[] buttons)
    {
        var heading = new TextBlock { Text = title };
        heading.Classes.Add("title");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        panel.Children.Add(heading);
        if (message is not null)
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var control in content)
            panel.Children.Add(control);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        foreach (var button in buttons)
            row.Children.Add(button);
        panel.Children.Add(row);
        return panel;
    }

    private static TextBlock Label(string text, int row)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("label");
        Grid.SetRow(label, row);
        return label;
    }

    private static Control Cell(Control control, int row)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        return control;
    }

    /// <summary>SL-55: "Already in the library as {title}." Open it · Import anyway · Cancel.</summary>
    public async Task<DuplicateChoice> AskDuplicateAsync(string existingTitle)
    {
        var window = new Window
        {
            Title = "Already in the library",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var open = new Button { Content = "Open it", Classes = { "primary" }, IsDefault = true };
        var anyway = new Button { Content = "Import anyway" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        open.Click += (_, _) => window.Close(DuplicateChoice.OpenExisting);
        anyway.Click += (_, _) => window.Close(DuplicateChoice.ImportAnyway);
        cancel.Click += (_, _) => window.Close(DuplicateChoice.Cancel);

        var title = new TextBlock { Text = "Already in the library" };
        title.Classes.Add("title");
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                title,
                new TextBlock { Text = $"These stems are already in the library as {existingTitle}.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, anyway, open },
                },
            },
        };

        return await window.ShowDialog<DuplicateChoice>(owner);
    }
}

internal static class DockExtensions
{
    public static T Dock<T>(this T control, Avalonia.Controls.Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
