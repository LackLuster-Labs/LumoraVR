// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

[System.Flags]
public enum FluidBodyRegions
{
    None = 0,
    Feet = 1,
    LowerLegs = 2,
    Hands = 4,
    Fingers = 8,
    Head = 16,
    Torso = 32,
    // every region: the whole body collides with and sticks to its own skin
    Body = Feet | LowerLegs | Hands | Fingers | Head | Torso,
}
