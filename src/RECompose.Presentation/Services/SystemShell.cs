// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace RECompose.Presentation.Services;

/// <summary>Opens a folder or a link with the system's default handler.</summary>
public static class SystemShell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            AppLog.Write("App", $"Could not open {target}: {ex.Message}");
        }
    }
}
