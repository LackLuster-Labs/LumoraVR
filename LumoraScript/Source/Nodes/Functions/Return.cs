// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Ends a Function's body, handing back what is wired into it. "fn" names the Function it returns from, and
// its inputs are what that Function gives. A Function that gives nothing needs none, its body ending where
// its flow does, and a Return there only leaves early. One that gives values has to reach a Return on every
// way through its body; the check refuses a way that ends anywhere else.
[Node("Flow/Functions/Return", Description = "Ends a Function's body, handing back what is wired into it. \"fn\" names the Function it returns from, and its inputs are what that Function gives.")]
public sealed class Return : ScriptNode
{
}
