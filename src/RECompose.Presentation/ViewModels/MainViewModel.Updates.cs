// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Updates (PLAT-70, AT-63): a newer release downloads in the background and installs when the app quits. Nothing is
/// shown or installed in Perform or while playing; the note sits in Prepare's top bar.
/// </summary>
public sealed partial class MainViewModel
{
    private string? _updateVersion;

    /// <summary>Set by the app; tests and development builds leave it null.</summary>
    public AppUpdates? Updates { get; set; }

    public bool HasUpdate => _updateVersion is not null;

    public string UpdateStatus => _updateVersion is null ? string.Empty : $"Update {_updateVersion} ready. It installs when you quit.";

    private async Task CheckUpdatesAsync()
    {
        if (Updates is not { IsInstalled: true } updates)
            return;
        var version = await Task.Run(updates.DownloadAsync);
        _ui.Post(() =>
        {
            _updateVersion = version;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateStatus));
        });
    }

    /// <summary>On quit: hands a downloaded update to the updater unless the app is playing or in Perform (then the next quit does).</summary>
    public bool InstallUpdateOnQuit() => !IsPlaying && !IsPerform && Updates?.InstallOnQuit() == true;
}
