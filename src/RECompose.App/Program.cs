// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;

namespace RECompose.App;

internal static class Program
{
    // Don't touch Avalonia, third-party APIs or anything SynchronizationContext-reliant
    // before AppMain is called: things aren't initialised yet.
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer and updater hooks (PLAT-70). A downloaded update is not applied at start-up: the engine may still be
        // playing from before (REL-34); it is installed when the app quits instead.
        // The hooks also make double-clicking a .recservice file open RECompose (Windows).
        var velopack = Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false);
        if (OperatingSystem.IsWindows())
            WindowsHooks(velopack);
        velopack.Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsHooks(Velopack.VelopackApp velopack) => velopack
        .OnAfterInstallFastCallback(_ => FileTypes.Register(Environment.ProcessPath!))
        .OnAfterUpdateFastCallback(_ => FileTypes.Register(Environment.ProcessPath!))
        .OnBeforeUninstallFastCallback(_ => FileTypes.Unregister());

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
