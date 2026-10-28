// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// How a picture's colour channels were stored. Slot leaves it to whatever reads it: colour slots take
// the pixels as sRGB, data slots (metalness, occlusion, masks) as they are. Alpha is always linear.
public enum TextureProfile
{
    Slot,
    Linear,
    sRGB
}
