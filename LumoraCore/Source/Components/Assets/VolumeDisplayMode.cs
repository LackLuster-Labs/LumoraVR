// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// How a VolumeTextureMaterial turns the steps of its march into a colour: every step added, steps added until
// they are bright enough, or only the first step that clears a threshold.
public enum VolumeDisplayMode
{
    Additive,
    AdditiveCutoff,
    HitThreshold
}
