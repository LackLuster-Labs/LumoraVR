// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Every value times scale, plus offset.
[Node("Data/Lists/Math/Scale List", Description = "Every value times scale, plus offset.",
    Keywords = "multiply list, gain, offset, map")]
public sealed class ScaleList : ReadNode
{
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? values, float scale = 1f, float offset = 0f)
    {
        var result = new ScriptList<float>(ctx.Collections);
        if (values == null)
            return result;
        foreach (float v in values.Items)
            result.Add(v * scale + offset);
        return result;
    }
}
