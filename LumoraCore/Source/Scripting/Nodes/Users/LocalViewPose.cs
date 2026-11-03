// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where the person at this machine sees this world from, in world space: the camera a screen shows once it
// has left the head (third person, a free camera; overridden says so), else their head. Scale is the view's
// world scale, which follows the user's. DesktopFov is the screen camera's vertical field of view in degrees.
// The camera and its field of view count only while this world is the one in front of them; otherwise the
// head, and a field of view of 0. For a Local script, the way the Input nodes are.
[Node("Users/Body/Local View", Capability.Input, Description = "Where the person at this machine sees this world from, in world space: the camera a screen shows once it has left the head (third person, a free camera; overridden says so), else their head.",
    Keywords = "camera, field of view, fov, screen camera, third person camera")]
public sealed class LocalViewPose : ReadNode
{
    [return: Port("position")]
    public static float3 Run(ScriptContext ctx, out floatQ rotation, out float3 scale, out bool overridden, out float desktopFov)
    {
        var input = ScriptInput.Of(ctx);
        desktopFov = input?.DesktopCameraFovY ?? 0f;
        var root = ScriptReads.RootOf(ctx.World.LocalUser);
        var head = root?.HeadSlot;
        if (head is { IsDestroyed: true })
            head = null;
        scale = head?.GlobalScale ?? root?.Slot.GlobalScale ?? float3.One;
        overridden = input is { IsVRActive: false, DesktopCameraHasOverride: true, DesktopCameraPoseValid: true };
        if (overridden)
        {
            rotation = input!.DesktopCameraRotation;
            return input.DesktopCameraPosition;
        }
        rotation = head?.GlobalRotation ?? floatQ.Identity;
        return head?.GlobalPosition ?? default;
    }
}
