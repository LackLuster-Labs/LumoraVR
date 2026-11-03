// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Each value of a minus the one at the same place in b, as long as the shorter list.
[Node("Data/Lists/Math/Subtract Lists", Description = "Each value of a minus the one at the same place in b, as long as the shorter list.",
    Keywords = "element-wise subtract, per item, zip")]
public sealed class SubtractLists : ReadNode
{
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? a, ScriptList<float>? b)
        => NumberLists.Pairwise(ctx, a, b, static (x, y) => x - y);
}
