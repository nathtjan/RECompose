// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using RECompose.Engine;

namespace RECompose.Presentation.Tests;

/// <summary>The app and the engine carry one version, the one in Directory.Build.props that a release tag must match.</summary>
public class VersionTests
{
    [Fact]
    public async Task The_engine_reports_the_same_version_as_the_app()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, EnginePaths.ExecutableFileName);
        var endpoint = Path.Combine(Path.GetTempPath(), "rec-version-" + Guid.NewGuid().ToString("N"));
        await using var session = await EngineHost.ConnectOrStartAsync(new EngineHostOptions(exe, endpoint) { IdleExitSeconds = 3 }, TestContext.Current.CancellationToken);

        var app = typeof(EngineSession).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(app, session.EngineVersion);
        Assert.Equal(app, typeof(VersionTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);
    }
}
