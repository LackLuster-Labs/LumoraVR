// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// How a toon outline takes light. None draws no hull whatever its width says, Lit shades the outline colour by
// the lights and the ambient the way a surface would be, Emissive draws it flat at full strength.
public enum ToonOutlineStyle
{
    None,
    Lit,
    Emissive
}
