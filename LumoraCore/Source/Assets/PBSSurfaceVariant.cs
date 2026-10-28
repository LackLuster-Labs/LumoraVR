// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// Everything that decides which shader a generated PBS surface needs. Two equal variants compile to the same
// text, so the cache shares one compile between them however many materials wear it.
//
// Blend is the full six: Cutout discards, Alpha fades everything with alpha, Transparent is premultiplied (the
// diffuse fades, reflections and emission stay), Additive and Multiply lay the surface over what is behind it.
public readonly record struct PBSSurfaceVariant(
    string Body,
    bool Specular,
    BlendMode Blend,
    ZWrite ZWrite,
    ZTest ZTest,
    Culling Culling,
    bool DepthOffset,
    PBSSurfaceFeatures Features);
