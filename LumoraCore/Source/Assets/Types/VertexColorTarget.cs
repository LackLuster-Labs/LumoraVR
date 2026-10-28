// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// What a surface's vertex colours drive, if anything. Reflectance is the metallic and smoothness in the
// metallic workflow (the colour's average and its alpha) and the specular colour in the specular one.
public enum VertexColorTarget
{
    None,
    Albedo,
    Emissive,
    Reflectance
}
