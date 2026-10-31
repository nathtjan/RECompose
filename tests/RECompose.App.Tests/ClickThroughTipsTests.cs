// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using RECompose.App.Controls;

namespace RECompose.App.Tests;

// A tip lives in its own native window; it must let the mouse through to the button under it.
public sealed class ClickThroughTipsTests
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExLayered = 0x80000;

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int exStyle, string cls, string? title, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [Fact]
    public void A_native_window_on_Windows_is_made_transparent_to_the_mouse()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var hwnd = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        try
        {
            Assert.Equal(0, (long)GetWindowLongPtr(hwnd, GwlExStyle) & (WsExTransparent | WsExLayered));

            Assert.True(ClickThroughTips.Apply("HWND", hwnd));

            Assert.Equal(WsExTransparent | WsExLayered, (long)GetWindowLongPtr(hwnd, GwlExStyle) & (WsExTransparent | WsExLayered));
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void A_handle_of_another_kind_is_left_alone()
    {
        Assert.False(ClickThroughTips.Apply("Something", 1));
        Assert.False(ClickThroughTips.Apply(null, 1));
    }
}
