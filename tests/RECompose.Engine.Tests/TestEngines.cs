// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace RECompose.Engine.Tests;

/// <summary>
/// Engines started by a test, shut down when the test ends. A playing engine never exits by itself (REL-34), so a test
/// that only detaches would leave it running and holding the test folder's recompose-engine.exe.
/// </summary>
internal sealed class TestEngines : IAsyncDisposable
{
    private readonly List<(EngineHostOptions Options, int Pid)> _started = [];

    public async Task<EngineClient> StartAsync(string executable)
    {
        // A short, unique endpoint per engine (socket paths are limited to about 100 bytes).
        var endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");
        // Shut down below, so no short idle exit: on a loaded CI runner 3 s ran out before the test had connected.
        var options = new EngineHostOptions(executable, endpoint) { IdleExitSeconds = 30 };
        var session = await EngineHost.ConnectOrStartAsync(options, TestContext.Current.CancellationToken);
        _started.Add((options, session.ProcessId));
        return new EngineClient(session);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (options, pid) in _started)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    continue;
                await using var session = await EngineHost.ConnectOrStartAsync(options);
                await session.ShutdownAsync();
            }
            catch (Exception ex) when (ex is ArgumentException or EngineUnavailableException or EngineDisconnectedException or IOException)
            {
                // already gone
            }
        }
    }
}
