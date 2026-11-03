// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Calls a Function of another script, one on target, by "function", its name, with the ports "sig" spells
// (see FunctionSignature), which have to be that Function's own. Only scripts of the same author running on
// the same machine answer. Like any await, flow carries on through next at once: the Function runs in the
// other script's own turn, as that script, and returned runs as a call of its own with what it gave back on
// the outputs, or failed with the reason it could not. -xlinka
[Node("Flow/Functions/Call Script Function", Capability.Events, Description = "Calls a named function on another script. The configured inputs and outputs must match that function.")]
public sealed class CallScriptFunction : ScriptNode
{
}
