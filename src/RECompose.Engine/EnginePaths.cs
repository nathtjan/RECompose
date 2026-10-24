// SPDX-License-Identifier: AGPL-3.0-or-later
namespace RECompose.Engine;

/// <summary>Where the engine executable and its socket live.</summary>
public static class EnginePaths
{
    /// <summary>recompose-engine.exe on Windows, recompose-engine elsewhere.</summary>
    public static string ExecutableFileName =>
        OperatingSystem.IsWindows() ? "recompose-engine.exe" : "recompose-engine";

    /// <summary>
    /// Next to the app (the build copies it there). REC_ENGINE_PATH overrides it, for
    /// running against an engine built elsewhere.
    /// </summary>
    public static string DefaultExecutablePath() =>
        Environment.GetEnvironmentVariable("REC_ENGINE_PATH") is { Length: > 0 } path
            ? path
            : Path.Combine(AppContext.BaseDirectory, ExecutableFileName);

    /// <summary>
    /// One engine per user: %LOCALAPPDATA%\RECompose\run\engine.sock on Windows,
    /// ~/Library/Application Support/RECompose/run/engine.sock on macOS.
    /// </summary>
    public static string DefaultEndpoint() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "RECompose", "run", "engine.sock");
}
