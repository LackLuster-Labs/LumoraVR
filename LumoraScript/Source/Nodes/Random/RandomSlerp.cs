// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A random rotation on the shorter turn from a to b, every angle along it as likely. A zero rotation
// counts as no turn.
[Node("Math/Random/Random Slerp", Description = "A random rotation on the shorter turn from a to b, every angle along it as likely.",
    Keywords = "random rotation between")]
public sealed class RandomSlerp : ReadNode
{
    [return: Port("rotation")]
    public static floatQ Run(ScriptContextBase ctx, [Default(0, 0, 0, 1)] floatQ a, [Default(0, 0, 0, 1)] floatQ b) => SphericalBlend.Rotations(a, b, ctx.Random.NextSingle());
}
