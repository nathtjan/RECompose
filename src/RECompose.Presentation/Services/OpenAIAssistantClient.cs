// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using RECompose.Core.Assistant;

namespace RECompose.Presentation.Services;

/// <summary>
/// The OpenAI Chat Completions API with function tools through the official SDK (AS-10): 60 s per call, 2 retries for 429
/// and 5xx. Tests pass an <see cref="HttpClient"/> with a fake handler.
/// </summary>
public sealed class OpenAIAssistantClient : IAssistantBackend
{
    private static readonly List<ChatTool> ToolList = AssistantPrompt.Tools.Select(t => ChatTool.CreateFunctionTool(
        t.Name,
        t.Description,
        BinaryData.FromString(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = t.Properties.DeepClone(),
            ["required"] = new JsonArray([.. t.Required.Select(r => (JsonNode)r)]),
        }.ToJsonString()))).ToList();

    private ChatClient? _chat;
    private string? _chatModel;
    private readonly OpenAIModelClient _models;
    private readonly ApiKeyCredential _key;
    private readonly OpenAIClientOptions _options;

    public OpenAIAssistantClient(string apiKey, HttpClient? http = null)
    {
        _key = new ApiKeyCredential(apiKey);
        _options = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(60),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 2),
        };
        if (http is not null)
            _options.Transport = new HttpClientPipelineTransport(http);
        _models = new OpenAIModelClient(_key, _options);
    }

    public string ProviderName => "OpenAI";

    public async Task<AssistantReply> SendAsync(string model, IReadOnlyList<AssistantTurn> history, CancellationToken ct = default)
    {
        // A chat client is bound to one model; the model can change between messages.
        if (_chat is null || _chatModel != model)
            (_chat, _chatModel) = (new ChatClient(model, _key, _options), model);
        var chat = _chat;
        var options = new ChatCompletionOptions { MaxOutputTokenCount = 16000 };
        foreach (var tool in ToolList)
            options.Tools.Add(tool);

        List<ChatMessage> messages = [new SystemChatMessage(AssistantPrompt.System), .. history.SelectMany(ToMessages)];
        ChatCompletion completion = await chat.CompleteChatAsync(messages, options, ct);

        var texts = completion.Content.Select(p => p.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
        var calls = completion.ToolCalls.Select(c => new ToolCall(c.Id, c.FunctionName, Arguments(c.FunctionArguments))).ToList();
        return new AssistantReply(new ReplyTurn(texts, calls), !string.IsNullOrEmpty(completion.Refusal));
    }

    /// <summary>The model's arguments; text that isn't a JSON object gives none, and the tool says what is missing.</summary>
    private static JsonObject Arguments(BinaryData? json)
    {
        try
        {
            return JsonNode.Parse(json?.ToString() is { Length: > 0 } text ? text : "{}") as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    // A reply's tool calls must be followed at once by one tool message each; the state comes after them.
    private static IEnumerable<ChatMessage> ToMessages(AssistantTurn turn)
    {
        switch (turn)
        {
            case UserTurn user:
                yield return new UserChatMessage(user.Texts.Select(ChatMessageContentPart.CreateTextPart));
                break;
            case ReplyTurn { Calls.Count: 0 } reply:
                yield return new AssistantChatMessage(string.Join("\n\n", reply.Texts));
                break;
            case ReplyTurn reply:
                var assistant = new AssistantChatMessage(reply.Calls.Select(c =>
                    ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(c.Input.ToJsonString()))));
                foreach (var text in reply.Texts)
                    assistant.Content.Add(ChatMessageContentPart.CreateTextPart(text));
                yield return assistant;
                break;
            case ResultsTurn results:
                foreach (var r in results.Results)
                    yield return new ToolChatMessage(r.Id, r.Content);
                yield return new UserChatMessage(results.State);
                break;
        }
    }

    public Task TestAsync(string model, CancellationToken ct = default) => _models.GetModelAsync(model, ct);

    public string ErrorText(Exception e) => e switch
    {
        ClientResultException { Status: 401 } => AssistantPrompt.KeyRejected(ProviderName),
        ClientResultException { Status: 0 } => AssistantPrompt.Offline(ProviderName, AssistantErrors.Innermost(e)), // no answer at all
        ClientResultException api => AssistantPrompt.ApiError(AssistantErrors.ApiMessage(api.GetRawResponse()?.Content.ToString() ?? api.Message)),
        HttpRequestException => AssistantPrompt.Offline(ProviderName, AssistantErrors.Innermost(e)),
        TaskCanceledException { InnerException: TimeoutException } => AssistantPrompt.Offline(ProviderName, "no answer in 60 seconds"),
        _ => AssistantPrompt.ApiError(e.Message),
    };
}
