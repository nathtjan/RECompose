// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using RECompose.Core.Settings;

namespace RECompose.App.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void The_restored_frame_from_Windows_becomes_the_screen_place_and_the_client_size()
    {
        // A taskbar 48 px tall at the top of the primary screen shifts workspace coordinates down by 48 px; at 150 %
        // the 2,400 × 1,500 px frame is 1,600 × 1,000, less a 2 × 2 border.
        var bounds = MainWindow.FromPlacement(new PixelRect(100, 50, 2400, 1500), new PixelPoint(0, 48), 1.5, new Size(2, 2));

        Assert.Equal(new WindowBounds(100, 98, 1598, 998), bounds);
    }
}
