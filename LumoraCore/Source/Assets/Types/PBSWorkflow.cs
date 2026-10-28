// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// How a PBS surface is told what it reflects. Metallic derives the reflection from a metalness and the albedo;
// specular is handed it as a colour, with smoothness in that colour's alpha.
public enum PBSWorkflow
{
    Metallic,
    Specular
}
