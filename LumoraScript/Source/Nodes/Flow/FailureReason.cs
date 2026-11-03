// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Why the last action that left by its failed exit failed: readable along that exit and anywhere after it,
// and kept until another action fails that way, so a Display on it shows the latest failure. Empty until one
// has. A failure with nothing wired to failed carries on by next and leaves this as it was.
[Node("Flow/Failure Reason", Description = "Why the last action that left by its failed exit failed: readable along that exit and anywhere after it, and kept until another action fails that way, so a Display on it shows the latest failure.",
    Keywords = "error, error message, why failed, last error, exception")]
public sealed class FailureReason : ReadNode
{
    [return: Port("reason")]
    public static string Run(ScriptContextBase ctx) => ctx.FailureReason;
}
