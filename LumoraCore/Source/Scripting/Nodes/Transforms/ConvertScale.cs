// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Carries a scale from one slot's space into another's, axis by axis with the rotations left out, the way a
// slot's world scale is its own times every parent's. An axis a slot is flattened on divides as one rather
// than giving infinity. From and to work as they do on Convert Point.
[Node("Transform/Conversion/Convert Scale", Capability.WorldRead, Description = "Carries a scale from one slot's space into another's, axis by axis with the rotations left out, the way a slot's world scale is its own times every parent's.")]
public sealed class ConvertScale : ReadNode
{
    [return: Port("converted")]
    public static float3 Run(ScriptContext ctx, Slot? from, Slot? to, [Default(1)] float3 scale)
        => ScriptSpaces.Scale(ScriptSpaces.Space(ctx, from), ScriptSpaces.Space(ctx, to), scale);
}
