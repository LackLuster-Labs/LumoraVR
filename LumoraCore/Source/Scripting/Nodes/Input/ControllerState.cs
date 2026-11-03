// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every control on one of this machine's VR controllers, the right unless right is off. The trigger and grip
// read 0 to 1 with whether each counts as pressed, the grip by the same press and release points grabbing
// uses; gripForce is the squeeze a force sensor reads, 0 on a handle without one. The stick and touchpad
// read -1 to 1 each axis, pressed and touched; primary and secondary are the two face buttons and menu the
// menu button. The touched outputs say a finger rests on the trigger or a face button, on controllers that
// sense it. Everything rests at 0 and off without VR or with no controller.
[Node("Input/Controllers/Controller", Capability.Input, Description = "Every control on one of this machine's VR controllers, the right unless right is off.",
    Keywords = "gamepad, buttons, controller buttons")]
public sealed class ControllerState : ReadNode
{
    [return: Port("trigger")]
    public static float Run(ScriptContext ctx, out bool triggerPressed, out float grip, out bool gripPressed, out float gripForce,
        out float2 stick, out bool stickPressed, out bool stickTouched, out float2 touchpad, out bool touchpadPressed,
        out bool touchpadTouched, out bool primary, out bool secondary, out bool menu, out bool triggerTouched,
        out bool primaryTouched, out bool secondaryTouched, bool right = true)
    {
        var input = ScriptInput.Of(ctx);
        var controller = right ? input?.RightController : input?.LeftController;
        if (input == null || !input.IsVRActive || controller == null || !controller.IsDeviceActive)
        {
            triggerPressed = gripPressed = stickPressed = stickTouched = touchpadPressed = touchpadTouched = false;
            primary = secondary = menu = false;
            triggerTouched = primaryTouched = secondaryTouched = false;
            grip = gripForce = 0f;
            stick = touchpad = default;
            return 0f;
        }
        triggerPressed = controller.TriggerPressed;
        grip = controller.GripValue;
        gripPressed = controller.GripPressed;
        gripForce = controller.HasGripForce ? controller.GripForce : 0f;
        stick = new float2(controller.ThumbstickPosition.X, controller.ThumbstickPosition.Y);
        stickPressed = controller.ThumbstickPressed;
        stickTouched = controller.ThumbstickTouched;
        touchpad = new float2(controller.TouchpadPosition.X, controller.TouchpadPosition.Y);
        touchpadPressed = controller.TouchpadPressed;
        touchpadTouched = controller.TouchpadTouched;
        primary = controller.PrimaryButtonPressed;
        secondary = controller.SecondaryButtonPressed;
        menu = controller.MenuButtonPressed;
        triggerTouched = controller.TriggerTouched;
        primaryTouched = controller.PrimaryButtonTouched;
        secondaryTouched = controller.SecondaryButtonTouched;
        return controller.TriggerValue;
    }
}
