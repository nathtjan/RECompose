// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RECompose.App.Theme;
using RECompose.Core.Persistence;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.ViewModels;

namespace RECompose.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (Resources is ResourceDictionary resources)
        {
            Tokens.Install(resources);
            FluentMapping.Install(resources);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var dialogs = new AvaloniaDialogs(window);
            var store = DataStore.Default();
            var engine = EngineHostOptions.Default();
            _viewModel = new MainViewModel(store, engine, new EngineTools(engine.ExecutablePath), new AvaloniaDispatcher(), dialogs);
            window.DataContext = _viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => Shutdown();
            window.Opened += async (_, _) => await _viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // On a normal quit the engine stops too. If the app crashes instead, the engine keeps
    // playing and the next launch reattaches to it (REL-34).
    private void Shutdown()
    {
        if (_viewModel is { } vm)
        {
            _viewModel = null;
            vm.Flush(); // on the UI thread: saves the mix
            Task.Run(vm.ShutdownEngineAsync).Wait(TimeSpan.FromSeconds(2));
        }
    }
}

/// <summary>Avalonia's UI thread for the view models.</summary>
public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void After(TimeSpan delay, Action action) =>
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(action, delay));
}
