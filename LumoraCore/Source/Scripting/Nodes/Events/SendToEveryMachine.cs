// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Tells every machine's copy of this author's Local scripts on target: each hears On Message. Only a
// Shared script sends it. Messages sent to one object in the same frame arrive as the last of them.
[Node("Network/Messages/Send To Every Machine", Capability.Events, Description = "Tells every machine's copy of this author's Local scripts on target: each hears On Message.")]
public sealed class SendToEveryMachine : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? target, string? message, float value = 0f)
    {
        if (ctx.OffHost)
            throw new UnauthorizedAccessException("a Local script sends to the host instead");
        if (!ScriptReads.Reached(ctx, target, "target") || !ctx.World.IsAuthority)
            return;
        ctx.CountMessage();
        ScriptMessages.ToEveryMachine(ctx.Principal, target!, ScriptMessages.Text(message), value);
    }
}
