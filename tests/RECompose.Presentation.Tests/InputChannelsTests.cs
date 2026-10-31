// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Engine;

namespace RECompose.Presentation.Tests;

/// <summary>Against the real engine and this machine's devices; with no input devices there is nothing to check.</summary>
public class InputChannelsTests
{
    [Fact]
    public async Task Every_input_device_of_a_driver_with_separate_inputs_lists_its_channels()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        var endpoint = Path.Combine(Path.GetTempPath(), "rec-inputs-" + Guid.NewGuid().ToString("N"));
        await using var session = await EngineHost.ConnectOrStartAsync(new EngineHostOptions(exe, endpoint) { IdleExitSeconds = 3 });
        var client = new EngineClient(session);

        var (types, _) = await client.ListDevicesAsync();
        foreach (var type in types.Where(t => t.SeparateInputs))
            foreach (var input in type.Inputs ?? [])
                Assert.NotEmpty(await client.ListInputChannelsAsync(type.Name, input));
        Assert.Empty(await client.ListInputChannelsAsync("No such driver", "No such device"));
    }
}
