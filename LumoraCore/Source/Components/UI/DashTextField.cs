// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// A real text field in the dash's look, on a slot the caller has already placed: the field fill, a hairline
// that turns accent while it has the keyboard, the value in body text and the hint dimmed while empty.
// Typing, the caret, selection, paste and the headset keyboard all come from TextInput itself, so a dash
// field types exactly like every other field in the game. -xlinka
internal static class DashTextField
{
    public const float TextInset = 12f;
    public const int WorldNameLength = 64;

    public static TextInput Attach(Slot slot, IAssetProvider<FontSet>? font, float fontSize, string placeholder,
        int maxLength)
    {
        var panel = SettingsUI.Panel(slot, DashTheme.Field, DashTheme.Outline, DashTheme.RadiusControl);
        slot.AttachComponent<Mask>().ShowMaskGraphic.Value = true;
        var input = slot.AttachComponent<TextInput>();
        input.Placeholder.Value = placeholder;
        input.MaxLength.Value = maxLength;

        // TextInput finds its text by the child's name, so "Text" is load-bearing.
        var text = SettingsUI.Label(slot, "Text", font, fontSize, DashTheme.Text, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(TextInset, 0f), new float2(-TextInset, 0f));
        text.Ellipsis.Value = false;
        text.KeepCaretVisible.Value = true;
        text.CaretColor.Value = DashTheme.Text;
        text.SelectionColor.Value = DashTheme.Accent.WithAlpha(0.35f);

        void Paint()
        {
            if (panel.IsDestroyed || text.IsDestroyed)
                return;
            bool focused = input.IsFocused;
            SettingsUI.SetPaint(panel, DashTheme.Field, focused ? DashTheme.Accent : DashTheme.Outline);
            bool hint = !focused && string.IsNullOrEmpty(input.Text.Value);
            ListingStyle.SetTextColor(text, hint ? DashTheme.TextMuted
                : input.ReadOnly.Value ? DashTheme.TextDim : DashTheme.Text);
        }
        input.EditingStarted += _ => Paint();
        input.EditingChanged += (_, _) => Paint();
        input.FocusLost += (_, _) => Paint();
        input.Text.OnChanged += _ => Paint();
        input.ReadOnly.OnChanged += _ => Paint();
        Paint();
        return input;
    }
}
