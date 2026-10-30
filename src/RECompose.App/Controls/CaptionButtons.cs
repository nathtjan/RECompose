// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace RECompose.App.Controls;

/// <summary>
/// Minimise, maximise or restore, and close for the window we draw ourselves (the top bars are the title bar). Each key
/// carries its window role, so Windows still offers snap layouts on maximise. macOS keeps its own traffic lights.
/// </summary>
public sealed class CaptionButtons : StackPanel
{
    private readonly Button? _maximize;
    private Window? _window;

    public CaptionButtons()
        : this(closeOnly: false)
    {
    }

    /// <summary>Dialogs have only the close key.</summary>
    public CaptionButtons(bool closeOnly)
    {
        Orientation = Orientation.Horizontal;
        IsVisible = !OperatingSystem.IsMacOS();
        if (!closeOnly)
        {
            Children.Add(Key("Minimise", "minus", WindowDecorationsElementRole.MinimizeButton, w => w.WindowState = WindowState.Minimized));
            Children.Add(_maximize = Key("Maximise", "maximize", WindowDecorationsElementRole.MaximizeButton,
                w => w.WindowState = w.WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal));
        }

        var close = Key("Close", "close", WindowDecorationsElementRole.CloseButton, w => w.Close());
        close.Classes.Add("close");
        Children.Add(close);
    }

    private Button Key(string name, string glyph, WindowDecorationsElementRole role, Action<Window> act)
    {
        var button = new Button { Name = name + "Button", Content = new Icon { Glyph = glyph, IconSize = 14 }, Classes = { "caption" } };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        WindowDecorationProperties.SetElementRole(button, role);
        button.Click += (_, _) => { if (_window != null) act(_window); };
        return button;
    }

    private void ShowState(WindowState state)
    {
        if (_maximize is null)
            return;
        var restore = state != WindowState.Normal;
        ((Icon)_maximize!.Content!).Glyph = restore ? "restore" : "maximize";
        AutomationProperties.SetName(_maximize, restore ? "Restore" : "Maximise");
        ToolTip.SetTip(_maximize, restore ? "Restore" : "Maximise");
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            ShowState((WindowState)e.NewValue!);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = this.FindAncestorOfType<Window>();
        if (_window == null)
            return;
        _window.PropertyChanged += OnWindowChanged;
        ShowState(_window.WindowState);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window != null)
            _window.PropertyChanged -= OnWindowChanged;
        _window = null;
    }
}
