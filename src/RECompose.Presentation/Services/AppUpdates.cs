// SPDX-License-Identifier: AGPL-3.0-or-later
using Velopack;
using Velopack.Sources;

namespace RECompose.Presentation.Services;

/// <summary>
/// Updates (PLAT-70) with Velopack from the GitHub releases: a newer release is downloaded in the background and installed
/// after the app quits, never while it plays or is in Perform (the caller decides). An app that was not installed (a
/// development build or the CI zip) never updates. Settings, the library, the cache and the key live outside the install
/// folder, so an update keeps them.
/// </summary>
public sealed class AppUpdates
{
    public const string Repository = "https://github.com/nathtjan/RECompose";

    private readonly UpdateManager _manager = new(new GithubSource(Repository, null, false));
    private VelopackAsset? _ready;

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The version downloaded and waiting for the app to quit, or null.</summary>
    public string? ReadyVersion => (_ready ?? (IsInstalled ? _manager.UpdatePendingRestart : null))?.Version.ToString();

    /// <summary>Checks and downloads a newer release; returns its version, or null when there is none or it failed.</summary>
    public async Task<string?> DownloadAsync()
    {
        if (!IsInstalled)
            return null;
        try
        {
            if (await _manager.CheckForUpdatesAsync().ConfigureAwait(false) is not { } update)
                return ReadyVersion;
            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            _ready = update.TargetFullRelease;
            AppLog.Write("App", $"Update {_ready.Version} downloaded; it installs when RECompose quits");
            return ReadyVersion;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            AppLog.Write("App", $"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Starts the updater, which waits for the app to exit and installs without restarting it.</summary>
    public bool InstallOnQuit()
    {
        if (ReadyVersion is not { } version)
            return false;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready ?? _manager.UpdatePendingRestart, silent: true, restart: false);
            AppLog.Write("App", $"Installing update {version} after quit");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("App", $"Update not installed: {ex.Message}");
            return false;
        }
    }
}
