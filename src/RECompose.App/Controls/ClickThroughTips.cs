// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RECompose.App.Controls;

/// <summary>
/// A tip may overlap the button under the pointer, and it can leave the main window, so it lives in its own native window.
/// That window would take the click; here it is made transparent to the mouse so the click reaches the button.
/// </summary>
internal static class ClickThroughTips
{
    public static void Install()
    {
        Control.LoadedEvent.AddClassHandler<ToolTip>((tip, _) =>
        {
            if (TopLevel.GetTopLevel(tip)?.TryGetPlatformHandle() is not { } handle)
                return;
            Apply(handle.HandleDescriptor, handle.Handle);
        });
    }

    /// <summary>Makes the native window of the given kind ignore the mouse; false when this system has no way to do it.</summary>
    internal static bool Apply(string? descriptor, nint window)
    {
        try
        {
            if (OperatingSystem.IsWindows() && descriptor == "HWND")
                Windows.Apply(window);
            else if (OperatingSystem.IsMacOS() && descriptor == "NSWindow")
                MacOS.Apply(window);
            else if (OperatingSystem.IsLinux() && descriptor == "XID")
                X11.Apply(window);
            else
                return false;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // shortcut: a system without the library keeps a tip that takes the click (Wayland has no such window to change)
            return false;
        }
    }

    private static class Windows
    {
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x20, WsExLayered = 0x80000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr(nint hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

        public static void Apply(nint hwnd) =>
            SetWindowLongPtr(hwnd, GwlExStyle, (nint)(GetWindowLongPtr(hwnd, GwlExStyle) | WsExTransparent | WsExLayered));    }

    private static class MacOS
    {
        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

        public static void Apply(nint window) => SendBool(window, sel_registerName("setIgnoresMouseEvents:"), true);
    }

    private static class X11
    {
        private const int ShapeInput = 2, ShapeSet = 0, Unsorted = 0;

        [DllImport("libX11.so.6")]
        private static extern nint XOpenDisplay(nint name);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(nint display);

        [DllImport("libXext.so.6")]
        private static extern void XShapeCombineRectangles(nint display, nint window, int kind, int x, int y, nint rectangles, int count, int op, int ordering);

        // An empty input region: the window is drawn but no pointer event lands on it.
        public static void Apply(nint window)
        {
            var display = XOpenDisplay(0);
            if (display == 0)
                return;
            XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, Unsorted);
            XCloseDisplay(display); // closing flushes the request
        }
    }
}
