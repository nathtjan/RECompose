// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using RECompose.Core.Assistant;

namespace RECompose.Presentation.Services;

/// <summary>A failed OpenAI request: the HTTP status and error.message from the body.</summary>
public sealed class OpenAIApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The OpenAI Chat Completions API with function tools, over plain HTTP (AS-10): 60 s per call, 2 retries for 429 and 5xx.
/// Tests pass an <see cref="HttpClient"/> with a fake handler.
/// </summary>
public sealed class OpenAIAssistantClient : IAssistantBackend
{
    private static readonly Uri Api = new("https://api.openai.com/v1/");
    private const int Retries = 2;

    private static readonly JsonArray ToolList = [.. AssistantPrompt.Tools.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = t.Properties.DeepClone(),
                ["required"] = new JsonArray([.. t.Required.Select(r => (JsonNode)r)]),
            },
        },
    })];

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public OpenAIAssistantClient(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public string ProviderName => "OpenAI";

    public async Task<AssistantReply> SendAsync(string model, IReadOnlyList<AssistantTurn> history, CancellationToken ct = default)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = AssistantPrompt.System } };
        foreach (var turn in history)
            foreach (var m in ToMessages(turn))
                messages.Add(m);

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["tools"] = ToolList.DeepClone(),
            ["max_completion_tokens"] = 16000,
        };
        var reply = await RequestAsync(HttpMethod.Post, "chat/completions", body, ct);

        var message = reply["choices"]?[0]?["message"] as JsonObject ?? throw new OpenAIApiException(HttpStatusCode.OK, "The answer had no message.");
        var texts = message["content"]?.GetValue<string>() is { Length: > 0 } content ? [content] : new List<string>();
        var calls = (message["tool_calls"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(c => c["function"] is JsonObject)
            .Select(c => new ToolCall(
                c["id"]?.GetValue<string>() ?? string.Empty,
                c["function"]!["name"]?.GetValue<string>() ?? string.Empty,
                Arguments(c["function"]!["arguments"]?.GetValue<string>())))
            .ToList();
        return new AssistantReply(new ReplyTurn(texts, calls), message["refusal"]?.GetValue<string>() is { Length: > 0 });
    }

    /// <summary>The model's arguments as JSON text; text that isn't an object gives no arguments, and the tool says what is missing.</summary>
    private static JsonObject Arguments(string? json)
    {
        try
        {
            return JsonNode.Parse(json ?? "{}") as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    // A reply's tool calls must be followed at once by one "tool" message each; the state comes after them.
    private static IEnumerable<JsonObject> ToMessages(AssistantTurn turn)
    {
        switch (turn)
        {
            case UserTurn user:
                yield return new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray([.. user.Texts.Select(t => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = t })]),
                };
                break;
            case ReplyTurn reply:
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = reply.Texts.Count > 0 ? string.Join("\n\n", reply.Texts) : null };
                if (reply.Calls.Count > 0)
                {
                    assistant["tool_calls"] = new JsonArray([.. reply.Calls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Input.ToJsonString() },
                    })]);
                }

                yield return assistant;
                break;
            case ResultsTurn results:
                foreach (var r in results.Results)
                    yield return new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.Id, ["content"] = r.Content };
                yield return new JsonObject { ["role"] = "user", ["content"] = results.State };
                break;
        }
    }

    public Task TestAsync(string model, CancellationToken ct = default) => RequestAsync(HttpMethod.Get, $"models/{Uri.EscapeDataString(model)}", null, ct);

    private async Task<JsonObject> RequestAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(Api, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return JsonNode.Parse(text) as JsonObject ?? [];

            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retry || attempt == Retries)
                throw new OpenAIApiException(response.StatusCode, AssistantErrors.ApiMessage(text));
            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
        }
    }

    public string ErrorText(Exception e) => e switch
    {
        OpenAIApiException { Status: HttpStatusCode.Unauthorized } => AssistantPrompt.KeyRejected(ProviderName),
        OpenAIApiException api => AssistantPrompt.ApiError(api.Message),
        HttpRequestException => AssistantPrompt.Offline(ProviderName, AssistantErrors.Innermost(e)),
        TaskCanceledException { InnerException: TimeoutException } => AssistantPrompt.Offline(ProviderName, "no answer in 60 seconds"),
        _ => AssistantPrompt.ApiError(e.Message),
    };
}
