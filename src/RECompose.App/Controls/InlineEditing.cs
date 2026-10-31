// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace RECompose.App.Controls;

/// <summary>Double-click a readout to type its value (PB-60), in Prepare's header and in Perform's key deck.</summary>
internal static class InlineEditing
{
    /// <summary>
    /// The readout swaps for a field: Enter or leaving it applies a changed value, Esc keeps the old one. <paramref name="owner"/>
    /// sees the clicks that end the edit; <paramref name="focusTarget"/> takes the focus back so the single-key shortcuts work (KEY-1).
    /// </summary>
    public static void Attach(Control owner, Func<InputElement?> focusTarget, TextBlock view, TextBox edit, Action<string> commit)
    {
        // A click on something that takes no focus (a panel, a label) doesn't end the edit by itself.
        owner.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (edit.IsVisible && !(e.Source is Avalonia.Visual source && (source == edit || edit.IsVisualAncestorOf(source))))
                Close(true);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        view.DoubleTapped += (_, e) =>
        {
            // The field takes the readout's place exactly: the readout keeps its space (only hidden), the field its width.
            edit.Text = view.Text;
            edit.Width = view.Bounds.Width;
            view.Opacity = 0;
            edit.IsVisible = true;
            edit.Focus();
            edit.SelectAll();
            e.Handled = true;
        };
        edit.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Close(e.Key == Key.Enter);
                e.Handled = true;
            }
        };
        edit.LostFocus += (_, _) => Close(true);

        void Close(bool apply)
        {
            if (!edit.IsVisible)
                return; // hiding the field moves focus away, which calls this again
            edit.IsVisible = false;
            view.Opacity = 1;
            if (edit.IsFocused)
                focusTarget()?.Focus(); // a hidden field keeping focus would turn the single-key shortcuts off
            if (apply && edit.Text is { } text && text.Trim() != view.Text?.Trim())
                commit(text);
        }
    }
}
