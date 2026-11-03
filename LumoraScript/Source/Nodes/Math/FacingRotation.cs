// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The rotation that turns forward (+z) to face along forward, with up as near to up as it can.
// An up of zero means world up. Facing straight along up leaves no side to keep up on, so up then
// becomes the direction a thing tilted from level onto forward would have its top: looking down, +z.
// The library's LookRotation hands back the inverse, hence the flip.
[Node("Math/Rotations/Look Rotation", Description = "The rotation that turns forward (+z) to face along forward, with up as near to up as it can.",
    Keywords = "face direction, facing, aim, point toward, aim at")]
public sealed class FacingRotation : ValueNode
{
    [return: Port("rotation")]
    public static floatQ Run([Default(0, 0, 1)] float3 forward, [Default(0, 1, 0)] float3 up)
    {
        if (forward.LengthSquared < 1e-12f)
            return floatQ.Identity;
        if (up.LengthSquared < 1e-12f)
            up = float3.Up;
        var f = forward.Normalized;
        if (float3.Cross(up.Normalized, f).LengthSquared < 1e-10f)
            up = Turns.Square(f);
        return Turns.Unit(floatQ.LookRotation(f, up).Inverse);
    }
}
