// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RECompose.Engine;

namespace RECompose.App;

public partial class App : Application
{
    private EngineSession? _engine;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => StopEngine();
            _ = ConnectEngineAsync(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ConnectEngineAsync(MainWindow window)
    {
        string status;
        try
        {
            _engine = await EngineHost.ConnectOrStartAsync(EngineHostOptions.Default()).ConfigureAwait(false);
            status = $"Engine {_engine.DisplayVersion} · process {_engine.ProcessId}" + (_engine.Attached ? " · reattached" : "");
        }
        catch (EngineUnavailableException ex)
        {
            status = ex.Message;
        }

        await Dispatcher.UIThread.InvokeAsync(() => window.SetEngineStatus(status));
    }

    // On a normal quit the engine stops too. If the app crashes instead, the engine keeps
    // running and the next launch reattaches to it (REL-34).
    private void StopEngine()
    {
        if (_engine is { } engine)
            Task.Run(() => engine.ShutdownAsync()).Wait(TimeSpan.FromSeconds(2));
    }
}
