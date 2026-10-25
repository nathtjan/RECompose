// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RECompose.Presentation.ViewModels;

namespace RECompose.App;

/// <summary>The Perform screen (UI-40). The view model does the work; this only forwards taps and keeps the clock.</summary>
public partial class PerformView : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public PerformView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => ShowClock();
        AttachedToVisualTree += (_, _) =>
        {
            ShowClock();
            _clock.Start();
        };
        DetachedFromVisualTree += (_, _) => _clock.Stop();
        Mini.SeekRequested += seconds =>
        {
            if (DataContext is MainViewModel vm)
                _ = vm.SeekPlanAsync(seconds);
        };
    }

    private void ShowClock() => ClockLine.Text = $"Perform · {DateTime.Now:HH:mm}";

    private void OnTileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TileViewModel tile } && DataContext is MainViewModel vm)
            _ = vm.TapTileAsync(tile);
    }

    private void OnPlayFromHere(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TileViewModel tile } && DataContext is MainViewModel vm)
            _ = vm.PlayFromHereAsync(tile);
    }

    private void OnRailRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && DataContext is MainViewModel vm)
            _ = vm.LoadEntryAsync(id);
    }
}
