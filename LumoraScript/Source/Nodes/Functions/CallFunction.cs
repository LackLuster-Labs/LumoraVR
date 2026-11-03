// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Runs a Function's body as a step of the flow that reaches it, and carries on at next with what it gave
// back. "fn" names the Function; its inputs are what that Function takes and its outputs what it gives. A
// Function may call itself this way, an If deciding when to stop. A Function that waits (a Delay, an await, or
// a call of one that waits, in its body) answers later: next then runs as a call of its own once it returns,
// the way an await's answer does, and its outputs are readable there.
[Node("Flow/Functions/Call Function", Description = "Runs a Function's body as a step of the flow that reaches it, and carries on at next with what it gave back. \"fn\" names the Function; its inputs are what that Function takes and its outputs what it gives.")]
public sealed class CallFunction : ScriptNode
{
}
