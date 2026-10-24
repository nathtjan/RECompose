// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Sockets;
using RECompose.Engine.Ipc;

namespace RECompose.Engine;

/// <summary>How to find and start the engine process.</summary>
public sealed record EngineHostOptions(string ExecutablePath, string Endpoint)
{
    /// <summary>How long a freshly started engine may take to accept connections.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The engine exits after this long with no app connected and nothing playing.</summary>
    public double IdleExitSeconds { get; init; } = 10;

    public static EngineHostOptions Default() =>
        new(EnginePaths.DefaultExecutablePath(), EnginePaths.DefaultEndpoint());
}

/// <summary>
/// Starts the engine process, or reattaches to one that is already running (for example
/// after the app crashed while the engine kept playing, REL-34).
/// </summary>
public static class EngineHost
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    /// <exception cref="EngineUnavailableException">With a message for the user.</exception>
    public static async Task<EngineSession> ConnectOrStartAsync(EngineHostOptions options, CancellationToken cancellationToken = default)
    {
        if (await TryConnectAsync(options.Endpoint, cancellationToken).ConfigureAwait(false) is { } running)
        {
            try
            {
                return await EngineSession.HandshakeAsync(running, attached: true, cancellationToken).ConfigureAwait(false);
            }
            catch (EngineErrorException ex) when (ex.Code == "protocolMismatch")
            {
                // An engine from another app version, e.g. still running across an update.
                await running.DisposeAsync().ConfigureAwait(false);
                await StopRunningEngineAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await running.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var started = await StartAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            return await EngineSession.HandshakeAsync(started, attached: false, cancellationToken).ConfigureAwait(false);
        }
        catch (EngineErrorException ex)
        {
            await started.DisposeAsync().ConfigureAwait(false);
            throw new EngineUnavailableException($"The audio engine refused the connection: {ex.Message}", ex);
        }
    }

    private static async Task<EngineConnection?> TryConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            return await EngineConnection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return null; // nothing listening, or a stale socket file the engine replaces when it starts
        }
    }

    private static async Task<EngineConnection> StartAsync(EngineHostOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ExecutablePath))
        {
            throw new EngineUnavailableException(
                $"The audio engine ({Path.GetFileName(options.ExecutablePath)}) was not found next to the app. " +
                "Build it with the build script, then rebuild the app.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(options.Endpoint)!);

        // No redirected output: if the app dies, a broken pipe must not take the engine with it.
        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--endpoint");
        start.ArgumentList.Add(options.Endpoint);
        start.ArgumentList.Add("--idle-exit-seconds");
        start.ArgumentList.Add(options.IdleExitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(start)
            ?? throw new EngineUnavailableException("The audio engine could not be started.");

        var deadline = DateTime.UtcNow + options.StartTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryConnectAsync(options.Endpoint, cancellationToken).ConfigureAwait(false) is { } connection)
                return connection;

            if (process.HasExited)
            {
                throw new EngineUnavailableException(
                    $"The audio engine stopped while starting ({DescribeExitCode(process.ExitCode)}).");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new EngineUnavailableException(
                    $"The audio engine did not answer within {options.StartTimeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task StopRunningEngineAsync(EngineHostOptions options, CancellationToken cancellationToken)
    {
        if (await TryConnectAsync(options.Endpoint, cancellationToken).ConfigureAwait(false) is { } connection)
        {
            await using (connection.ConfigureAwait(false))
            {
                try
                {
                    await connection.RequestAsync("shutdown", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (EngineDisconnectedException)
                {
                }
            }
        }

        // Wait until nothing answers on the endpoint any more.
        var deadline = DateTime.UtcNow + options.StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var stillThere = await TryConnectAsync(options.Endpoint, cancellationToken).ConfigureAwait(false);
            if (stillThere is null)
                return;
            await stillThere.DisposeAsync().ConfigureAwait(false);
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string DescribeExitCode(int code) => code switch
    {
        64 => "bad arguments",
        69 => "its socket could not be opened",
        70 => "the socket library could not be initialised",
        _ => $"exit code {code}",
    };
}
