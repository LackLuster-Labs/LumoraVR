// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;

namespace Lumora.Core.Scripting;

// The input of whoever is at this machine, for a Local script, or a person's Author script on their own
// machine, the host's included: nothing while this world is not the one in front of them, and no keys while
// they are typing into a text field or have the dash open. A world's Author script runs on the host as the
// world, so the host's input is not its to read. -xlinka
internal static class ScriptInput
{
    public static InputInterface? Of(ScriptContext ctx) => InFront(ctx) ? Engine.Current?.InputInterface : null;

    public static bool InFront(ScriptContext ctx)
        => (ctx.IsLocal || (ctx.OnAuthorMachine && !ctx.Principal.IsWorldContent)) && ctx.World.Focus == World.WorldFocus.Focused;

    public static bool KeysOpen(InputInterface input)
        => input.Keyboard != null && input.Actions?.TextFocusHeld != true && !input.IsDashboardOpen;
}
