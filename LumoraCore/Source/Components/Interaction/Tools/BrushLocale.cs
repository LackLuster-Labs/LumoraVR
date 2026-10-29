// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Components.Interaction;

public static class BrushLocale
{
    public static readonly LocaleText Tip = LocaleText.Keyed("Tools.Brush.Tip", "Tip");
    public static readonly LocaleText Colour = LocaleText.Keyed("Tools.Brush.Colour", "Colour");
    public static readonly LocaleText Picker = LocaleText.Keyed("Tools.Brush.Picker", "Picker");
    public static readonly LocaleText Width = LocaleText.Keyed("Tools.Brush.Width", "Width {0}");
    public static readonly LocaleText Size = LocaleText.Keyed("Tools.Brush.Size", "Size {0}");
    public static readonly LocaleText Smooth = LocaleText.Keyed("Tools.Brush.Smooth", "Smooth {0}");
    public static readonly LocaleText Mine = LocaleText.Keyed("Tools.Brush.Mine", "only your own strokes rub out");

    public static LocaleText TipName(BrushTip tip) => tip switch
    {
        BrushTip.Ribbon => LocaleText.Keyed("Tools.Brush.Tip.Ribbon", "Ribbon"),
        BrushTip.Tube => LocaleText.Keyed("Tools.Brush.Tip.Tube", "Tube"),
        BrushTip.Neon => LocaleText.Keyed("Tools.Brush.Tip.Neon", "Neon"),
        BrushTip.Spline => LocaleText.Keyed("Tools.Brush.Tip.Spline", "Spline"),
        BrushTip.Confetti => LocaleText.Keyed("Tools.Brush.Tip.Confetti", "Confetti"),
        BrushTip.Fill => LocaleText.Keyed("Tools.Brush.Tip.Fill", "Fill"),
        _ => LocaleText.Keyed("Tools.Brush.Tip.Eraser", "Eraser"),
    };

    // What the hub says under a tip while it is hovered.
    public static LocaleText TipDetail(BrushTip tip) => tip switch
    {
        BrushTip.Ribbon => LocaleText.Keyed("Tools.Brush.Detail.Ribbon", "flat, turns with the brush"),
        BrushTip.Tube => LocaleText.Keyed("Tools.Brush.Detail.Tube", "round, solid"),
        BrushTip.Neon => LocaleText.Keyed("Tools.Brush.Detail.Neon", "glowing line"),
        BrushTip.Spline => LocaleText.Keyed("Tools.Brush.Detail.Spline", "smooth, even wire"),
        BrushTip.Confetti => LocaleText.Keyed("Tools.Brush.Detail.Confetti", "scattered chips"),
        BrushTip.Fill => LocaleText.Keyed("Tools.Brush.Detail.Fill", "draw a loop, get a sheet"),
        _ => LocaleText.Keyed("Tools.Brush.Detail.Eraser", "rubs out your strokes"),
    };
}
