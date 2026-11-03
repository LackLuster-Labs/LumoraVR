// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A point on a flow wire: flow in by "in" leaves by "next", which runs one node like any exit. Gone by the
// time the graph is lowered, so it costs nothing to run. -xlinka
[Node("Graph/Wires/Flow Relay", Description = "A point on a flow wire: flow in by \"in\" leaves by \"next\", which runs one node like any exit.",
    Keywords = "reroute, knot, wire point, pass through, flow wire")]
public sealed class FlowRelay : ScriptNode
{
}
