// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Each value of a over the one at the same place in b, as long as the shorter list. Over 0 it is what
// Divide gives: infinity, or not a number for 0 over 0.
[Node("Data/Lists/Math/Divide Lists", Description = "Each value of a over the one at the same place in b, as long as the shorter list.",
    Keywords = "element-wise divide, per item, zip")]
public sealed class DivideLists : ReadNode
{
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? a, ScriptList<float>? b)
        => NumberLists.Pairwise(ctx, a, b, static (x, y) => x / y);
}
