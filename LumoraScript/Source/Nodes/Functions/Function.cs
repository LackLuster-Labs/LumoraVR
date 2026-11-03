// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A function of the graph's own. "sig" is its ports (see FunctionSignature): what it takes comes out of this
// node as outputs for its body to read, and next runs the body, which ends at a Return. With nothing after
// the bar it gives nothing back, and its body may simply end. "name" is what it is called by and exported as,
// the node's id when unset. Lowered to a function of the module, so it costs a call, however many places use
// it. -xlinka
[Node("Flow/Functions/Function", Description = "Defines a reusable function in this graph, with its own inputs and outputs. End its body with Return.")]
public sealed class Function : ScriptNode
{
}
