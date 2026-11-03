// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A point on a wire: "result" is "value", for as many inputs as are wired to it. Typed by what feeds it, and
// gone by the time the graph is lowered, so it costs nothing to run. -xlinka
[Node("Graph/Wires/Value Relay", Description = "A point on a wire: \"result\" is \"value\", for as many inputs as are wired to it.",
    Keywords = "reroute, knot, wire point, pass through")]
public sealed class Relay<T> : ScriptNode
{
}
