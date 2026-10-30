// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using RECompose.App.Controls;

namespace RECompose.App.Dialogs;

/// <summary>
/// A dialog drawn like the main window: its title and a close key sit in our own title bar instead of the system's.
/// macOS keeps its native title bar, as the main window does.
/// </summary>
public class DialogWindow : Window
{
    private const double TitleBarHeight = 40;
    private bool _framed;

    public DialogWindow()
    {
        if (OperatingSystem.IsMacOS())
            return;
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
    }

    protected override Type StyleKeyOverride => typeof(Window);

    // The content is set by the dialog (in XAML or in code) after construction, so the bar is added once the template is up.
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_framed || OperatingSystem.IsMacOS() || Content is not Control body)
            return;
        _framed = true;

        var title = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        var close = new CaptionButtons(closeOnly: true);
        DockPanel.SetDock(close, Dock.Right);
        var bar = new Border
        {
            Classes = { "topbar" },
            Height = TitleBarHeight,
            Padding = new Thickness(16, 0, 0, 0),
            Child = new DockPanel { Children = { close, title } },
        };
        DockPanel.SetDock(bar, Dock.Top);

        Content = null;
        Content = new DockPanel { Children = { bar, body } };
    }
}
