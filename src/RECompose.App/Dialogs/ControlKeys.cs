// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Input;

namespace RECompose.App.Dialogs;

/// <summary>Names a key press for the control map: "PageDown", "G", "Ctrl+Shift+Space" (LIVE-51).</summary>
public static class ControlKeys
{
    public static string? Name(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
            return null;

        var name = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)e.Key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => e.Key.ToString(),
        };

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        parts.Add(name);
        return string.Join('+', parts);
    }
}
