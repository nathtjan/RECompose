// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.Json.Nodes;
using RECompose.Engine.Ipc;

namespace RECompose.Engine;

/// <summary>
/// A connection to the engine process after a successful hello. Disposing it detaches
/// the app and leaves the engine running (it exits by itself when idle);
/// <see cref="ShutdownAsync"/> stops the engine.
/// </summary>
public sealed class EngineSession : IAsyncDisposable
{
    private EngineSession(EngineConnection connection, JsonObject hello, bool attached)
    {
        Connection = connection;
        EngineVersion = (string?)hello["engine"] ?? "unknown";
        JuceVersion = (string?)hello["juce"] ?? "unknown";
        ProcessId = (int?)hello["pid"] ?? 0;
        Attached = attached;
    }

    public EngineConnection Connection { get; }

    /// <summary>e.g. "0.1.0".</summary>
    public string EngineVersion { get; }

    /// <summary>e.g. "JUCE v9.0.4".</summary>
    public string JuceVersion { get; }

    public int ProcessId { get; }

    /// <summary>True when the app attached to an engine that was already running.</summary>
    public bool Attached { get; }

    /// <summary>"0.1.0 (JUCE v9.0.4)".</summary>
    public string DisplayVersion => $"{EngineVersion} ({JuceVersion})";

    internal static async Task<EngineSession> HandshakeAsync(EngineConnection connection, bool attached, CancellationToken cancellationToken)
    {
        var client = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var hello = await connection.RequestAsync("hello", new JsonObject
        {
            ["protocol"] = EngineProtocol.Version,
            ["client"] = $"RECompose {client}",
        }, cancellationToken).ConfigureAwait(false);

        return new EngineSession(connection, hello, attached);
    }

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        Connection.RequestAsync("ping", cancellationToken: cancellationToken);

    /// <summary>Asks the engine process to exit, then closes the connection.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Connection.RequestAsync("shutdown", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (EngineDisconnectedException)
        {
            // It is already gone.
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
