// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// What a UI material makes of its picture: the colours as they are, or a normal map shown as the directions
// it holds (each axis from -1..1 onto 0..1), for looking at one.
public enum UITextureMode
{
    Color,
    NormalMap
}
