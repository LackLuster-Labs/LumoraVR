// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Everything about a screen filter that is not the filter itself: how it blends and culls, its depth write and
// test, its queue, and the canvas state it carries for use inside a UI.
public readonly record struct ScreenFilterState(
    BlendMode Blend,
    Culling Culling,
    ZWrite ZWrite,
    ZTest ZTest,
    int RenderQueue,
    Rect Rect,
    bool RectClip,
    ColorMask ColorMask,
    StencilComparison StencilComparison,
    StencilOperation StencilOperation,
    byte StencilID,
    byte StencilWriteMask,
    byte StencilReadMask);
