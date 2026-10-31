// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;

namespace RECompose.App;

// On Windows a window that covers its whole monitor counts as full screen, so a taskbar set to hide itself never comes
// back up over it. A screen with such a taskbar sets no space aside for it, so the maximised window stops 2 px short of
// the taskbar's edge, where the pointer reveals it. Full screen still covers the taskbar.
public partial class MainWindow
{
    private const int TaskbarStrip = 2;

    // Kept in a field so the collector can't take it while Windows still calls it.
    private static readonly Taskbar.SubclassProc LeaveTaskbarStrip = OnWindowMessage;

    // The window whose close key the left button went down on; 0 for none.
    private static nint _closePressed;

    private void LeaveRoomForHiddenTaskbar()
    {
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
            Taskbar.SetWindowSubclass(handle.Handle, LeaveTaskbarStrip, 1, 0);
    }

    // Avalonia places the window and sizes its client area first; the strip comes off what it settled on. The frame of a
    // maximised window reaches past the monitor, so both the frame and the client area stop at the strip.
    private static nint OnWindowMessage(nint hwnd, uint msg, nint wParam, nint lParam, nint id, nint data)
    {
        // The close key reaches the screen's corner when maximised, and Windows reports that pixel as the close key, but
        // the click there never reached the key. Every click on it closes here: pressed and released on it, as a key does.
        var (pressed, close, handled) = CloseKeyClick(_closePressed == hwnd, msg, wParam);
        _closePressed = pressed ? hwnd : 0;
        if (close)
            Taskbar.PostMessage(hwnd, Taskbar.WmSysCommand, Taskbar.ScClose, 0);
        if (handled)
            return 0;

        if (msg == Taskbar.WmWindowPosChanging)
        {
            var result = Taskbar.DefSubclassProc(hwnd, msg, wParam, lParam);
            var pos = Marshal.PtrToStructure<Taskbar.WindowPos>(lParam);
            var frame = new Taskbar.Rect { Left = pos.X, Top = pos.Y, Right = pos.X + pos.Width, Bottom = pos.Y + pos.Height };
            if ((pos.Flags & (Taskbar.SwpNoSize | Taskbar.SwpNoMove)) == 0 && StripFor(hwnd, frame) is var (monitor, strip))
            {
                frame = Pulled(frame, monitor, strip);
                (pos.X, pos.Y, pos.Width, pos.Height) = (frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
                Marshal.StructureToPtr(pos, lParam, false);
            }
            return result;
        }

        if (msg == Taskbar.WmNcCalcSize && wParam != 0)
        {
            var frame = Marshal.PtrToStructure<Taskbar.Rect>(lParam); // the first rectangle of NCCALCSIZE_PARAMS: the new frame
            var result = Taskbar.DefSubclassProc(hwnd, msg, wParam, lParam);
            if (StripFor(hwnd, frame) is var (monitor, strip))
            {
                var client = Marshal.PtrToStructure<Taskbar.Rect>(lParam); // now the client area
                var edge = Pulled(frame, monitor, strip);
                if (strip.Left > 0) client.Left = edge.Left;
                if (strip.Top > 0) client.Top = edge.Top;
                if (strip.Right > 0) client.Right = edge.Right;
                if (strip.Bottom > 0) client.Bottom = edge.Bottom;
                Marshal.StructureToPtr(client, lParam, false);
            }
            return result;
        }

        return Taskbar.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// A left button message against the close key: whether the button is now down on it, whether the window closes, and
    /// whether the message is used up. Down and up both on the key close; down on it and up elsewhere doesn't.
    /// </summary>
    internal static (bool Pressed, bool Close, bool Handled) CloseKeyClick(bool pressed, uint msg, nint hit) => msg switch
    {
        Taskbar.WmNcLButtonDown when hit == Taskbar.HtClose => (true, false, true),
        Taskbar.WmNcLButtonUp when hit == Taskbar.HtClose => (false, pressed, true),
        Taskbar.WmNcLButtonDown or Taskbar.WmNcLButtonUp or Taskbar.WmLButtonUp => (false, false, false),
        _ => (pressed, false, false),
    };

    /// <summary>A rectangle's edges on the taskbar's sides, kept the strip inside the monitor.</summary>
    private static Taskbar.Rect Pulled(Taskbar.Rect r, Taskbar.Rect monitor, Taskbar.Rect strip) => new()
    {
        Left = strip.Left > 0 ? Math.Max(r.Left, monitor.Left + strip.Left) : r.Left,
        Top = strip.Top > 0 ? Math.Max(r.Top, monitor.Top + strip.Top) : r.Top,
        Right = strip.Right > 0 ? Math.Min(r.Right, monitor.Right - strip.Right) : r.Right,
        Bottom = strip.Bottom > 0 ? Math.Min(r.Bottom, monitor.Bottom - strip.Bottom) : r.Bottom,
    };

    /// <summary>
    /// For a maximised window, the monitor its frame is on and the strip it leaves on each edge with a hidden taskbar,
    /// in pixels; null when it leaves none.
    /// </summary>
    private static (Taskbar.Rect Monitor, Taskbar.Rect Strip)? StripFor(nint hwnd, Taskbar.Rect frame)
    {
        var info = new Taskbar.MonitorInfo { Size = Marshal.SizeOf<Taskbar.MonitorInfo>() };
        if (!Taskbar.IsZoomed(hwnd) || !Taskbar.GetMonitorInfo(Taskbar.MonitorFromRect(ref frame, 2 /* nearest */), ref info))
            return null;

        int On(uint edge)
        {
            var bar = new Taskbar.AppBarData { Size = Marshal.SizeOf<Taskbar.AppBarData>(), Edge = edge, Bounds = info.Monitor };
            return Taskbar.SHAppBarMessage(11 /* ABM_GETAUTOHIDEBAREX */, ref bar) != 0 ? TaskbarStrip : 0;
        }

        var strip = new Taskbar.Rect { Left = On(0), Top = On(1), Right = On(2), Bottom = On(3) };
        return strip is { Left: 0, Top: 0, Right: 0, Bottom: 0 } ? null : (info.Monitor, strip);
    }

    private static class Taskbar
    {
        public const uint WmWindowPosChanging = 0x0046, WmNcCalcSize = 0x0083;
        public const uint WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2, WmLButtonUp = 0x0202, WmSysCommand = 0x0112;
        public const nint HtClose = 20, ScClose = 0xF060;
        public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002;

        public delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nint id, nint data);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowPos
        {
            public nint Hwnd, InsertAfter;
            public int X, Y, Width, Height;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, Work;
            public int Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AppBarData
        {
            public int Size;
            public nint Hwnd;
            public uint CallbackMessage, Edge;
            public Rect Bounds;
            public nint Param;
        }

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

        [DllImport("comctl32.dll")]
        public static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(nint hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromRect(ref Rect rect, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

        [DllImport("shell32.dll")]
        public static extern nint SHAppBarMessage(uint message, ref AppBarData data);
    }
}
