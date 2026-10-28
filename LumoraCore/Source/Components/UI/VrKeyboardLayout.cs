// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The built-in keyboard's layout: a preview strip over four glyph rows and a modifier row, with the numeric pad
// and the arrow cluster down the right. Built the same way for the keyboard in the user's own space and for a
// copy put in a world to be changed (Create New > Object > Keyboard), so the copy starts as exactly what it
// replaces. It only builds; typing is VrKeyboard's, which takes whatever layout it finds up. -xlinka
public static class VrKeyboardLayout
{
    // Canvas units, and metres per unit on the panel.
    public const float PanelWidth = 800f;
    public const float PanelHeight = 276f;
    public const float PanelScale = 0.0005f;
    private const float Padding = 6f;
    private const float RowSpacing = 4f;
    private const float KeyGap = 3f;
    private const float PreviewHeight = 32f;
    private const float KeyHeight = 33f;
    private const float KeyWidth = 49f;
    private const float LabelSize = 16f;
    // The corner glyph on a dual-face key, as a fraction of the main face.
    private const float AltLabelScale = 0.55f;
    private const float PreviewSize = 15f;
    private const float CornerRadius = 8f;
    private const float LabelInset = 8f;

    // Right column: three keys wide, so the numpad and the arrow cluster share one width.
    private const float SideColumnWidth = KeyWidth * 3f + KeyGap * 2f;
    // Breathing room between the main grid and the right column.
    private const float SideColumnGap = 7f;
    private const float NumpadHeight = KeyHeight * 5f + RowSpacing * 4f;
    private const float ArrowsHeight = KeyHeight * 2f + RowSpacing;

    // Modifier row, left to right. Sums with the eight gaps to exactly the padded content width.
    private const float ShiftKeyWidth = 78f;
    private const float TabKeyWidth = 48f;
    private const float CopyKeyWidth = 52f;
    private const float SpaceKeyWidth = 130f;
    private const float PasteKeyWidth = 52f;
    private const float BackspaceKeyWidth = 78f;
    private const float EnterKeyWidth = 66f;
    private const float EscapeKeyWidth = 44f;
    private const float CloseKeyWidth = 56f;

    public static readonly color KeyFill = new color(0.22f, 0.20f, 0.34f, 1f);
    private static readonly color KeyBorder = new color(0.52f, 0.46f, 0.82f, 0.45f);
    private static readonly color PanelBorder = new color(0.52f, 0.46f, 0.82f, 0.55f);

    private static readonly string[] GlyphRows =
    {
        "1234567890-=",
        "qwertyuiop[]",
        "asdfghjkl;'\\",
        "zxcvbnm,./",
    };

    // Index-aligned with GlyphRows: row r, column c on one is the same key on the other.
    private static readonly string[] ShiftGlyphRows =
    {
        "!@#$%^&*()_+",
        "QWERTYUIOP{}",
        "ASDFGHJKL:\"|",
        "ZXCVBNM<>?",
    };

    // Numeric pad, top row first. These type themselves whether or not shift is up - there is no
    // second face for a number pad, and nobody wants a shifted 7 to come out as an ampersand.
    private static readonly string[] NumpadRows =
    {
        "789",
        "456",
        "123",
        "0.-",
        "+*/",
    };

    // The whole keyboard on root, which should be empty. The panel's own size goes on root's rect.
    public static KeyboardDesign Build(Slot root)
    {
        var theme = root.GetOrAttachComponent<UITheme>();
        // Borrow the dash's font provider when there is one: same world so the ref is legal, one glyph
        // atlas between the two panels, and the keys never come up blank because the theme's own
        // default font URL had not been registered yet. -xlinka
        var dash = UserspaceDashboard.LocalInstance;
        if (dash != null && !dash.IsDestroyed && ReferenceEquals(dash.World, root.World) && dash.Font.Target != null)
            theme.Font.Target = dash.Font.Target;
        theme.PanelBackground.Value = InspectorUI.PaneColor;
        theme.Header.Value = InspectorUI.HeaderColor;
        theme.ButtonFill.Value = KeyFill;
        theme.Accent.Value = InspectorUI.AccentColor;
        theme.Separator.Value = PanelBorder;
        theme.Border.Value = KeyBorder;
        theme.CornerRadius.Value = CornerRadius;

        Size(root.GetOrAttachComponent<RectTransform>());
        root.GetOrAttachComponent<Canvas>();

        // Grabbable and scalable so a user can put it where they like and size it to their reach
        // rather than ours.
        var grab = root.GetOrAttachComponent<Grabbable>();
        grab.AllowGrab.Value = true;
        grab.Scalable.Value = true;
        grab.FollowRotation.Value = true;
        grab.Receivable.Value = false;
        grab.KeepMomentum.Value = false;

        var background = root.GetOrAttachComponent<BorderedImage>();
        background.Tint.Value = InspectorUI.PaneColor;
        background.BorderTint.Value = PanelBorder;
        ApplyRounded(theme, background);

        // The theme parks its font and rounded-sprite providers on child slots of this one, so the
        // rows go in their own container - a layout on the root would try to lay those out as keys.
        var content = root.FindChildOrAdd("Content");
        var contentRect = content.GetOrAttachComponent<RectTransform>();
        contentRect.AnchorMin.Value = float2.Zero;
        contentRect.AnchorMax.Value = float2.One;
        contentRect.OffsetMin.Value = new float2(Padding, Padding);
        // Stops short of the right column, so the main grid's rows are the exact width they always
        // were and every row below keeps its own key sizes.
        contentRect.OffsetMax.Value = new float2(-(Padding + SideColumnWidth + SideColumnGap), -Padding);

        var column = content.AttachComponent<VerticalLayout>();
        column.Spacing.Value = RowSpacing;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;
        // The main grid is shorter than the right column (six rows against seven), so centre it rather
        // than leaving all the slack in one lump under the modifier row.
        column.MainAlignment.Value = MainAxisAlignment.Center;

        var design = root.GetOrAttachComponent<KeyboardDesign>();
        design.Preview.Target = BuildPreviewStrip(theme, content);
        for (int i = 0; i < GlyphRows.Length; i++)
            BuildGlyphRow(theme, content, i);
        BuildModifierRow(theme, content);
        BuildSideColumn(theme, root);
        return design;
    }

    public static void Size(RectTransform rect)
    {
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(PanelWidth * -0.5f, PanelHeight * -0.5f);
        rect.OffsetMax.Value = new float2(PanelWidth * 0.5f, PanelHeight * 0.5f);
    }

    // The right-hand block: numeric pad anchored to the top, arrow cluster to the bottom. Two
    // independently anchored containers rather than one long layout, so the gap between the two blocks
    // is deliberate instead of whatever the leftover space happened to be. -xlinka
    private static void BuildSideColumn(UITheme theme, Slot root)
    {
        var side = root.FindChildOrAdd("SideColumn");
        var sideRect = side.GetOrAttachComponent<RectTransform>();
        sideRect.AnchorMin.Value = new float2(1f, 0f);
        sideRect.AnchorMax.Value = new float2(1f, 1f);
        sideRect.OffsetMin.Value = new float2(-(Padding + SideColumnWidth), Padding);
        sideRect.OffsetMax.Value = new float2(-Padding, -Padding);

        var numpad = side.AddSlot("Numpad");
        var numpadRect = numpad.AttachComponent<RectTransform>();
        numpadRect.AnchorMin.Value = new float2(0f, 1f);
        numpadRect.AnchorMax.Value = new float2(1f, 1f);
        numpadRect.OffsetMin.Value = new float2(0f, -NumpadHeight);
        numpadRect.OffsetMax.Value = float2.Zero;
        var numpadColumn = numpad.AttachComponent<VerticalLayout>();
        numpadColumn.Spacing.Value = RowSpacing;
        numpadColumn.ForceExpandWidth.Value = true;
        numpadColumn.ForceExpandHeight.Value = false;

        for (int i = 0; i < NumpadRows.Length; i++)
        {
            var row = BuildRow(numpad, "Numpad" + (i + 1), KeyHeight);
            AddKeyLayout(row);
            string glyphs = NumpadRows[i];
            for (int c = 0; c < glyphs.Length; c++)
            {
                string glyph = glyphs[c].ToString();
                // Same glyph on both faces: the pad types literally whatever shift is doing.
                BuildKey(theme, row, "Num" + glyph, glyph, glyph, VrKeyRole.Character, KeyWidth, KeyFill);
            }
        }

        var arrows = side.AddSlot("Arrows");
        var arrowsRect = arrows.AttachComponent<RectTransform>();
        arrowsRect.AnchorMin.Value = new float2(0f, 0f);
        arrowsRect.AnchorMax.Value = new float2(1f, 0f);
        arrowsRect.OffsetMin.Value = float2.Zero;
        arrowsRect.OffsetMax.Value = new float2(0f, ArrowsHeight);
        var arrowColumn = arrows.AttachComponent<VerticalLayout>();
        arrowColumn.Spacing.Value = RowSpacing;
        arrowColumn.ForceExpandWidth.Value = true;
        arrowColumn.ForceExpandHeight.Value = false;

        // Inverted T: up on its own over left/down/right, which is the shape every hand already knows
        // where to reach on.
        var top = BuildRow(arrows, "ArrowsTop", KeyHeight);
        AddKeyLayout(top);
        BuildKey(theme, top, "ArrowUp", "↑", string.Empty, VrKeyRole.ArrowUp, KeyWidth, KeyFill);

        var bottom = BuildRow(arrows, "ArrowsBottom", KeyHeight);
        AddKeyLayout(bottom);
        BuildKey(theme, bottom, "ArrowLeft", "←", string.Empty, VrKeyRole.ArrowLeft, KeyWidth, KeyFill);
        BuildKey(theme, bottom, "ArrowDown", "↓", string.Empty, VrKeyRole.ArrowDown, KeyWidth, KeyFill);
        BuildKey(theme, bottom, "ArrowRight", "→", string.Empty, VrKeyRole.ArrowRight, KeyWidth, KeyFill);
    }

    private static Text BuildPreviewStrip(UITheme theme, Slot content)
    {
        var strip = BuildRow(content, "Preview", PreviewHeight);

        var backing = strip.AttachComponent<BorderedImage>();
        backing.Tint.Value = InspectorUI.RowColor;
        backing.BorderTint.Value = KeyBorder;
        ApplyRounded(theme, backing);

        var labelSlot = strip.AddSlot("Text");
        var labelRect = labelSlot.AttachComponent<RectTransform>();
        labelRect.AnchorMin.Value = float2.Zero;
        labelRect.AnchorMax.Value = float2.One;
        labelRect.OffsetMin.Value = new float2(LabelInset, 0f);
        labelRect.OffsetMax.Value = new float2(-LabelInset, 0f);

        var text = labelSlot.AttachComponent<Text>();
        text.Content.Value = string.Empty;
        text.Size.Value = PreviewSize;
        text.Color.Value = InspectorUI.TextColor;
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        ApplyFont(theme, text);
        return text;
    }

    private static void BuildGlyphRow(UITheme theme, Slot content, int index)
    {
        var row = BuildRow(content, "Row" + (index + 1), KeyHeight);
        AddKeyLayout(row);

        string glyphs = GlyphRows[index];
        string shifted = ShiftGlyphRows[index];
        for (int i = 0; i < glyphs.Length; i++)
        {
            string glyph = glyphs[i].ToString();
            string shiftGlyph = i < shifted.Length ? shifted[i].ToString() : glyph;
            BuildKey(theme, row, glyph, glyph, shiftGlyph, VrKeyRole.Character, KeyWidth, KeyFill);
        }
    }

    private static void BuildModifierRow(UITheme theme, Slot content)
    {
        var row = BuildRow(content, "Modifiers", KeyHeight);
        AddKeyLayout(row);

        BuildKey(theme, row, "Shift", "Shift", string.Empty, VrKeyRole.Shift, ShiftKeyWidth, KeyFill);
        BuildKey(theme, row, "Tab", "Tab", string.Empty, VrKeyRole.Tab, TabKeyWidth, KeyFill);
        BuildKey(theme, row, "Copy", "Copy", string.Empty, VrKeyRole.Copy, CopyKeyWidth, KeyFill);
        BuildKey(theme, row, "Space", "Space", string.Empty, VrKeyRole.Space, SpaceKeyWidth, KeyFill);
        BuildKey(theme, row, "Paste", "Paste", string.Empty, VrKeyRole.Paste, PasteKeyWidth, KeyFill);
        BuildKey(theme, row, "Backspace", "Back", string.Empty, VrKeyRole.Backspace, BackspaceKeyWidth, KeyFill);
        BuildKey(theme, row, "Enter", "Enter", string.Empty, VrKeyRole.Enter, EnterKeyWidth, InspectorUI.AccentColor);
        BuildKey(theme, row, "Escape", "Esc", string.Empty, VrKeyRole.Escape, EscapeKeyWidth, KeyFill);
        BuildKey(theme, row, "Close", "Close", string.Empty, VrKeyRole.Close, CloseKeyWidth, InspectorUI.DangerColor);
    }

    private static Slot BuildRow(Slot content, string name, float height)
    {
        var row = content.AddSlot(name);
        row.AttachComponent<RectTransform>();
        var element = row.AttachComponent<LayoutElement>();
        element.MinHeight.Value = height;
        element.PreferredHeight.Value = height;
        element.FlexibleHeight.Value = 0f;
        element.FlexibleWidth.Value = 1f;
        // Own chunk per row: a shift flip rewrites four rows of labels, and the preview strip changes
        // on every keystroke. Chunked, each of those re-meshes one strip instead of the whole panel.
        row.AttachComponent<GraphicChunkRoot>();
        return row;
    }

    // Rows are narrower than the panel by design (twelve keys, then ten), so they centre rather than
    // leaving a ragged gutter down one side.
    private static void AddKeyLayout(Slot row)
    {
        var layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = KeyGap;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        layout.MainAlignment.Value = MainAxisAlignment.Center;
    }

    private static void BuildKey(UITheme theme, Slot row, string name, string glyph, string shiftGlyph, VrKeyRole role,
        float width, color fill)
    {
        var slot = row.AddSlot(name);
        slot.AttachComponent<RectTransform>();
        var element = slot.AttachComponent<LayoutElement>();
        element.MinWidth.Value = width;
        element.PreferredWidth.Value = width;
        element.FlexibleWidth.Value = 0f;
        element.MinHeight.Value = KeyHeight;
        element.PreferredHeight.Value = KeyHeight;
        element.FlexibleHeight.Value = 0f;

        var backing = slot.AttachComponent<BorderedImage>();
        backing.Tint.Value = fill;
        backing.BorderTint.Value = KeyBorder;
        ApplyRounded(theme, backing);

        var button = slot.AttachComponent<Button>();
        // Button.OnAttach only adopts a plain Image; the rounded keys use BorderedImage, so the
        // hover/press driver has to be pointed at the tint by hand or the keys never light up.
        button.SetupBackgroundColor(backing.Tint);
        // Every point of the key grid is a key: each answers out to the middle of the row gap around it,
        // which also covers the narrower gap between keys (the right-hand key takes the sliver they share).
        button.HitOutset.Value = RowSpacing * 0.5f;

        var key = slot.AttachComponent<VrKey>();
        key.Glyph.Value = glyph;
        key.ShiftGlyph.Value = shiftGlyph;
        key.Role.Value = role;
        button.KeepPressUntilRelease.Value = key.Repeats;
        button.SetAction(key.OnPressed);

        key.Label.Target = BuildKeyFace(theme, slot, "Label", glyph, LabelSize, InspectorUI.TextColor,
            TextHorizontalAlignment.Center, TextVerticalAlignment.Middle, float2.Zero, float2.One,
            float2.Zero, float2.Zero);

        // Two genuinely different glyphs (a symbol key, not a letter) get a second small face in the
        // corner showing the one shift is NOT on, so you can read what a key does both ways without
        // pressing shift to find out. Letters skip it - a big 'a' with a little 'A' beside it is
        // noise. -xlinka
        if (key.HasDualFace)
        {
            key.AltLabel.Target = BuildKeyFace(theme, slot, "AltLabel", key.ActiveGlyph(true), LabelSize * AltLabelScale,
                InspectorUI.MutedColor, TextHorizontalAlignment.Right, TextVerticalAlignment.Top,
                new float2(0.45f, 0.45f), float2.One, float2.Zero, new float2(-4f, -2f));
        }
    }

    private static Text BuildKeyFace(UITheme theme, Slot keySlot, string name, string content, float size, color tint,
        TextHorizontalAlignment horizontal, TextVerticalAlignment vertical,
        float2 anchorMin, float2 anchorMax, float2 offsetMin, float2 offsetMax)
    {
        var faceSlot = keySlot.AddSlot(name);
        var faceRect = faceSlot.AttachComponent<RectTransform>();
        faceRect.AnchorMin.Value = anchorMin;
        faceRect.AnchorMax.Value = anchorMax;
        faceRect.OffsetMin.Value = offsetMin;
        faceRect.OffsetMax.Value = offsetMax;

        var text = faceSlot.AttachComponent<Text>();
        text.Content.Value = content;
        text.Size.Value = size;
        text.Color.Value = tint;
        text.HorizontalAlignment.Value = horizontal;
        text.VerticalAlignment.Value = vertical;
        ApplyFont(theme, text);
        return text;
    }

    private static void ApplyRounded(UITheme theme, BorderedImage image)
    {
        image.Texture.Target = theme.RoundedSprite;
        image.NineSlice.Value = true;
        image.Borders.Value = new float4(CornerRadius, CornerRadius, CornerRadius, CornerRadius);
    }

    private static void ApplyFont(UITheme theme, Text text)
    {
        var font = theme.ThemeFont;
        if (font != null)
            text.Font.Target = font;
    }
}
