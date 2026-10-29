// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RECompose.Core.Library;
using RECompose.Core.Persistence;
using RECompose.Core.Sections;
using RECompose.Core.Services;
using RECompose.Core.Timing;
using RECompose.Engine;
using RECompose.Presentation.Services;
using RECompose.Presentation.ViewModels;

namespace RECompose.Presentation.Tests;

// The assistant panel against a fake Messages API (AS-10…50): the tool loop, ✓ rows, undo groups, Perform cards and errors.
// No engine and no network.
public sealed class AssistantTests : IDisposable
{
    private static readonly TempoGrid A = new(72, 3, 0.02);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rec-assistant-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceDialogs _dialogs = new();
    private readonly FakeApi _api = new();

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

    private sealed class FakeApi : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Replies { get; } = new();
        public List<(HttpRequestMessage Request, JsonObject? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject;
            Requests.Add((request, body));
            var (status, reply) = Replies.Count > 0 ? Replies.Dequeue() : (HttpStatusCode.InternalServerError, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }

        public void Reply(JsonArray content, string stop) => Replies.Enqueue((HttpStatusCode.OK, new JsonObject
        {
            ["id"] = $"msg_{Replies.Count}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-sonnet-5-5",
            ["content"] = content,
            ["stop_reason"] = stop,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
        }.ToJsonString()));

        public void Tool(string name, JsonObject input) =>
            Reply([new JsonObject { ["type"] = "tool_use", ["id"] = $"toolu_{Replies.Count}", ["name"] = name, ["input"] = input }], "tool_use");

        public void Text(string text) => Reply([new JsonObject { ["type"] = "text", ["text"] = text }], "end_turn");
    }

    private async Task<MainViewModel> SundayAsync()
    {
        var store = new DataStore(Path.Combine(_root, "data"));
        var sections = new (string Name, int Bar)[] { ("Intro", 1), ("Verse 1", 11), ("Chorus", 27), ("Verse 2", 49), ("Chorus", 65), ("Ending", 117) }
            .Select(s => Section.Create(s.Name, A.BarToTime(s.Bar), SectionOrigin.Guide)).ToList();
        var champion = new Song
        {
            Id = "champion",
            Title = "Champion",
            Source = new SongSource { Path = _root },
            OriginalKey = "Bb",
            Bpm = 72,
            BeatsPerBar = 3,
            FirstDownbeatSec = 0.02,
            DurationSec = 310.02,
            Sections = sections,
            Tracks = [new Track { Id = "drums", Name = "drums", File = Path.Combine(_root, "d.wav"), Group = TrackGroups.Drums }],
        };
        store.SaveSong(champion);
        store.SaveLibrary(new LibraryIndex { Songs = [SongSummary.From(champion)] });

        var missing = Path.Combine(_root, "no-engine", EnginePaths.ExecutableFileName);
        var vm = new MainViewModel(store, new EngineHostOptions(missing, Path.Combine(_root, "e.sock")), new EngineTools(missing), new LockedDispatcher(), _dialogs,
            new SpeechModel(store.Root, allowDownload: false));
        await vm.InitializeAsync();
        _dialogs.Details = new ServiceDetails("Sunday 26 Oct", new DateTime(2026, 10, 26, 9, 0, 0));
        await vm.NewServiceAsync();
        vm.AddSongToService("champion");

        vm.Assistant.Secrets = new MemorySecretStore();
        vm.Assistant.Secrets.Set(AssistantViewModel.KeyName, "sk-test");
        vm.Assistant.ClientFactory = (key, workspace) => new AssistantClient(key, workspace, new HttpClient(_api));
        return vm;
    }

    private static string Rows(AssistantViewModel a) => string.Join(" | ", a.Rows.Select(r => r.Text));

    [Fact]
    public async Task AS60_a_key_change_is_one_undo_group_with_a_tool_row()
    {
        var vm = await SundayAsync();
        vm.Assistant.WorkspaceId = "wrkspc_1";
        _api.Tool("set_entry_key", new JsonObject { ["entry"] = 1, ["key"] = "C" });
        _api.Tool("set_entry_tempo", new JsonObject { ["entry"] = 1, ["percent"] = 95 });
        _api.Text("Song 1, Champion, is now in C, up a whole step from B♭, at 95 %.");

        await vm.Assistant.SendAsync("he change the key of song 1 to C and slow it a bit");

        Assert.Equal("he change the key of song 1 to C and slow it a bit | ✓ Sunday 26 Oct: Champion to C. | ✓ Sunday 26 Oct: Champion at 95%. | Song 1, Champion, is now in C, up a whole step from B♭, at 95 %.", Rows(vm.Assistant));
        var entry = vm.CurrentService!.Entries[0];
        Assert.Equal((2, 95), (entry.KeyOffset, entry.TempoPercent));

        // The requests: the fixed system prompt, every tool, the state after the message and after each round of results.
        Assert.Equal(3, _api.Requests.Count);
        var (request, body) = _api.Requests[0];
        Assert.Equal("wrkspc_1", request.Headers.GetValues("anthropic-workspace-id").Single());
        Assert.Equal(21, body!["tools"]!.AsArray().Count);
        Assert.DoesNotContain("<state>", body["system"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("<state>", body["messages"]![0]!["content"]![1]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var results = _api.Requests[1].Body!["messages"]![2]!["content"]!.AsArray();
        Assert.Equal("tool_result", results[0]!["type"]!.GetValue<string>());
        Assert.StartsWith("{\"ok\":true,\"summary\":\"Sunday 26 Oct: Champion to C.\"", results[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("\"key\":\"C\"", results[1]!["text"]!.GetValue<string>(), StringComparison.Ordinal); // the state after the change

        // One message = one undo step for both changes (AS-40).
        vm.Assistant.UndoCommand.Execute(null);
        entry = vm.CurrentService!.Entries[0];
        Assert.Equal((0, 100), (entry.KeyOffset, entry.TempoPercent));
        Assert.StartsWith("↶ Undid: Sunday 26 Oct: Champion to C.", vm.Assistant.Rows[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AS45_in_Perform_a_change_waits_for_Apply_and_the_model_hears_how_it_ended()
    {
        var vm = await SundayAsync();
        vm.IsPerform = true;
        _api.Tool("set_entry_tempo", new JsonObject { ["entry"] = 1, ["percent"] = 90 });
        _api.Text("I asked you to confirm the tempo change.");

        await vm.Assistant.SendAsync("song 1 at 90 percent");

        var card = vm.Assistant.Rows.Single(r => r.IsCard);
        Assert.Equal("Sunday 26 Oct: Champion at 90%.", card.Text);
        Assert.True(card.IsPending);
        Assert.Equal(100, vm.CurrentService!.Entries[0].TempoPercent);
        Assert.Contains("pending_confirmation", _api.Requests[1].Body!["messages"]![2]!["content"]![0]!["content"]!.ToJsonString(), StringComparison.Ordinal);

        card.ApplyCommand!.Execute(null);
        Assert.Equal(90, vm.CurrentService!.Entries[0].TempoPercent);
        Assert.Equal("Applied", card.Status);

        _api.Text("Done.");
        await vm.Assistant.SendAsync("thanks");
        var last = _api.Requests[^1].Body!["messages"]!.AsArray()[^1]!["content"]!.AsArray();
        Assert.Equal("The MD applied: Sunday 26 Oct: Champion at 90%.", last[1]!["text"]!.GetValue<string>());

        vm.Assistant.ConfirmInPerform = false;
        _api.Tool("set_entry_tempo", new JsonObject { ["entry"] = 1, ["percent"] = 80 });
        _api.Text("Changes are off in Perform.");
        await vm.Assistant.SendAsync("song 1 at 80");
        Assert.Equal(90, vm.CurrentService!.Entries[0].TempoPercent);
        Assert.Contains("not allowed in Perform", _api.Requests[^1].Body!["messages"]!.AsArray()[^1]!["content"]![0]!["content"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AS1_the_key_round_trips_through_the_OS_credential_store()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "The Keychain needs a signed-in macOS user");
        var store = new OsSecretStore(_root);
        var name = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(store.Get(name));
            store.Set(name, "sk-ant-é1");
            Assert.Equal("sk-ant-é1", store.Get(name));
        }
        finally
        {
            store.Set(name, null);
        }

        Assert.Null(store.Get(name));
    }

    [Fact]
    public async Task AS50_errors_are_bubbles_and_are_not_sent_again()
    {
        var vm = await SundayAsync();
        _api.Replies.Enqueue((HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));

        await vm.Assistant.SendAsync("make it short");

        Assert.Equal("⊗ The Anthropic API key was rejected. Update it in Settings > Assistant.", vm.Assistant.Rows[^1].Text);
        Assert.False(vm.Assistant.IsWorking);

        _api.Replies.Enqueue((HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"model: not found"}}"""));
        await vm.Assistant.SendAsync("again");
        Assert.Equal("⊗ Assistant error: model: not found", vm.Assistant.Rows[^1].Text);

        _api.Text("Hello.");
        await vm.Assistant.SendAsync("hi");
        Assert.Single(_api.Requests[^1].Body!["messages"]!.AsArray()); // failed messages left the history

        vm.Assistant.Secrets.Set(AssistantViewModel.KeyName, null);
        vm.Assistant.SaveKeyCommand.Execute(null);
        Assert.True(vm.Assistant.NeedsKey);
        var count = _api.Requests.Count;
        await vm.Assistant.SendAsync("no key");
        Assert.Equal(count, _api.Requests.Count);
    }
}
