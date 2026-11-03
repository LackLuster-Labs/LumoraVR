// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The controls of the person at this machine while they sit in the seat on a slot: the stick or keys they
// walk with, the turn, and jump and crouch held. Sitting stops walking, so while seated these steer only
// what reads them; with Release On Jump on, the seat also stands them up on jump. Seated false and zeros
// while this person is not in that seat. For a Local script, the way the Input nodes are; a rider's
// machine sends what it reads to the host with Send To Host.
[Node("Interaction/Seats/Seat Controls", Capability.Input, Description = "The controls of the person at this machine while they sit in the seat on a slot: the stick or keys they walk with, the turn, and jump and crouch held.",
    Keywords = "vehicle controls, steering, drive input, seat input")]
public sealed class SeatControls : ReadNode
{
    [return: Port("seated")]
    public static bool Run(ScriptContext ctx, Slot? seat, out float2 move, out float turn, out bool jump, out bool crouch)
    {
        move = default;
        turn = 0f;
        jump = false;
        crouch = false;
        var input = ScriptInput.Of(ctx);
        if (input == null || !ScriptReads.Visible(ctx, seat) || seat!.GetComponent<Seat>() is not { IsDestroyed: false, IsLocalUserSeated: true })
            return false;
        var controls = input.Actions?.Locomotion;
        if (controls != null)
        {
            move = controls.Move.Value;
            turn = controls.Turn.Value;
            jump = controls.Jump.Held;
            crouch = controls.Crouch.Held;
        }
        return true;
    }
}
