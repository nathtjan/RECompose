// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using RECompose.Core.Settings;
using RECompose.Presentation.ViewModels;

namespace RECompose.App;

// Where the main window opens: its last place and size (when not maximised) and whether it was maximised come back at
// the next start. On Windows the place and size come from the system's own record of the restored window, so a
// maximise in progress can't be taken for them; elsewhere they are tracked, and a size counts only once it has settled.
public partial class MainWindow
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private WindowBounds? _normalBounds;
    private bool _maximized;
    private Size? _normalBorder;
    private DispatcherTimer? _settle;

    private void TrackPlacement()
    {
        // Open nearly as tall as the screen's free area (above the taskbar or dock), never below the minimum.
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
            Height = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - 48);

        _settle = new DispatcherTimer { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            NoteNormalBounds();
        };

        Opened += (_, _) => NoteNormalBounds();
        PositionChanged += (_, _) => _settle.Start();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ClientSizeProperty)
                _settle.Start(); // restarts: a drag or a maximise in progress isn't noted until it stops
            else if (e.Property == WindowStateProperty && WindowState is WindowState.Maximized or WindowState.Normal)
                _maximized = WindowState == WindowState.Maximized; // minimising changes neither
        };
        Closing += (_, _) =>
        {
            _settle.Stop();
            NoteNormalBounds();
            // A window snapped into a layout is restored but Windows still keeps its place from before the snap, so only a
            // maximised or minimised window takes the system's record.
            var bounds = WindowState == WindowState.Normal ? _normalBounds : SystemNormalBounds() ?? _normalBounds;
            if (bounds is not null && ViewModel is { } vm)
                vm.SaveWindow(bounds, _maximized);
        };
    }

    private void NoteNormalBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
            return;
        var bounds = new WindowBounds(Position.X, Position.Y, ClientSize.Width, ClientSize.Height);
        if (FillsItsScreen(bounds)) // a normal window as big as the screen's free area is a maximise caught half-way
            return;
        _normalBounds = bounds;
        if (FrameSize is { } frame)
            _normalBorder = new Size(frame.Width - ClientSize.Width, frame.Height - ClientSize.Height); // differs from the maximised one
    }

    private bool FillsItsScreen(WindowBounds bounds) =>
        ScreenAt(bounds) is { } screen
        && bounds.Width * screen.Scaling >= screen.WorkingArea.Width - 2
        && bounds.Height * screen.Scaling >= screen.WorkingArea.Height - 2;

    // The last place and size come back when the window's title bar would still be on a screen (a monitor may have gone),
    // no bigger than that screen's free area; otherwise it opens centred, nearly as tall as the screen. Maximised, it
    // un-maximises to that place.
    private void RestorePlacement(MainViewModel vm)
    {
        if (vm.WindowBounds is { } saved && ScreenAt(saved) is { } screen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(saved.X, saved.Y);
            Width = Math.Max(MinWidth, Math.Min(saved.Width, screen.WorkingArea.Width / screen.Scaling));
            Height = Math.Max(MinHeight, Math.Min(saved.Height, screen.WorkingArea.Height / screen.Scaling));
            _normalBounds = saved with { Width = Width, Height = Height };
        }

        if (vm.WindowMaximized)
            WindowState = WindowState.Maximized;
        _maximized = vm.WindowMaximized;
    }

    /// <summary>The screen holding the window's title bar strip, or null when it is on none.</summary>
    private Screen? ScreenAt(WindowBounds bounds) =>
        Screens.All.FirstOrDefault(s => s.WorkingArea.Intersects(new PixelRect(bounds.X, bounds.Y, (int)(bounds.Width * s.Scaling), (int)(40 * s.Scaling))));

    /// <summary>
    /// Windows keeps the restored place and size of a window even while it is maximised; null elsewhere or when it can't
    /// be read. Its rectangle is the outer frame in workspace coordinates (offset by a taskbar at the top or left of the
    /// primary screen), so it is moved to screen coordinates and the restored border is taken off the size.
    /// </summary>
    private WindowBounds? SystemNormalBounds()
    {
        // The border is known only from the window seen restored; until then the place it opened at stands.
        if (!OperatingSystem.IsWindows() || _normalBorder is not { } border
            || TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle || Screens.Primary is not { } primary)
            return null;
        var placement = new Win32.WindowPlacement { Length = Marshal.SizeOf<Win32.WindowPlacement>() };
        if (!Win32.GetWindowPlacement(handle.Handle, ref placement))
            return null;

        var r = placement.NormalPosition;
        var offset = primary.WorkingArea.Position - primary.Bounds.Position;
        var scaling = Screens.ScreenFromPoint(new PixelPoint(r.Left + offset.X, r.Top + offset.Y))?.Scaling ?? primary.Scaling;
        return FromPlacement(new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), offset, scaling, border);
    }

    /// <summary>A restored frame in workspace pixels as the window's screen place and its client size in device-independent units.</summary>
    internal static WindowBounds FromPlacement(PixelRect frame, PixelPoint workspaceOffset, double scaling, Size border) =>
        new(frame.X + workspaceOffset.X, frame.Y + workspaceOffset.Y, frame.Width / scaling - border.Width, frame.Height / scaling - border.Height);

    private static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowPlacement
        {
            public int Length, Flags, ShowCmd;
            public int MinX, MinY, MaxX, MaxY;
            public Rect NormalPosition;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    }
}
