// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets;

// Extra features a generated PBS surface switches on in its body. Each is one #define.
[Flags]
public enum PBSSurfaceFeatures
{
    None = 0,
    Rim = 1 << 0,
    Intersect = 1 << 1,
    VertexAlbedo = 1 << 2,
    VertexEmissive = 1 << 3,
    VertexReflectance = 1 << 4,
    // Projected on three planes from the position instead of read off the mesh's UVs.
    Triplanar = 1 << 5,
}
