// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Shows a value on its card while the graph is open. Lowered to globals the host reads: a value that can
// be worked out at any time is refreshed a few times a second, and one that only exists inside a flow is
// taken at the end of that flow. -xlinka
[Node("Debug/Display", Description = "Shows a value on its card while the graph is open.",
    Keywords = "show value, watch value, inspect, debug value, print value, readout")]
public sealed class Display<T> : ScriptNode
{
}
