// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace RECompose.Engine.Tests;

// Starts the real recompose-engine process. Locally these tests are skipped when the engine
// hasn't been built yet; CI sets REC_REQUIRE_NATIVE=1 so a missing engine fails instead.
public class EngineHostTests
{
    private static EngineHostOptions Options()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        if (!File.Exists(executable))
        {
            var problem = $"{EnginePaths.ExecutableFileName} not found in {AppContext.BaseDirectory}. Run the build script first.";
            if (Environment.GetEnvironmentVariable("REC_REQUIRE_NATIVE") == "1")
                Assert.Fail(problem);
            Assert.Skip(problem);
        }

        // A short, unique endpoint per test (socket paths are limited to about 100 bytes).
        var endpoint = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}"[..14] + ".sock");
        return new EngineHostOptions(executable, endpoint) { IdleExitSeconds = 3 };
    }

    private static async Task WaitForExitAsync(int pid)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // already gone
        }

        using (process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task Starts_the_engine_and_says_hello()
    {
        var options = Options();
        var ct = TestContext.Current.CancellationToken;

        var session = await EngineHost.ConnectOrStartAsync(options, ct);

        Assert.False(session.Attached);
        Assert.Equal("0.1.0", session.EngineVersion);
        Assert.StartsWith("JUCE v", session.JuceVersion);
        Assert.True(session.ProcessId > 0);
        await session.PingAsync(ct);

        var pid = session.ProcessId;
        await session.ShutdownAsync(ct);
        await WaitForExitAsync(pid);
    }

    [Fact]
    public async Task A_second_app_reattaches_to_the_running_engine()
    {
        var options = Options();
        var ct = TestContext.Current.CancellationToken;

        var first = await EngineHost.ConnectOrStartAsync(options, ct);
        var pid = first.ProcessId;
        await first.DisposeAsync(); // detach without stopping, as a crashed app would

        var second = await EngineHost.ConnectOrStartAsync(options, ct);
        Assert.True(second.Attached);
        Assert.Equal(pid, second.ProcessId);

        await second.ShutdownAsync(ct);
        await WaitForExitAsync(pid);
    }

    [Fact]
    public async Task The_engine_exits_by_itself_when_no_app_is_connected()
    {
        var options = Options();
        var ct = TestContext.Current.CancellationToken;

        var session = await EngineHost.ConnectOrStartAsync(options, ct);
        var pid = session.ProcessId;
        await session.DisposeAsync();

        await WaitForExitAsync(pid); // IdleExitSeconds = 3
    }

    [Fact]
    public async Task A_missing_executable_gives_a_readable_problem()
    {
        var options = Options() with { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-engine") };

        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(
            () => EngineHost.ConnectOrStartAsync(options, TestContext.Current.CancellationToken));
        Assert.Contains("was not found", ex.Message);
    }
}
