// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A Function used as a value, with no flow: worked out wherever its outputs are read, a drive included. Only
// for a Function whose body goes straight to one Return of values that need no flow, and never one that
// comes back round to evaluating itself, since a value is worked out at once and has no If to stop it.
[Node("Flow/Functions/Evaluate Function", Description = "A Function used as a value, with no flow: worked out wherever its outputs are read, a drive included.")]
public sealed class EvaluateFunction : ScriptNode
{
}
