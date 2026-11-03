// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Each value of a times the one at the same place in b, as long as the shorter list: a window over samples,
// a gain curve over a spectrum.
[Node("Data/Lists/Math/Multiply Lists", Description = "Each value of a times the one at the same place in b, as long as the shorter list: a window over samples, a gain curve over a spectrum.",
    Keywords = "element-wise multiply, per item, zip")]
public sealed class MultiplyLists : ReadNode
{
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? a, ScriptList<float>? b)
        => NumberLists.Pairwise(ctx, a, b, static (x, y) => x * y);
}
