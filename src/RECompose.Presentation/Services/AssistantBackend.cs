// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;

namespace RECompose.Presentation.Services;

/// <summary>A model's request to run one tool.</summary>
public sealed record ToolCall(string Id, string Name, JsonObject Input);

/// <summary>What a tool returned, sent back to the model; an error when the tool failed.</summary>
public sealed record ToolResult(string Id, string Content, bool IsError);

/// <summary>
/// One message of the assistant conversation, the same for every provider; each backend turns the history into its own
/// request (AS-20).
/// </summary>
public abstract record AssistantTurn;

/// <summary>What the user typed, then notes on cards the MD acted on, then the state snapshot.</summary>
public sealed record UserTurn(IReadOnlyList<string> Texts) : AssistantTurn;

/// <summary>
/// The model's answer: its words and the tools it called. <see cref="Native"/> is the provider's own form of it when that
/// carries more than words and calls (Anthropic's thinking), replayed as it came; null once trimmed.
/// </summary>
public sealed record ReplyTurn(IReadOnlyList<string> Texts, IReadOnlyList<ToolCall> Calls, object? Native = null) : AssistantTurn;

/// <summary>The tools' results for the previous reply, and the state after them.</summary>
public sealed record ResultsTurn(IReadOnlyList<ToolResult> Results, string State) : AssistantTurn;

/// <param name="Refused">The model declined the request.</param>
public sealed record AssistantReply(ReplyTurn Turn, bool Refused);

/// <summary>A model provider behind the assistant (AS-10): one request per round of the tool loop.</summary>
public interface IAssistantBackend
{
    /// <summary>"Anthropic", "OpenAI": named in error bubbles.</summary>
    string ProviderName { get; }

    Task<AssistantReply> SendAsync(string model, IReadOnlyList<AssistantTurn> history, CancellationToken ct = default);

    /// <summary>Settings › Assistant › Test connection: checks the key and the model.</summary>
    Task TestAsync(string model, CancellationToken ct = default);

    /// <summary>The AS-50 bubble for a failed call.</summary>
    string ErrorText(Exception e);
}

internal static class AssistantErrors
{
    public static string Innermost(Exception e)
    {
        while (e.InnerException is not null)
            e = e.InnerException;
        return e.Message;
    }

    /// <summary>error.message from a response body inside <paramref name="text"/> (both APIs put it there), or the text itself.</summary>
    public static string ApiMessage(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start >= 0)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text[start..]);
                if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object
                    && error.TryGetProperty("message", out var message))
                    return message.GetString() ?? text;
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return text;
    }
}
