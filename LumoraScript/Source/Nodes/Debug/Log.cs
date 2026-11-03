// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A line in the host's log, under the script's author. Every line is written; past ten a second the
// script is warned.
[Node("Debug/Log", Description = "A line in the host's log, under the script's author.",
    Keywords = "print, console, debug log, output, trace")]
public sealed class Log : ActionNode
{
    public static void Run(ScriptContextBase ctx, string? text) => ctx.Log(text);
}
