// SPDX-License-Identifier: AGPL-3.0-or-later
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;

namespace RECompose.Presentation.ViewModels;

/// <summary>The tabs of the Settings window, in order.</summary>
public enum SettingsTab
{
    Audio,
    Voice,
    Controls,
    Midi,
    Assistant,
    Storage,
    Updates,
}

/// <summary>
/// The Settings window: one tab per area. Audio, Voice and the render cache share one staged <see cref="AudioSettingsViewModel"/>
/// that is applied with the window's Apply (null without the engine); the other tabs change their settings as they are edited.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private int _selectedIndex;

    public SettingsViewModel(MainViewModel main, AudioSettingsViewModel? audio, SettingsTab tab)
    {
        Main = main;
        Audio = audio;
        _selectedIndex = (int)tab;
        OpenDataFolderCommand = new RelayCommand(() => SystemShell.Open(main.DataRoot));
    }

    /// <summary>The updates tab binds the app's update state directly.</summary>
    public MainViewModel Main { get; }

    public AudioSettingsViewModel? Audio { get; }
    public bool HasAudio => Audio is not null;
    public ControlsViewModel Controls => Main.Controls;
    public AssistantViewModel Assistant => Main.Assistant;

    /// <summary>The selected tab as a <see cref="SettingsTab"/> number.</summary>
    public int SelectedIndex { get => _selectedIndex; set => Set(ref _selectedIndex, value); }

    public string DataFolder => Main.DataRoot;
    public RelayCommand OpenDataFolderCommand { get; }
}
