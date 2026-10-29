// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RECompose.App.Dialogs;
using RECompose.App.Theme;
using RECompose.Core.Persistence;
using RECompose.Engine;
using RECompose.Presentation.Mvvm;
using RECompose.Presentation.Services;
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

    private static FileStream? _instanceLock;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!TryTakeInstanceLock())
            {
                desktop.MainWindow = AlreadyRunningWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var window = new MainWindow();
            var dialogs = new AvaloniaDialogs(window);
            var store = DataStore.Default();
            AppLog.Start(store.Root);
            AppLog.Write("App", $"RECompose {typeof(App).Assembly.GetName().Version?.ToString(3)} started on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Errors", $"Unhandled: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLog.Error("Unobserved task", e.Exception);
                e.SetObserved();
            };
            var engine = EngineHostOptions.Default();
            _viewModel = new MainViewModel(store, engine, new EngineTools(engine.ExecutablePath), new AvaloniaDispatcher(), dialogs) { Updates = new AppUpdates() };
            window.DataContext = _viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => Shutdown();
            window.Opened += async (_, _) => await _viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// One RECompose per user: two windows would take the engine from each other. The lock is
    /// a file held open exclusively; the OS releases it if the app crashes.
    /// </summary>
    private static bool TryTakeInstanceLock()
    {
        var path = Path.Combine(Path.GetDirectoryName(EnginePaths.DefaultEndpoint())!, "app.lock");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // can't tell; don't block the app
        }
    }

    private static Window AlreadyRunningWindow()
    {
        var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, IsDefault = true };
        var window = new Window
        {
            Title = "RECompose",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "RECompose is already open. Switch to its window to continue.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        return window;
    }

    // On a normal quit the engine stops too. If the app crashes instead, the engine keeps
    // playing and the next launch reattaches to it (REL-34).
    private void Shutdown()
    {
        if (_viewModel is { } vm)
        {
            _viewModel = null;
            vm.InstallUpdateOnQuit(); // the updater waits for this process to exit
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

    public void Queue(Action action) => Dispatcher.UIThread.Post(action);

    public void After(TimeSpan delay, Action action) =>
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(action, delay));
}
