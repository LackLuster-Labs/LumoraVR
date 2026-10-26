// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

// Which views a view material override draws in. OwnEyes is the wearer looking out of their own head, and only
// counts while the override sits under that wearer; OtherViews is everything that films the scene and is not an
// eye: mirrors, portals and cameras alike.
public enum ViewMaterialScope
{
    OwnEyes,
    OtherViews
}
