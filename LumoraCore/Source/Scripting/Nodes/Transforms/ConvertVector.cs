// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Carries an offset from one slot's space into another's, turned and scaled but not moved, so its length
// changes with the scale. From and to work as they do on Convert Point.
[Node("Transform/Conversion/Convert Vector", Capability.WorldRead, Description = "Carries an offset from one slot's space into another's, turned and scaled but not moved, so its length changes with the scale.",
    Keywords = "local to global, global to local, offset to world")]
public sealed class ConvertVector : ReadNode
{
    [return: Port("converted")]
    public static float3 Run(ScriptContext ctx, Slot? from, Slot? to, float3 vector)
        => ScriptSpaces.Vector(ScriptSpaces.Space(ctx, from), ScriptSpaces.Space(ctx, to), vector);
}
