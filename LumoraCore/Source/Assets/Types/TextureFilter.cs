// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// How a material slot filters a picture. Slot leaves it to the slot; Point takes the nearest texel (pixel art
// stays square), Linear blends between texels and mips, Anisotropic also keeps it sharp at a glancing angle.
public enum TextureFilter
{
    Slot,
    Point,
    Linear,
    Anisotropic
}
