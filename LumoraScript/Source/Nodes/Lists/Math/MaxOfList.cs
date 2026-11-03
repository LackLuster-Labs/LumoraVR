// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The largest value and where it is, the first if it comes more than once: the loudest bin of a spectrum. A
// value that is not a number is passed over. An empty list, or none, gives 0 at index -1.
[Node("Data/Lists/Math/Max Of List", Description = "The largest value and where it is, the first if it comes more than once: the loudest bin of a spectrum.",
    Keywords = "largest, highest, peak, argmax")]
public sealed class MaxOfList : ReadNode
{
    [return: Port("max")]
    public static float Run(ScriptList<float>? values, out int index)
    {
        index = -1;
        float best = 0f;
        if (values == null)
            return best;
        var items = values.Items;
        for (int i = 0; i < items.Count; i++)
        {
            float v = items[i];
            if (float.IsNaN(v) || (index >= 0 && v <= best))
                continue;
            best = v;
            index = i;
        }
        return best;
    }

    [return: Port("max")]
    public static int Run(ScriptList<int>? values, out int index)
    {
        index = -1;
        int best = 0;
        if (values == null)
            return best;
        var items = values.Items;
        for (int i = 0; i < items.Count; i++)
        {
            int v = items[i];
            if (index >= 0 && v <= best)
                continue;
            best = v;
            index = i;
        }
        return best;
    }

    [return: Port("max")]
    public static double Run(ScriptList<double>? values, out int index)
    {
        index = -1;
        double best = 0d;
        if (values == null)
            return best;
        var items = values.Items;
        for (int i = 0; i < items.Count; i++)
        {
            double v = items[i];
            if (double.IsNaN(v) || (index >= 0 && v <= best))
                continue;
            best = v;
            index = i;
        }
        return best;
    }
}
