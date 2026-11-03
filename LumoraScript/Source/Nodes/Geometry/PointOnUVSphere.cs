// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The point at uv on a sphere about zero, and the rotation there that faces out. u goes once round the
// equator from +x toward -z, the way the sphere mesh wraps it so a picture reads left to right from
// outside; v runs from the top pole at 0 to the bottom pole at 1, the way a picture's rows run top down.
// The rotation turns forward (+z) out from the middle and up (+y) along the surface toward the top pole,
// so a sign placed there stands upright.
[Node("Math/Geometry 3D/Point On UV Sphere", Description = "Finds a point and outward-facing rotation on a sphere from UV coordinates. V runs from the top pole to the bottom.")]
public sealed class PointOnUVSphere : ValueNode
{
    [return: Port("point")]
    public static float3 Run(float2 uv, out floatQ rotation, float radius = 1f)
    {
        float around = uv.x * (2f * MathF.PI);
        float down = uv.y * MathF.PI;
        float sinDown = MathF.Sin(down), cosDown = MathF.Cos(down);
        float sinAround = MathF.Sin(around), cosAround = MathF.Cos(around);

        var outward = new float3(sinDown * cosAround, cosDown, -sinDown * sinAround);
        var north = new float3(-cosDown * cosAround, sinDown, cosDown * sinAround);
        rotation = FacingRotation.Run(outward, north);
        return outward * radius;
    }
}
