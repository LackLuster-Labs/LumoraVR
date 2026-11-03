// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Utility;
using Lumora.Core.Components.Variables;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The value of the named zones a world point stands in, picked the way a zone value driver on Highest Priority
// picks it: the highest priority wins and the zone the point is deeper in breaks a tie. Outside them all, with
// no name, or where only zones the script cannot see hold the point, it is the default.
[Node("Variables/Zones/Sample Zone", Capability.WorldRead, Description = "The value of the named zones a world point stands in, picked the way a zone value driver on Highest Priority picks it: the highest priority wins and the zone the point is deeper in breaks a tie.",
    Keywords = "area value, region, volume, zone value, inside area")]
public sealed class SampleZone<T> : ReadNode
{
    public static bool IsValidGenericType => DrivenValueTypes.IsPrimitive(typeof(T));

    [return: Port("value")]
    public static T Run(ScriptContext ctx, float3 point, string? zone, T defaultValue)
    {
        if (ValueZoneRegistry.Filter(zone) is not { } name || ValueZoneRegistry.Find(ctx.World, name) is not { Count: > 0 } zones)
            return defaultValue;

        bool found = false;
        T best = defaultValue;
        int bestPriority = 0;
        float bestWeight = 0f;
        RefID bestId = default;
        for (int i = 0; i < zones.Count; i++)
        {
            if (zones[i] is not IValueZone<T> candidate || zones[i] is not Component owner || !ScriptReads.Visible(ctx, owner)
                || !candidate.TrySample(in point, out var value, out float weight))
                continue;
            int priority = candidate.Priority;
            var id = candidate.ReferenceID;
            if (found && (priority < bestPriority || (priority == bestPriority
                    && (weight < bestWeight || (weight == bestWeight && id.CompareTo(bestId) >= 0)))))
                continue;
            found = true;
            best = value;
            bestPriority = priority;
            bestWeight = weight;
            bestId = id;
        }
        return best;
    }
}
