// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace RECompose.App.Theme;

/// <summary>
/// Points the Fluent theme's own resource keys at Instrument tokens, so stock controls
/// (buttons, fields, selects, sliders, tabs) take the design system's colours without
/// restyling their templates. Keys are Fluent's; values are token names.
/// </summary>
public static class FluentMapping
{
    private static readonly (string FluentKey, string Token)[] Brushes =
    [
        ("ButtonBackground", "key"),
        ("ButtonBackgroundPointerOver", "key-hover"),
        ("ButtonBackgroundPressed", "key-pressed"),
        ("ButtonBackgroundDisabled", "bg-surface"),
        ("ButtonForeground", "text-primary"),
        ("ButtonForegroundPointerOver", "text-primary"),
        ("ButtonForegroundPressed", "text-primary"),
        ("ButtonForegroundDisabled", "text-disabled"),
        ("ButtonBorderBrush", "key-edge"),
        ("ButtonBorderBrushPointerOver", "key-edge"),
        ("ButtonBorderBrushPressed", "key-edge"),
        ("ToggleButtonBackground", "key"),
        ("ToggleButtonBackgroundPointerOver", "key-hover"),
        ("ToggleButtonBackgroundPressed", "key-pressed"),
        ("ToggleButtonForeground", "text-primary"),
        ("ToggleButtonBorderBrush", "key-edge"),
        ("ToggleButtonBackgroundChecked", "bg-raised"),
        ("ToggleButtonBackgroundCheckedPointerOver", "bg-raised"),
        ("ToggleButtonBackgroundCheckedPressed", "key-pressed"),
        ("ToggleButtonForegroundChecked", "text-primary"),
        ("ToggleButtonForegroundCheckedPointerOver", "text-primary"),
        ("ToggleButtonBorderBrushChecked", "control-border"),
        ("TextControlBackground", "bg-inset"),
        ("TextControlBackgroundPointerOver", "bg-inset"),
        ("TextControlBackgroundFocused", "bg-inset"),
        ("TextControlBorderBrush", "control-border"),
        ("TextControlBorderBrushPointerOver", "text-secondary"),
        ("TextControlBorderBrushFocused", "focus-ring"),
        ("TextControlForeground", "text-primary"),
        ("TextControlForegroundPointerOver", "text-primary"),
        ("TextControlForegroundFocused", "text-primary"),
        ("TextControlPlaceholderForeground", "text-secondary"),
        ("TextControlPlaceholderForegroundPointerOver", "text-secondary"),
        ("TextControlPlaceholderForegroundFocused", "text-secondary"),
        ("ComboBoxBackground", "bg-inset"),
        ("ComboBoxBackgroundPointerOver", "bg-raised"),
        ("ComboBoxBorderBrush", "control-border"),
        ("ComboBoxBorderBrushPointerOver", "text-secondary"),
        ("ComboBoxForeground", "text-primary"),
        ("ComboBoxDropDownBackground", "bg-raised"),
        ("ComboBoxDropDownBorderBrush", "border"),
        ("SliderTrackFill", "bg-inset"),
        ("SliderTrackFillPointerOver", "bg-inset"),
        ("SliderTrackValueFill", "text-secondary"),
        ("SliderTrackValueFillPointerOver", "text-primary"),
        ("SliderThumbBackground", "text-primary"),
        ("SliderThumbBackgroundPointerOver", "text-primary"),
        ("TabItemHeaderForegroundSelected", "text-primary"),
        ("TabItemHeaderForegroundUnselected", "text-secondary"),
        ("TabItemHeaderForegroundUnselectedPointerOver", "text-primary"),
        ("TabItemHeaderSelectedPipeFill", "accent"),
        ("SystemControlHighlightListAccentLowBrush", "bg-raised"),
        ("SystemControlHighlightListAccentMediumBrush", "bg-raised"),
        ("SystemControlHighlightListLowBrush", "key-hover"),
    ];

    /// <summary>Adds the mapping to each theme dictionary already installed by <see cref="Tokens.Install"/>.</summary>
    public static void Install(ResourceDictionary resources)
    {
        foreach (var (variant, provider) in resources.ThemeDictionaries)
        {
            if (provider is not ResourceDictionary theme)
                continue;
            foreach (var (fluentKey, token) in Brushes)
                theme[fluentKey] = new SolidColorBrush(Tokens.ColorOf(token, variant));

            // Focus, selection and progress inside stock controls: the pending blue, so the
            // REC yellow stays reserved for the one primary key (design system: one yellow key).
            theme["SystemAccentColor"] = Tokens.ColorOf("state-pending", variant);
        }
    }
}
