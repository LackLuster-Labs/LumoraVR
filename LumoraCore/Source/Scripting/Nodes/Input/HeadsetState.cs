// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// This machine's headset: whether a VR runtime is running, whether the headset is being worn and showing
// this app (the runtime takes that away when it comes off or its own menu covers the view), and its battery
// 0 to 1, or -1 where the runtime does not say.
[Node("Input/Headset/Headset", Capability.Input, Description = "Reads whether VR is running, whether the headset is active, and its battery level when available.",
    Keywords = "vr mode, hmd, headset worn")]
public sealed class HeadsetState : ReadNode
{
    [return: Port("vr")]
    public static bool Run(ScriptContext ctx, out bool worn, out float battery)
    {
        worn = false;
        battery = -1f;
        var input = ScriptInput.Of(ctx);
        if (input == null || !input.IsVRActive)
            return false;
        worn = input.IsHeadsetFocused;
        float level = input.HeadDevice?.BatteryLevel ?? -1f;
        battery = level >= 0f && float.IsFinite(level) ? System.Math.Min(level, 1f) : -1f;
        return true;
    }
}
