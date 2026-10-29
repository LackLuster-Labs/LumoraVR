// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Input.Actions;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

// A load carried on a laser handles the same whichever hand picked it up, the world hand or the userspace
// pointer: one stick slides, twists and scales it, it rides the beam upright, and a trigger press straightens
// it. Everything comes in as plain vectors and rotations, so each side feeds its own frame: the world hand its
// user root and head, the userspace pointer the focused world's user. -xlinka
public static class LaserCarry
{
    // A stick pushed all the way turns the load once a second.
    public const float TwistRate = MathF.PI * 2f;

    // A second trigger press inside this switches between upright and free.
    public const double DoublePressWindow = 0.5;

    // While a hand carries on its laser in VR its whole stick is the load's. Turning, walking, flying and the
    // hop arc all read that same stick a set below, and they read it centred while this is asked for. Asked
    // every frame: a hold lapses one pass after the last ask. The click goes too where the caller says so,
    // since it is the scale modifier and jump sits on it.
    public static void HoldStick(InteractionActions hand, bool click = false)
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.HoldControl(hand.Set, ControlRef.VR(hand.Side, InputControlCatalog.VRStick));
        if (click)
            map.HoldControl(hand.Set, ControlRef.VR(hand.Side, InputControlCatalog.VRStickPress));
    }

    // The hold for a carry, kept on past the drop until the stick is back inside the deadzone on both axes, so
    // a stick still pushed when the load is let go never turns or walks the moment it is. held is the caller's
    // latch.
    public static void HoldStickWhile(InteractionActions? hand, bool carrying, ref bool held, bool click = false)
    {
        if (hand == null)
        {
            held = false;
            return;
        }
        if (carrying)
            held = true;
        else if (held && hand.HoldAxis.Value == float2.Zero)
            held = false;
        if (held)
            HoldStick(hand, click);
    }

    // VR: stick Y slides the load along the beam, faster the further out it is, stick X twists it, and with
    // the stick clicked in Y resizes it instead. The axis action cuts each axis on its own, so a hard pull
    // never smears a twist onto the load as well. scaleStep is the holder's resize for this frame, zero for
    // none.
    public static void ApplyVrAxis(InteractionActions hand, float delta, bool canScale, ref float distance, ref float twist, out float scaleStep)
    {
        scaleStep = 0f;
        var axis = hand.HoldAxis.Value;
        float slide = axis.y;
        if (hand.HoldModifier.Held && slide != 0f && canScale)
            scaleStep = slide * delta;
        else if (slide != 0f)
            distance += slide * MathF.Max(1f, distance) * 4f * delta;
        if (axis.x != 0f)
            twist += axis.x * TwistRate * delta;
    }

    // Desktop: a wheel notch moves the load a share of its distance, or with the modifier held resizes it.
    public static void ApplyWheel(InteractionActions hand, float scrollStep, float resizeStep, bool canScale, ref float distance, out float scaleStep)
    {
        scaleStep = 0f;
        float scroll = hand.HoldScroll.Value;
        if (scroll == 0f)
            return;
        if (hand.HoldModifier.Held && canScale)
            scaleStep = scroll * resizeStep;
        else
            distance += scroll * MathF.Max(0.05f, distance * scrollStep);
    }

    public static float ScaleFactor(float scaleStep) => MathF.Max(0.05f, 1f + scaleStep);

    // The facing of a load riding the beam in VR: where the beam points from the head, flattened in the root's
    // frame, plus where the tip points, flattened the same way. Both move with the person, so turning the wrist
    // turns the load and walking round it brings it along. angle is the stick twist plus half the wrist roll.
    public static floatQ UprightRotation(HandTool.LaserRotationMode mode, float3 rootUp, float3 headPosition, float3 holderPosition,
        float3 beam, float3 rightAxis, float3 fallbackForward, float angle)
    {
        rootUp = Unit(rootUp, float3.Up);
        float3 toHolder = FlattenInRoot(rootUp, holderPosition - headPosition);
        float3 tip = FlattenInRoot(rootUp, beam);
        float3 forward = toHolder + tip;
        if (forward.LengthSquared <= 1e-8f)
            forward = tip.LengthSquared > 1e-8f ? tip : fallbackForward;
        return TurnedFacing(mode, forward, rootUp, rightAxis, angle);
    }

    // The facing turned by angle about the mode's axis: the side axis, the user's up, or the facing itself.
    public static floatQ TurnedFacing(HandTool.LaserRotationMode mode, float3 forward, float3 rootUp, float3 rightAxis, float angle)
    {
        forward = Unit(forward, float3.Backward);
        rootUp = Unit(rootUp, float3.Up);
        rightAxis = Unit(rightAxis, float3.Right);
        switch (mode)
        {
            case HandTool.LaserRotationMode.AxisX:
                forward = (floatQ.AxisAngle(rightAxis, angle) * forward).Normalized;
                break;
            case HandTool.LaserRotationMode.AxisY:
                forward = (floatQ.AxisAngle(rootUp, angle) * forward).Normalized;
                break;
            case HandTool.LaserRotationMode.AxisZ:
                rootUp = (floatQ.AxisAngle(forward, angle) * rootUp).Normalized;
                break;
        }
        return FacingRotation(forward, rootUp);
    }

    // A panel's face (+Z) turned to the eye, standing upright on the user's up: the way to the eye is taken level,
    // so neither the head's pitch nor its roll ever tips the panel back or leans it over, and one left tilted
    // comes back upright. With the eye straight above or below it, where level says nothing about which way to
    // face, it faces back along the view's heading. -xlinka
    public static bool TryFaceRotation(float3 headPosition, float3 viewForward, float3 rootUp, float3 panelPosition, out floatQ rotation)
    {
        rotation = floatQ.Identity;
        rootUp = Unit(rootUp, float3.Up);
        float3 face = FlattenInRoot(rootUp, headPosition - panelPosition);
        if (face.Length <= 0.0001f)
            face = -FlattenInRoot(rootUp, viewForward);
        if (face.Length <= 0.0001f)
            return false;
        rotation = FacingRotation(face.Normalized, rootUp);
        return true;
    }

    // Straightens a held item: its local axis nearest the given direction goes onto it. Built and applied in the
    // parent's frame (the holder for anything in a hand), so the holder's own turn never enters the delta.
    public static void Straighten(Slot slot, float3 referenceGlobal)
    {
        if (referenceGlobal.Length <= 0.0001f)
            return;
        referenceGlobal = referenceGlobal.Normalized;

        float3 referenceInTarget = slot.GlobalRotation.Inverse * referenceGlobal;
        if (referenceInTarget.Length <= 0.0001f)
            return;
        float3 localAxis = ClosestLocalAxis(referenceInTarget.Normalized);

        var parent = slot.Parent;
        float3 selectedParent = slot.LocalRotation.Value * localAxis;
        float3 referenceParent = parent != null ? parent.GlobalRotation.Inverse * referenceGlobal : referenceGlobal;
        floatQ delta = RotationFromTo(selectedParent, referenceParent);
        slot.LocalRotation.Value = delta * slot.LocalRotation.Value;
    }

    // The controller's roll about the beam in the root's frame, radians. Only ever read as a change from the
    // value at the grab, so where zero sits does not matter, only that it is stable.
    public static float TwistAngle(float3 rootUp, float3 beam, float3 tipUp)
    {
        if (beam.LengthSquared <= 1e-8f)
            return 0f;
        rootUp = Unit(rootUp, float3.Up);
        float3 side = float3.Cross(rootUp, beam);
        if (side.LengthSquared <= 1e-8f)
            return 0f;
        side = side.Normalized;
        float3 levelUp = float3.Cross(beam.Normalized, side);
        return MathF.Atan2(float3.Dot(tipUp, side), float3.Dot(tipUp, levelUp));
    }

    public static float DeltaAngle(float from, float to)
    {
        float delta = to - from;
        while (delta > MathF.PI) delta -= 2f * MathF.PI;
        while (delta < -MathF.PI) delta += 2f * MathF.PI;
        return delta;
    }

    // floatQ.LookRotation builds its basis from matrix rows, so it returns the inverse of the facing wanted.
    // Inverted, local +Z points along forward.
    public static floatQ FacingRotation(float3 forward, float3 up) => floatQ.LookRotation(forward, up).Inverse;

    // A direction with the user's up taken out, so the user's own tilt does not enter the aim.
    public static float3 FlattenInRoot(float3 rootUp, float3 direction)
    {
        rootUp = Unit(rootUp, float3.Up);
        return direction - rootUp * float3.Dot(direction, rootUp);
    }

    public static float3 ProjectOnPlane(float3 vector, float3 normal)
    {
        if (normal.Length <= 0.0001f)
            return vector;
        normal = normal.Normalized;
        return vector - normal * float3.Dot(vector, normal);
    }

    public static float3 ClosestLocalAxis(float3 direction)
    {
        float3 best = float3.Up;
        float bestDot = float3.Dot(best, direction);
        TestAxis(float3.Down, direction, ref best, ref bestDot);
        TestAxis(float3.Right, direction, ref best, ref bestDot);
        TestAxis(float3.Left, direction, ref best, ref bestDot);
        TestAxis(float3.Forward, direction, ref best, ref bestDot);
        TestAxis(float3.Backward, direction, ref best, ref bestDot);
        return best;
    }

    private static void TestAxis(float3 axis, float3 direction, ref float3 best, ref float bestDot)
    {
        float dot = float3.Dot(axis, direction);
        if (dot > bestDot)
        {
            bestDot = dot;
            best = axis;
        }
    }

    public static floatQ RotationFromTo(float3 from, float3 to)
    {
        if (from.Length <= 0.0001f || to.Length <= 0.0001f)
            return floatQ.Identity;

        from = from.Normalized;
        to = to.Normalized;
        float dot = System.Math.Clamp(float3.Dot(from, to), -1f, 1f);
        if (dot > 0.9999f)
            return floatQ.Identity;

        if (dot < -0.9999f)
        {
            float3 fallback = MathF.Abs(float3.Dot(from, float3.Up)) > 0.9f ? float3.Right : float3.Up;
            float3 axis = float3.Cross(from, fallback);
            return axis.Length <= 0.0001f ? floatQ.Identity : floatQ.AxisAngle(axis.Normalized, MathF.PI);
        }

        float3 rotationAxis = float3.Cross(from, to);
        return rotationAxis.Length <= 0.0001f
            ? floatQ.Identity
            : floatQ.AxisAngle(rotationAxis.Normalized, MathF.Acos(dot));
    }

    private static float3 Unit(float3 vector, float3 fallback)
        => vector.LengthSquared > 1e-8f ? vector.Normalized : fallback;
}
