// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where a VR controller's thumbstick points, each axis -1 to 1. On the right hand unless right is off.
[Node("Input/Controllers/Thumbstick", Capability.Input, Description = "Where a VR controller's thumbstick points, each axis -1 to 1.",
    Keywords = "joystick, stick, axis, gamepad")]
public sealed class ThumbstickTilt : ReadNode
{
    [return: Port("value")]
    public static float2 Run(ScriptContext ctx, bool right = true)
    {
        var input = ScriptInput.Of(ctx);
        var controller = right ? input?.RightController : input?.LeftController;
        if (controller == null)
            return default;
        var stick = controller.ThumbstickPosition;
        return new float2(stick.X, stick.Y);
    }
}
