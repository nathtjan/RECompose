// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Services;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.App.Tests;

// The assistant drawer (UI-17, AS-2): Ctrl+J, the setup card without a key, a ✓ row and a Perform card, with a fake API.
public sealed class AssistantWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-app-ai-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeApi(Queue<string> replies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies.Dequeue(), Encoding.UTF8, "application/json") });
    }

    private static string Reply(string content, string stop) =>
        $$$"""{"id":"m","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[{{{content}}}],"stop_reason":"{{{stop}}}","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""";

    [Fact]
    public async Task Ctrl_J_opens_the_drawer_with_the_setup_card_then_rows_and_a_Perform_card()
    {
        var sink = new CollectingSink();
        await HeadlessApp.RunAsync(async () =>
        {
            Logger.Sink = sink;
            var store = new DataStore(Path.Combine(_root, "data"));
            var song = new Song { Id = "s", Title = "Champion", Source = new SongSource { Path = _root }, OriginalKey = "Bb", DurationSec = 100 };
            store.SaveSong(song);
            store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(song)] });
            store.SaveService(ServiceEdit.Add(ServiceEdit.New("Sunday 26 Oct", new DateTime(2026, 10, 26, 9, 0, 0)), ServiceEdit.NewEntry(song)));
            var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
            await using var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing),
                new AvaloniaDispatcher(), new AcceptingDialogs(), new SpeechModel(store.Root, allowDownload: false));
            vm.Assistant.Secrets = new MemorySecretStore();
            var replies = new Queue<string>();
            vm.Assistant.ClientFactory = (key, workspace) => new AssistantClient(key, workspace, new HttpClient(new FakeApi(replies)));

            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            await vm.InitializeAsync();
            Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.Assistant.IsOpen);
            Assert.True(vm.Assistant.NeedsKey);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Add your Anthropic API key to use the assistant" && t.IsEffectivelyVisible);

            vm.Assistant.KeyText = "sk-test";
            vm.Assistant.SaveKeyCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.Assistant.NeedsKey);
            Assert.Equal("sk-test", vm.Assistant.Secrets.Get(AssistantViewModel.KeyName));
            replies.Enqueue(Reply("""{"type":"tool_use","id":"t1","name":"set_entry_key","input":{"entry":1,"key":"C"}}""", "tool_use"));
            replies.Enqueue(Reply("""{"type":"text","text":"Song 1, Champion, is now in C."}""", "end_turn"));
            await vm.Assistant.SendAsync("he change the key of song 1 to C");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "✓ Sunday 26 Oct: Champion to C." && t.IsEffectivelyVisible);

            vm.IsPerform = true;
            replies.Enqueue(Reply("""{"type":"tool_use","id":"t2","name":"set_entry_tempo","input":{"entry":1,"percent":90}}""", "tool_use"));
            replies.Enqueue(Reply("""{"type":"text","text":"Please confirm."}""", "end_turn"));
            await vm.Assistant.SendAsync("slower");
            Dispatcher.UIThread.RunJobs();
            var apply = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apply" && b.IsEffectivelyVisible);
            apply.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(90, vm.CurrentService!.Entries[0].TempoPercent);
            Assert.NotNull(window.CaptureRenderedFrame());
            window.Close();
        }, TestContext.Current.CancellationToken);

        List<string> problems;
        lock (sink.Messages)
        {
            problems = sink.Messages
                .Where(m => m.StartsWith("Error", StringComparison.Ordinal)
                         || (m.Contains("Binding", StringComparison.Ordinal) && !m.EndsWith("Value is null.", StringComparison.Ordinal)))
                .ToList();
        }

        Assert.Empty(problems);
    }
}
