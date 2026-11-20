// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using RECompose.App.Dialogs;
using RECompose.Presentation.Services;

namespace RECompose.App.Tests;

// An error the app did not expect shows its details, which can be copied; one that ends the app is shown at the next start.
public sealed class ErrorDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-app-err-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // Windows: a file still open
        {
        }
    }

    [Fact]
    public void An_error_that_ends_the_app_is_kept_for_the_next_start_once()
    {
        AppLog.Start(_root);
        var details = App.ErrorDetails(new InvalidOperationException("The drop failed"));
        Assert.Contains("RECompose ", details, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: The drop failed", details, StringComparison.Ordinal);

        AppLog.Crash(details);
        Assert.Contains("Errors: Unhandled: RECompose", File.ReadAllText(Directory.GetFiles(Path.Combine(_root, "logs"), "app-*.log").Single()), StringComparison.Ordinal);
        Assert.Equal(details, AppLog.TakeCrash());
        Assert.Null(AppLog.TakeCrash());
    }

    [Fact]
    public async Task The_error_window_shows_and_copies_the_details()
    {
        await HeadlessApp.RunAsync(async () =>
        {
            const string details = "RECompose 0.2.0 on macOS\n\nSystem.InvalidOperationException: The drop failed";
            var window = ErrorDialog.Create("Something went wrong", "RECompose hit an error it did not expect.", details);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Button ButtonOf(string text) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text);
            var shown = window.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "Details");

            var box = (Control)shown.Parent!;
            Assert.False(box.IsVisible); // the details are folded away at first
            ButtonOf("Show details").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(box.IsVisible);
            Assert.Equal(details, shown.Text);
            Assert.NotNull(window.CaptureRenderedFrame());

            ButtonOf("Copy details").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(details, await window.Clipboard!.TryGetTextAsync());
            ButtonOf("Copied");

            ButtonOf("Close").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
        }, TestContext.Current.CancellationToken);
    }
}
