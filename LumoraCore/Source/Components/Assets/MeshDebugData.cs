// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// Which vertex attribute a MeshDebugMaterial shows. TangentDir is the tangent's handedness, the sign that turns
// the bitangent; UV0s to UV3s are the four UV sets.
public enum MeshDebugData
{
    Position,
    Color,
    ColorAlpha,
    Normals,
    Tangents,
    TangentDir,
    Bitangents,
    UV0s,
    UV1s,
    UV2s,
    UV3s
}
