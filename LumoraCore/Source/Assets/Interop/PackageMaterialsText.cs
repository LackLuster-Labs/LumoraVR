// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// TEXT AND CANVAS MATERIALS. World text keeps the package's depth bias, depth test and glyph background on its own
// material. Canvas text draws through its canvas's shared text material here, so a canvas text material's look
// only reaches the screen when world text wears it as a style (StyleFromUITextK4); what it stores is kept for
// that copy. Which glyph method a material asks for is our atlas's to decide: our fonts are distance fields,
// and the text sets the method from the atlas it draws with. -xlinka
public static partial class PackageMemberMapper
{
    // A stored member with this name never lands; ApplyMatsText takes it instead.
    private const string HandledMatsText = "\u0000matst";

    private sealed record UITextLookMats(float OffsetFactor, float OffsetUnits, bool AutoBackground, colorHDR? Background);

    private static readonly ConditionalWeakTable<UITextMaterial, UITextLookMats> UITextLooksMats = new();

    static void MemberAliasesMatsText(Dictionary<string, Dictionary<string, string>> table)
    {
        MergeMats(table, "TextMaterial", ("FontAtlas", "Texture"), ("GlyphRenderMethod", HandledMatsText));
        MergeMats(table, "UITextMaterial", ("FontAtlas", "Texture"), ("GlyphRenderMethod", HandledMatsText),
            ("OffsetFactor", HandledMatsText), ("OffsetUnits", HandledMatsText),
            ("AutoBackgroundColor", HandledMatsText), ("BackgroundColor", HandledMatsText),
            ("Overlay", HandledMatsText), ("OverlayTint", HandledMatsText));
        MergeMats(table, "UIUnlitMaterial", ("Overlay", "TintHidden"), ("OverlayTint", "HiddenTint"),
            ("TextureMode", HandledMatsText));
    }

    static void ApplyMatsText(Component component, DataTreeDictionary data)
    {
        switch (component)
        {
            case TextMaterial text:
                WarnGlyphMethodMats(text, data);
                break;
            case UITextMaterial uiText:
                WarnGlyphMethodMats(uiText, data);
                var look = new UITextLookMats(
                    StoredMats(data, "OffsetFactor") is IConvertible factor and not string ? Convert.ToSingle(factor, System.Globalization.CultureInfo.InvariantCulture) : 0f,
                    StoredMats(data, "OffsetUnits") is IConvertible units and not string ? Convert.ToSingle(units, System.Globalization.CultureInfo.InvariantCulture) : 0f,
                    StoredMats(data, "AutoBackgroundColor") is not false,
                    StoredColorMats(data, "BackgroundColor"));
                UITextLooksMats.AddOrUpdate(uiText, look);
                if (StoredMats(data, "Overlay") is true)
                    Logging.Logger.Warn($"Package import: UITextMaterial on '{component.Slot?.Name}' tints its letters where they are hidden; text here has no hidden tint, so they draw untinted.");
                break;
            case UIUnlitMaterial ui:
                if (StoredMats(data, "TextureMode") is string mode)
                {
                    ui.TextureMode.Value = mode == "NormalMap" ? UITextureMode.NormalMap : UITextureMode.Color;
                    // The two colours it blends between are a pair per vertex the canvas does not make here.
                    if (mode == "LerpColor")
                        Logging.Logger.Warn($"Package import: UIUnlitMaterial on '{component.Slot?.Name}' blends two vertex colours by its picture; ours shows the picture's colours.");
                }
                break;
        }
    }

    // A canvas text material's look on the world text style made from it.
    private static void CarryUITextLookMats(UITextMaterial from, TextMaterial style)
    {
        if (!UITextLooksMats.TryGetValue(from, out var look))
            return;
        style.OffsetFactor.Value = look.OffsetFactor;
        style.OffsetUnits.Value = look.OffsetUnits;
        style.AutoBackgroundColor.Value = look.AutoBackground;
        if (look.Background is { } background)
            style.BackgroundColor.Value = background;
    }

    private static void WarnGlyphMethodMats(Component component, DataTreeDictionary data)
    {
        if (StoredMats(data, "GlyphRenderMethod") is string method && method != "MSDF")
            Logging.Logger.Warn($"Package import: {component.GetType().Name} on '{component.Slot?.Name}' asks for {method} glyphs; ours draw from the font's distance-field atlas.");
    }

    private static colorHDR? StoredColorMats(DataTreeDictionary data, string name)
        => (data.TryGetNode(name) as DataTreeDictionary)?.TryGetNode("Data") is { } node
            && TryConvert(node, typeof(colorHDR), out var value, static url => url, name) && value is colorHDR colour
            ? colour
            : null;
}
