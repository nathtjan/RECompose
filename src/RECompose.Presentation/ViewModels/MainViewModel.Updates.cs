// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>
/// Updates (PLAT-70, AT-63): a newer release downloads in the background and installs when the app quits. Nothing is
/// shown or installed in Perform or while playing; the note sits in Prepare's top bar. Settings › Updates turns the
/// background download off and checks by hand.
/// </summary>
public sealed partial class MainViewModel
{
    private string? _updateVersion;
    private string _updateCheckText = string.Empty;
    private RelayCommand? _checkForUpdates;

    /// <summary>Set by the app; tests and development builds leave it null.</summary>
    public AppUpdates? Updates { get; set; }

    public bool HasUpdate => _updateVersion is not null;

    public string UpdateStatus => _updateVersion is null ? string.Empty : $"Update {_updateVersion} ready. It installs when you quit.";

    /// <summary>Settings › Updates: download in the background and install on quit. A check by hand works either way.</summary>
    public bool AutoUpdate
    {
        get => _settings.AutoUpdate;
        set
        {
            if (_settings.AutoUpdate != value)
            {
                UpdateSettings(s => s with { AutoUpdate = value });
                OnPropertyChanged(nameof(AutoUpdate));
            }
        }
    }

    /// <summary>The result of the last check by hand.</summary>
    public string UpdateCheckText { get => _updateCheckText; private set => Set(ref _updateCheckText, value); }

    public RelayCommand CheckForUpdatesCommand => _checkForUpdates ??= new RelayCommand(() => CheckUpdatesAsync(manual: true));

    private async Task CheckUpdatesAsync(bool manual = false)
    {
        if (!manual && !AutoUpdate)
            return;
        if (Updates is not { IsInstalled: true } updates)
        {
            if (manual)
                UpdateCheckText = "This copy was not installed, so it does not update itself.";
            return;
        }

        if (manual)
            UpdateCheckText = "Checking…";
        var version = await Task.Run(updates.DownloadAsync);
        _ui.Post(() =>
        {
            _updateVersion = version;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateStatus));
            if (manual)
                UpdateCheckText = version is null ? "No newer version was found, or the check failed. The log has the details." : UpdateStatus;
        });
    }

    /// <summary>On quit: hands a downloaded update to the updater unless the app is playing or in Perform (then the next quit does). With updates off, only one fetched by hand this session installs.</summary>
    public bool InstallUpdateOnQuit() => (AutoUpdate || _updateVersion is not null) && !IsPlaying && !IsPerform && Updates?.InstallOnQuit() == true;
}
