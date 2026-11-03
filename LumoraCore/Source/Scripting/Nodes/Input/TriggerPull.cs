// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// How far a VR controller's trigger is pulled, 0 to 1. On the right hand unless right is off.
[Node("Input/Controllers/Trigger", Capability.Input, Description = "How far a VR controller's trigger is pulled, 0 to 1.",
    Keywords = "controller trigger, axis, gamepad")]
public sealed class TriggerPull : ReadNode
{
    [return: Port("value")]
    public static float Run(ScriptContext ctx, bool right = true)
    {
        var input = ScriptInput.Of(ctx);
        var controller = right ? input?.RightController : input?.LeftController;
        return controller?.TriggerValue ?? 0f;
    }
}
