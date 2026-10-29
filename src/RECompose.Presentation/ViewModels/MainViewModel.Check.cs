// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using RECompose.Core.Library;
using RECompose.Core.Services;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>One row of the service check (REL-1): status, what was found, and a Fix button where there is one.</summary>
public sealed class CheckItemViewModel : ObservableObject
{
    public CheckItemViewModel(CheckItem item, Func<CheckItem, Task> fix)
    {
        Item = item;
        FixCommand = new RelayCommand(() => fix(item));
    }

    public CheckItem Item { get; }
    public string Mark => Item.Mark;
    public string Title => $"{Item.Number}. {Item.Title}";
    public string Detail => Item.Detail;
    public CheckStatus Status => Item.Status;
    public bool IsError => Item.Status == CheckStatus.Error;
    public bool IsWarning => Item.Status == CheckStatus.Warning;
    public bool HasFix => Item.Fix != CheckFix.None;

    public string FixLabel => Item.Fix switch
    {
        CheckFix.Relink => "Relink…",
        CheckFix.OpenEntry => "Open entry",
        CheckFix.AnalyseNow => "Analyse now",
        CheckFix.RenderNow => "Render now",
        CheckFix.OpenAudioSettings => "Open Audio settings",
        CheckFix.ChooseFallback => "Choose…",
        CheckFix.CleanCache => "Clean cache",
        CheckFix.IncreaseBuffer => "Increase buffer…",
        _ => string.Empty,
    };

    public RelayCommand FixCommand { get; }
}

/// <summary>The service check dialog (REL-1, REL-2): the items, the verdict, Run check.</summary>
public sealed class ServiceCheckViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _verdict = string.Empty;
    private string _at = string.Empty;

    public ServiceCheckViewModel(MainViewModel main)
    {
        _main = main;
        RunCommand = new RelayCommand(Run);
        Run();
    }

    public ObservableCollection<CheckItemViewModel> Items { get; } = [];
    public RelayCommand RunCommand { get; }
    public string Title => _main.CheckServiceName;
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string CheckedAt { get => _at; private set => Set(ref _at, value); }

    public void Run()
    {
        var result = _main.RunServiceCheck();
        Items.Clear();
        if (result is null)
        {
            Verdict = "No service";
            CheckedAt = string.Empty;
            return;
        }

        foreach (var item in result.Items)
            Items.Add(new CheckItemViewModel(item, _main.FixCheckItemAsync));
        Verdict = result.Text;
        CheckedAt = $"Checked {result.At:HH:mm}";
    }
}

public sealed partial class MainViewModel
{
    private RelayCommand? _openCheck;
    private ServiceCheckViewModel? _openCheckViewModel;

    public string CheckServiceName => _service?.Name ?? string.Empty;

    /// <summary>The badge in the top bar: the last result, and whether the service changed since (REL-2).</summary>
    public string CheckBadgeText
    {
        get
        {
            if (_service is not { } service)
                return string.Empty;
            if (service.LastCheck is not { } last)
                return "Not checked";
            var text = last.Result switch { "ready" => "✓ Ready", "warnings" => "⚠ Ready with warnings", _ => "✗ Not ready" };
            return last.Signature != ServiceCheck.Signature(service, FindSongForCheck) ? text + " · changed since check" : text;
        }
    }

    public bool HasCheckBadge => _service is not null;

    public RelayCommand OpenCheckCommand => _openCheck ??= new RelayCommand(OpenCheckAsync, () => _service is not null);

    private async Task OpenCheckAsync()
    {
        _openCheckViewModel = new ServiceCheckViewModel(this);
        await _dialogs.ShowServiceCheckAsync(_openCheckViewModel);
        _openCheckViewModel = null;
    }

    private Song? FindSongForCheck(string id) => _allSongs.Any(s => s.Id == id) || _song?.Id == id ? _store.LoadSong(id) : null;

    /// <summary>What the check needs to know about this machine (REL-1).</summary>
    private CheckContext BuildCheckContext(Service service)
    {
        long? free = null;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_store.Root))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        var a = _settings.Audio;
        return new CheckContext
        {
            Service = service,
            FindSong = FindSongForCheck,
            FileExists = File.Exists,
            Renders = RenderNeedFor,
            DeviceName = _device?.Name,
            OutputChannels = _device?.OutputChannels.Count ?? 0,
            MainChannel = a.MainChannel,
            MainWidth = a.MainWidth,
            CueChannel = a.CueChannel,
            CueWidth = a.CueWidth,
            FallbackDeviceName = _settings.Audio.FallbackDeviceName,
            FallbackPresent = _settings.Audio.FallbackDeviceName is not null && _fallbackPresent,
            HasMidiBindings = Controls.Bindings.Any(b => b.Input.Kind != RECompose.Core.Controls.ControlKind.Key),
            MidiInputPresent = _midiInputs.Count > 0,
            FreeDiskBytes = free,
            OnBattery = SystemPower.OnBattery,
            BatteryPercent = SystemPower.BatteryPercent,
            AssistantEnabled = Assistant.HasKey,
        };
    }

    /// <summary>Runs the check and keeps its result on the service (REL-2). The quick check in Perform does not store (REL-3).</summary>
    internal CheckResult? RunServiceCheck(bool quick = false)
    {
        if (_service is not { } service)
            return null;
        var result = ServiceCheck.Run(BuildCheckContext(service), quick);
        AppLog.Write("Check", $"{(quick ? "Quick check" : "Service check")}: {result.Text}" + string.Concat(result.Items.Where(i => i.Status is CheckStatus.Warning or CheckStatus.Error).Select(i => $" · {i.Number} {i.Mark}")));
        if (!quick)
        {
            ApplyService(service with
            {
                LastCheck = new ServiceCheckResult
                {
                    At = result.At,
                    Result = result.Code,
                    Items = [.. result.Items.Select(i => $"{i.Number} {i.Mark} {i.Title}: {i.Detail}")],
                    Signature = ServiceCheck.Signature(service, FindSongForCheck),
                },
            });
            OnPropertyChanged(nameof(CheckBadgeText));
        }

        return result;
    }

    /// <summary>The quick check of Perform (REL-3): items 1, 5, 7, 8, 9 in a moment, shown as a line.</summary>
    public void RunQuickCheck()
    {
        if (RunServiceCheck(quick: true) is not { } result)
            return;
        var problems = result.Items.Where(i => i.Status is CheckStatus.Warning or CheckStatus.Error).ToList();
        ShowToast(problems.Count == 0 ? $"{result.Text}" : $"{result.Text} · {problems[0].Title}: {problems[0].Detail}");
    }

    /// <summary>The Fix button of a check item (REL-1).</summary>
    internal async Task FixCheckItemAsync(CheckItem item)
    {
        switch (item.Fix)
        {
            case CheckFix.Relink when item.SongId is { } songId:
                await RelinkSongAsync(songId);
                break;
            case CheckFix.OpenEntry when item.EntryId is { } entryId:
                SelectEntry(entryId);
                SelectedTab = EntryTab;
                IsPerform = false;
                break;
            case CheckFix.OpenAudioSettings:
                await OpenAudioSettingsAsync();
                break;
            case CheckFix.ChooseFallback:
                await OpenAudioSettingsAsync();
                break;
            case CheckFix.RenderNow:
                RenderServiceNow();
                break;
            case CheckFix.CleanCache:
                CleanRenderCache();
                break;
            case CheckFix.AnalyseNow when item.SongId is { } id:
                if (_song?.Id == id)
                    await ReanalyseClickAsync(downbeatOnly: false);
                else
                    ShowToast("Load the song, then use Analyse on the SONG tab.");
                break;
        }

        _openCheckViewModel?.Run();
    }

    /// <summary>Relink…: choose the folder the stems moved to; stems are found there by file name (REL-1 item 1).</summary>
    private async Task RelinkSongAsync(string songId)
    {
        if (_store.LoadSong(songId) is not { } song)
            return;
        var folder = await _dialogs.PickFolderAsync();
        if (folder is null || !Directory.Exists(folder))
            return;

        var tracks = song.Tracks.Select(t =>
        {
            if (File.Exists(t.File))
                return t;
            var found = Directory.EnumerateFiles(folder, Path.GetFileName(t.File), SearchOption.AllDirectories).FirstOrDefault();
            return found is null ? t : t with { File = found };
        }).ToList();

        var fixedCount = tracks.Count(t => File.Exists(t.File)) - song.Tracks.Count(t => File.Exists(t.File));
        _store.SaveSong(song with { Tracks = tracks, UpdatedAt = DateTimeOffset.UtcNow });
        ShowToast(fixedCount > 0 ? $"Relinked {fixedCount} stem{(fixedCount == 1 ? string.Empty : "s")}." : "No missing stem was found in that folder.");
        if (_song?.Id == songId)
            await LoadSongAsync(songId, force: true, entry: _song.Entry, serviceId: _loadedServiceId);
    }
}
