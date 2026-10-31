// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RECompose.App.Dialogs;
using RECompose.Core.Export;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

public sealed class RenderAudioWindowTests
{
    [Fact]
    public async Task The_render_dialog_lists_the_sources_and_the_ticked_ones_become_the_options()
    {
        await HeadlessApp.RunAsync(() =>
        {
            var vm = new RenderAudioViewModel("Champion",
            [
                new("Mixes", new RenderSource(RenderSourceKind.Mix)),
                new("Outputs, as routed", new RenderSource(RenderSourceKind.Cue)),
            ]);
            var window = new RenderAudioWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, window.GetVisualDescendants().OfType<CheckBox>().Count(c => vm.Sources.Any(s => Equals(c.Content, s.Label))));
            Assert.Equal([RenderSourceKind.Mix], vm.Options().Sources.Select(s => s.Kind));
            vm.Sources[1].IsChecked = true;
            vm.RespectMute = false;

            var options = vm.Options();
            Assert.Equal([RenderSourceKind.Mix, RenderSourceKind.Cue], options.Sources.Select(s => s.Kind));
            Assert.False(options.RespectMute);
            window.Close();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }
}
