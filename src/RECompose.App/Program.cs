// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;

namespace RECompose.App;

internal static class Program
{
    // Don't touch Avalonia, third-party APIs or anything SynchronizationContext-reliant
    // before AppMain is called: things aren't initialised yet.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
