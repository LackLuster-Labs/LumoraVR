// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Runs the body once for each slot whose collider reaches into the box, with the slot and its place in
// the list, then completed. Size is edge to edge, and the rotation turns the box about its centre. The
// box is searched once, as flow arrives, and counts as one ray. -xlinka
[Node("Physics/Queries/For Each In Box", Capability.Physics, Description = "Runs the body once for each slot whose collider reaches into the box, with the slot and its place in the list, then completed.",
    Keywords = "overlap, in area, volume search, inside box")]
public sealed class ForEachInBox : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Query(EachSource.OverlapBox, f.Input<float3>("center"), f.Input<float3>("size", float3.One),
            f.Input<floatQ>("rotation", floatQ.Identity), f.Out("body"), f.Output<Slot>("slot"), f.Output<int>("index"),
            f.Out("completed"));
}
