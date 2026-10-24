// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RECompose.Engine.Ipc;

/// <summary>
/// One connection to the engine process over its local socket. Requests carry an "id";
/// the engine's reply carries the same value in "re". Messages without "re" are events.
/// Thread-safe: requests may be sent from any thread.
/// </summary>
public sealed class EngineConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reader;
    private int _nextId;

    private EngineConnection(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Raised on the reader thread for every message that isn't a reply.</summary>
    public event Action<JsonObject>? EventReceived;

    /// <summary>Completes when the connection has closed, for whatever reason.</summary>
    public Task Completion => _reader;

    /// <summary>Connects to an engine listening on <paramref name="endpoint"/>.</summary>
    /// <exception cref="SocketException">Nothing is listening there.</exception>
    public static async Task<EngineConnection> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken).ConfigureAwait(false);
            return new EngineConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a request and waits for its reply. An {"type":"error"} reply throws
    /// <see cref="EngineErrorException"/>.
    /// </summary>
    public async Task<JsonObject> RequestAsync(string type, JsonObject? fields = null, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = fields?.DeepClone().AsObject() ?? new JsonObject();
        message["type"] = type;
        message["id"] = id;

        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;

        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => reply.TrySetCanceled(cancellationToken));
            var answer = await reply.Task.ConfigureAwait(false);

            if ((string?)answer["type"] == "error")
                throw new EngineErrorException((string?)answer["code"] ?? "unknown", (string?)answer["message"] ?? "The engine reported an error.");

            return answer;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        byte[] payload;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
                message.WriteTo(writer);
            payload = buffer.ToArray();
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new EngineDisconnectedException($"The connection to the audio engine was lost: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        string reason = "The audio engine closed the connection.";
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                var payload = await FrameCodec.ReadFrameAsync(_stream, _closing.Token).ConfigureAwait(false);
                if (payload is null)
                    break;

                if (JsonNode.Parse(payload) is not JsonObject message)
                    throw new EngineProtocolException("The engine sent a message that isn't a JSON object.");

                if (message["re"] is JsonValue re && re.TryGetValue(out int id))
                {
                    if (_pending.TryGetValue(id, out var waiting))
                        waiting.TrySetResult(message);
                }
                else
                {
                    EventReceived?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "The connection to the audio engine was closed.";
        }
        catch (Exception ex) when (ex is IOException or EngineProtocolException or JsonException or ObjectDisposedException)
        {
            reason = $"The connection to the audio engine was lost: {ex.Message}";
        }
        finally
        {
            foreach (var waiting in _pending.Values)
                waiting.TrySetException(new EngineDisconnectedException(reason));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already closed by the engine.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch
        {
            // The reader reports through the pending requests; nothing more to do here.
        }

        _closing.Dispose();
        _writeLock.Dispose();
    }
}
