// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// What one of this machine's VR controllers is, the right unless right is off: whether one is there with VR
// running, whether it is tracked, the interaction profile the VR runtime bound it to (a path naming its kind
// of hardware, empty while nothing is bound), its battery 0 to 1 or -1 where the runtime does not say, and
// whether it has face buttons and a grip force sensor.
[Node("Input/Controllers/Controller Info", Capability.Input, Description = "Reads a VR controller's availability, tracking, device profile, battery and supported controls.")]
public sealed class ControllerInfo : ReadNode
{
    [return: Port("present")]
    public static bool Run(ScriptContext ctx, out bool tracked, out string profile, out float battery, out bool faceButtons,
        out bool forceSensor, bool right = true)
    {
        tracked = false;
        profile = string.Empty;
        battery = -1f;
        faceButtons = false;
        forceSensor = false;
        var input = ScriptInput.Of(ctx);
        var controller = right ? input?.RightController : input?.LeftController;
        if (input == null || !input.IsVRActive || controller == null || !controller.IsDeviceActive)
            return false;
        tracked = controller.IsTracked;
        var driver = input.ActiveVRDriver;
        profile = (right ? driver?.RightInteractionProfile : driver?.LeftInteractionProfile) ?? string.Empty;
        battery = controller.BatteryLevel >= 0f && float.IsFinite(controller.BatteryLevel) ? System.Math.Min(controller.BatteryLevel, 1f) : -1f;
        faceButtons = controller.HasFaceButtons;
        forceSensor = controller.HasGripForce;
        return true;
    }
}
