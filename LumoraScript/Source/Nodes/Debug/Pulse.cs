// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Counts the times flow passes through it, shown on its card, and carries on to "next". The count is a
// global the host reads. -xlinka
[Node("Flow/Pulse", Description = "Counts the times flow passes through it, shown on its card, and carries on to \"next\".",
    Keywords = "counter, count runs, hits, how many times, debug")]
public sealed class Pulse : ScriptNode
{
}
