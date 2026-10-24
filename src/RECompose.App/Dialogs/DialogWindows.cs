// SPDX-License-Identifier: AGPL-3.0-or-later
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

public partial class AudioSettingsWindow : Window
{
    public AudioSettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(false);
        this.FindControl<Button>("ApplyButton")!.Click += (_, _) => Close(true);
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

    public async Task<bool> ReviewImportAsync(ImportReviewViewModel review)
    {
        var window = new ImportReviewWindow { DataContext = review };
        return await window.ShowDialog<bool>(owner);
    }

    public async Task<bool> EditAudioSettingsAsync(AudioSettingsViewModel settings)
    {
        var window = new AudioSettingsWindow { DataContext = settings };
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
