// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RECompose.App.Dialogs;
using RECompose.Core.Library;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

// SL-47: the pad set review shows every key, the missing one with a way to make it, and stays open on a bad value.
public sealed class PadSetWindowTests
{
    [Fact]
    public async Task The_pad_review_shows_twelve_keys_and_saves_only_valid_values()
    {
        await HeadlessApp.RunAsync(() =>
        {
            var files = Enumerable.Range(0, 11).Select(k => $"/pads/OB Pad {k}.wav").ToList();
            var set = new PadSet { Id = "ob", Name = "OB Pad", Folder = "/pads", Samples = Enumerable.Range(0, 11).ToDictionary(k => k, k => files[k]) };
            var vm = new PadSetEditViewModel(set, null, isImport: true, "This looks like a pad set.", canImportAsSong: true,
                _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null));
            var window = new PadSetWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("D♭", texts);
            Assert.Contains("No file", texts);
            Assert.Equal(12, window.GetVisualDescendants().OfType<Button>().Count(b => Equals(b.Content, "Replace…")));
            var fill = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible);
            Assert.Equal("Make from C, a semitone down", fill.Content);
            Assert.True(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SongButton").IsEffectivelyVisible);

            // A bad volume keeps the window open with the error.
            vm.GainText = "loud";
            var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveButton");
            save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.NotNull(vm.Error);
            window.Close();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }
}
