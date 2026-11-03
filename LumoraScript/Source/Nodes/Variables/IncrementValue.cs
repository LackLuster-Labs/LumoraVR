// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Adds "by", one unless set, to a stored value and writes it back. "var" names the Stored Value node it
// changes, the way Write Value's does; whole numbers wrap past their largest value. The compiler lowers it
// into a write fed by Add, reading the stored value as the step runs.
[Node("Variables/Increment Value", Description = "Adds \"by\", one unless set, to a stored value and writes it back. \"var\" names the Stored Value node it changes, the way Write Value's does; whole numbers wrap past their largest value.",
    Keywords = "add one, plus plus, increase, count up, counter")]
public sealed class IncrementValue<T> : ScriptNode
{
    public static bool IsValidGenericType => NodeRegistry.IsSteppable(typeof(T));
}
