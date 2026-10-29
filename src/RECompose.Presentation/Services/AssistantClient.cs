// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using RECompose.Core.Assistant;

namespace RECompose.Presentation.Services;

/// <summary>
/// The Messages API through the official SDK (AS-10): 60 s per call, 2 retries for 429 and 5xx, the workspace id as a header.
/// Tests pass an <see cref="HttpClient"/> with a fake handler.
/// </summary>
public sealed class AssistantClient
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

    public AssistantClient(string apiKey, string? workspaceId, HttpClient? http = null)
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

    public Task<Message> SendAsync(string model, IReadOnlyList<MessageParam> messages, CancellationToken ct = default) =>
        _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = AssistantPrompt.System,
            Messages = [.. messages],
            Tools = ToolList,
        }, ct);

    /// <summary>Settings › Assistant › Test connection: the model's details, which checks the key and the workspace.</summary>
    public Task TestAsync(string model, CancellationToken ct = default) => _client.Models.Retrieve(model, cancellationToken: ct);

    /// <summary>The AS-50 bubble for a failed call.</summary>
    public static string ErrorText(Exception e) => e switch
    {
        AnthropicUnauthorizedException => AssistantPrompt.KeyRejected,
        AnthropicIOException or HttpRequestException => AssistantPrompt.Offline(Inner(e)),
        TaskCanceledException { InnerException: TimeoutException } => AssistantPrompt.Offline("no answer in 60 seconds"),
        AnthropicApiException api => AssistantPrompt.ApiError(ApiMessage(api)),
        _ => AssistantPrompt.ApiError(e.Message),
    };

    private static string Inner(Exception e)
    {
        while (e.InnerException is not null)
            e = e.InnerException;
        return e.Message;
    }

    /// <summary>error.message from the response body, when the exception carries it.</summary>
    private static string ApiMessage(AnthropicApiException e)
    {
        var text = e.Message;
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..]);
                if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                    return message.GetString() ?? text;
            }
            catch (JsonException)
            {
            }
        }

        return text;
    }
}
