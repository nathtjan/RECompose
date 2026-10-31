// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using RECompose.Core.Assistant;

namespace RECompose.Presentation.Services;

/// <summary>
/// The Messages API through the official SDK (AS-10): 60 s per call, 2 retries for 429 and 5xx, the workspace id as a header.
/// Tests pass an <see cref="HttpClient"/> with a fake handler.
/// </summary>
public sealed class AnthropicAssistantClient : IAssistantBackend
{
    private static readonly List<ToolUnion> ToolList = AssistantPrompt.Tools.Select(t => (ToolUnion)new Tool
    {
        Name = t.Name,
        Description = t.Description,
        InputSchema = new()
        {
            Properties = t.Properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            Required = [.. t.Required],
        },
    }).ToList();

    private readonly IAnthropicClient _client;

    public AnthropicAssistantClient(string apiKey, string? workspaceId, HttpClient? http = null)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(workspaceId))
            headers["anthropic-workspace-id"] = workspaceId.Trim();
        _client = new AnthropicClient
        {
            ApiKey = apiKey,
            Timeout = TimeSpan.FromSeconds(60),
            MaxRetries = 2,
        }.WithOptions(o => o with { ExtraHeaders = headers, HttpClient = http ?? o.HttpClient });
    }

    public string ProviderName => "Anthropic";

    public async Task<AssistantReply> SendAsync(string model, IReadOnlyList<AssistantTurn> history, CancellationToken ct = default)
    {
        var message = await _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = AssistantPrompt.System,
            Messages = [.. history.Select(ToParam)],
            Tools = ToolList,
        }, ct);

        // The blocks go back as they came, thinking included, until the history is trimmed.
        List<ContentBlockParam> echo = [];
        var texts = new List<string>();
        var calls = new List<ToolCall>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var t))
            {
                echo.Add(new TextBlockParam { Text = t.Text });
                texts.Add(t.Text);
            }
            else if (block.TryPickThinking(out var thinking))
            {
                echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out var use))
            {
                echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                calls.Add(new ToolCall(use.ID, use.Name, JsonSerializer.SerializeToNode(use.Input) as JsonObject ?? []));
            }
        }

        return new AssistantReply(new ReplyTurn(texts, calls, echo), message.StopReason == "refusal");
    }

    private static MessageParam ToParam(AssistantTurn turn) => turn switch
    {
        UserTurn user => new UserMessageParam { Content = user.Texts.Select(t => (ContentBlockParam)new TextBlockParam { Text = t }).ToList() },
        ReplyTurn { Native: List<ContentBlockParam> blocks } => new AssistantMessageParam { Content = blocks },
        ReplyTurn reply => new AssistantMessageParam
        {
            Content = new List<ContentBlockParam>(
            [
                .. reply.Texts.Select(t => (ContentBlockParam)new TextBlockParam { Text = t }),
                .. reply.Calls.Select(c => (ContentBlockParam)new ToolUseBlockParam
                {
                    ID = c.Id,
                    Name = c.Name,
                    Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c.Input.ToJsonString())!,
                }),
            ]),
        },
        ResultsTurn results => new UserMessageParam
        {
            Content = new List<ContentBlockParam>(
            [
                .. results.Results.Select(r => (ContentBlockParam)new ToolResultBlockParam { ToolUseID = r.Id, Content = r.Content, IsError = r.IsError }),
                new TextBlockParam { Text = results.State },
            ]),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(turn)),
    };

    public Task TestAsync(string model, CancellationToken ct = default) => _client.Models.Retrieve(model, cancellationToken: ct);

    public string ErrorText(Exception e) => e switch
    {
        AnthropicUnauthorizedException => AssistantPrompt.KeyRejected(ProviderName),
        AnthropicIOException or HttpRequestException => AssistantPrompt.Offline(ProviderName, AssistantErrors.Innermost(e)),
        TaskCanceledException { InnerException: TimeoutException } => AssistantPrompt.Offline(ProviderName, "no answer in 60 seconds"),
        AnthropicApiException api => AssistantPrompt.ApiError(AssistantErrors.ApiMessage(api.Message)),
        _ => AssistantPrompt.ApiError(e.Message),
    };
}
