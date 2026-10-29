// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RECompose.Presentation.Services;

/// <summary>
/// Microphone permission (PLAT-35, AT-61). Windows: the privacy switches in the registry (all apps, desktop apps).
/// macOS: AVCaptureDevice's authorisation for the app; the system asks the first time the engine opens the input, with
/// the usage text from the app bundle. Elsewhere access is assumed.
/// </summary>
public static class MicrophoneAccess
{
    /// <summary>Tests replace the reading.</summary>
    public static Func<bool>? Override { get; set; }

    public static bool Denied()
    {
        if (Override is { } custom)
            return custom();
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsDenied();
            if (OperatingSystem.IsMacOS())
                return MacStatus() is 1 or 2; // restricted, denied
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    public static string Problem => OperatingSystem.IsMacOS()
        ? "Microphone access is off for RECompose. Turn it on in System Settings › Privacy & Security › Microphone."
        : "Microphone access is off for desktop apps. Turn it on in Settings › Privacy & security › Microphone.";

    /// <summary>Opens the system's microphone privacy page.</summary>
    public static void OpenSettings()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose();
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Voice", $"Could not open the privacy settings: {ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WindowsDenied()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        static bool Deny(Microsoft.Win32.RegistryKey root, string path) =>
            root.OpenSubKey(path)?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        return Deny(Microsoft.Win32.Registry.LocalMachine, key) || Deny(Microsoft.Win32.Registry.CurrentUser, key)
            || Deny(Microsoft.Win32.Registry.CurrentUser, key + @"\NonPackaged");
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendText(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

    /// <summary>[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio]: 0 not asked, 1 restricted, 2 denied, 3 allowed.</summary>
    private static long MacStatus()
    {
        dlopen("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", 1);
        var device = objc_getClass("AVCaptureDevice");
        if (device == IntPtr.Zero)
            return 0;
        var audio = SendText(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "soun"); // AVMediaTypeAudio
        return (long)Send(device, sel_registerName("authorizationStatusForMediaType:"), audio);
    }
}
