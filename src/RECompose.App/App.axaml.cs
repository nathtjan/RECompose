// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
        Controls.ClickThroughTips.Install();
    }

    private static FileStream? _instanceLock;
    private bool _showingError;

    /// <summary>What a bug report needs: the version, the system, when, and the exception with its stack.</summary>
    internal static string ErrorDetails(object error) => string.Join(Environment.NewLine,
        $"RECompose {typeof(App).Assembly.GetName().Version?.ToString(3)} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        string.Empty,
        error.ToString());

    /// <summary>One error window at a time: an error that repeats is only logged. While the band plays it opens without taking the keys.</summary>
    private void ShowError(Window owner, string title, string message, string details)
    {
        if (_showingError)
            return;
        _showingError = true;
        var dialog = ErrorDialog.Create(title, message, details);
        dialog.Closed += (_, _) => _showingError = false;
        if (!owner.IsVisible)
            dialog.Show();
        else if (_viewModel is { IsPlaying: true } or { IsPerform: true })
        {
            dialog.ShowActivated = false;
            dialog.Show(owner);
        }
        else
            _ = dialog.ShowDialog(owner);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var opened = OpenFiles.FromArgs(desktop.Args ?? []);
            if (!TryTakeInstanceLock())
            {
                if (opened is not null && OpenFiles.Forward(opened))
                {
                    desktop.Shutdown();
                    base.OnFrameworkInitializationCompleted();
                    return;
                }

                desktop.MainWindow = AlreadyRunningWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var window = new MainWindow();
            var dialogs = new AvaloniaDialogs(window);
            var store = DataStore.Default();
            AppLog.Start(store.Root);
            AppLog.Write("App", $"RECompose {typeof(App).Assembly.GetName().Version?.ToString(3)} started on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            // An error on the UI thread is logged and shown and the app goes on; one that ends the app is shown at the next start.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                AppLog.Error("Unhandled", e.Exception);
                var details = ErrorDetails(e.Exception);
                Dispatcher.UIThread.Post(() => ShowError(window, "Something went wrong",
                    $"{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}RECompose keeps running. Restart it if something looks wrong.", details));
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Crash(ErrorDetails(e.ExceptionObject));
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
            window.Opened += async (_, _) =>
            {
                await _viewModel.InitializeAsync();
                if (AppLog.TakeCrash() is { } last)
                    ShowError(window, "RECompose closed after an error",
                        "RECompose closed last time because of an error it did not expect. The details help to find the cause.", last);
                if (opened is not null)
                    await _viewModel.ImportServiceAsync(opened);
                OpenFiles.Listen(path => Dispatcher.UIThread.Post(() => OpenService(window, path)));
                if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime) // macOS delivers opened files here
                    lifetime.Activated += (_, e) =>
                    {
                        if (e is FileActivatedEventArgs { Files: var files })
                            foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>().Where(OpenFiles.IsService))
                                OpenService(window, path);
                    };
            };
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

    private void OpenService(Window window, string path)
    {
        window.Activate();
        if (_viewModel is { } vm)
            _ = vm.ImportServiceAsync(path);
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
