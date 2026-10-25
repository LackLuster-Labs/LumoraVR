// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Assets;

// How TextureSizeValue states a picture's size: in pixels, scaled to a height of one, to a width of one, or as a
// length of one.
public enum TextureSizeMode
{
    Absolute,
    UnitHeight,
    UnitWidth,
    Normalized,
}
