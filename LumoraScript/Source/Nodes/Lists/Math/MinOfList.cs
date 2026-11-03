// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The smallest value and where it is, the first if it comes more than once. A value that is not a number is
// passed over. An empty list, or none, gives 0 at index -1.
[Node("Data/Lists/Math/Min Of List", Description = "The smallest value and where it is, the first if it comes more than once.",
    Keywords = "smallest, lowest, argmin")]
public sealed class MinOfList : ReadNode
{
    [return: Port("min")]
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
            if (float.IsNaN(v) || (index >= 0 && v >= best))
                continue;
            best = v;
            index = i;
        }
        return best;
    }

    [return: Port("min")]
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
            if (index >= 0 && v >= best)
                continue;
            best = v;
            index = i;
        }
        return best;
    }

    [return: Port("min")]
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
            if (double.IsNaN(v) || (index >= 0 && v >= best))
                continue;
            best = v;
            index = i;
        }
        return best;
    }
}
