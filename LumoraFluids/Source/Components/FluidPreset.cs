// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// A substance's starting point. Picking one writes every field; any dial moved afterwards reads as Custom.
public enum FluidPreset
{
    Custom = 0,
    Water = 1,
    Oil = 2,
    Paint = 3,
    Honey = 4,
    Slime = 5,
    Glue = 6,
    Mud = 7,
}
