// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Runs the body once for each slot whose collider reaches into the sphere, with the slot and its place
// in the list, then completed. The sphere is searched once, as flow arrives, and counts as one ray.
// Sensors are found the way a trigger finds them; people's own bodies are not. -xlinka
[Node("Physics/Queries/For Each In Sphere", Capability.Physics, Description = "Runs the body once for each slot whose collider reaches into the sphere, with the slot and its place in the list, then completed.",
    Keywords = "overlap, nearby, in range, within radius, proximity, area search")]
public sealed class ForEachInSphere : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Query(EachSource.OverlapSphere, f.Input<float3>("center"), f.Input<float>("radius", 1f), f.Out("body"),
            f.Output<Slot>("slot"), f.Output<int>("index"), f.Out("completed"));
}
