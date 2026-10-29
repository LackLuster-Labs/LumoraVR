// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;

namespace Lumora.Core.Scripting;

// Where an unpacked script graph stands.
//
// Packing keeps the graph's pose on its script, in the object's own space. Unpacking puts it back there while
// that spot is still within reach of whoever opens it and in front of them, so a graph comes back where it
// was left. Otherwise, and the first time, it stands in front of them: facing them, centred where they look,
// far enough out for its whole width to fit a comfortable view but never so far its cards cannot be read, its
// middle a little under the eyes and its bottom never in the floor. Everything is at the opener's size. A script unpacking a graph has nobody to
// face: its graph goes back where it was, or stands just above its object. -xlinka
public static class ScriptGraphPlacement
{
    // Metres at the opener's size.
    private const float Reach = 4f;
    private const float NearestView = 0.8f;
    private const float FarthestView = 2.2f;
    private const float ViewMargin = 0.15f;
    private const float BelowEyes = 0.1f;
    private const float FloorGap = 0.25f;
    private const float Lift = 0.35f;

    // Half the view a graph's width is fitted into, and how far off where someone looks a remembered spot may be
    // and still count as in front of them.
    private static readonly float HalfView = 32f * MathF.PI / 180f;
    private static readonly float InFront = MathF.Cos(60f * MathF.PI / 180f);

    public readonly record struct Frame(float3 Position, floatQ Rotation, float Scale)
    {
        public void Apply(Slot slot)
        {
            slot.GlobalPosition = Position;
            slot.GlobalRotation = Rotation;
            slot.GlobalScale = float3.One * Scale;
        }
    }

    public static bool HasRemembered(ScriptHost host) => host != null && host.GraphTurn.Value.LengthSquared > 0.5f;

    // The frame a graph's space takes, for a card block given in the space's own metres, and where in it the
    // cards gather: the middle of where they are, which in a graph of one big part and a few small ones is not
    // the middle of the block.
    public static Frame For(ScriptHost host, ShaderGraphBuilder.Bounds block, UserRoot? opener, float3? facing = null, float2? focus = null)
    {
        if (Remembered(host, out var origin, out var rotation))
        {
            if (opener == null)
                return new Frame(origin, rotation, 1f);
            float scale = SizeOf(opener);
            if (InReach(opener, origin + rotation * (Middle(block) * scale), rotation, scale))
                return new Frame(origin, rotation, scale);
        }
        if (opener != null)
            return InFrontOf(opener, block, focus);
        return AboveObject(host.Slot, block, facing);
    }

    // The cards' gathering point centred where the opener looks, the block facing them.
    public static Frame InFrontOf(UserRoot opener, ShaderGraphBuilder.Bounds block, float2? focus = null)
    {
        float scale = SizeOf(opener);
        var head = opener.HeadPosition;
        var look = Flat(opener.HeadRotation * float3.Backward, float3.Backward);
        float width = MathF.Max(block.Width, 0.3f) * scale;
        float distance = System.Math.Clamp(width * 0.5f / MathF.Tan(HalfView) + ViewMargin * scale, NearestView * scale, FarthestView * scale);
        var aim = focus is float2 point ? new float3(point.x, point.y, 0f) : Middle(block);
        var at = head + look * distance;
        float floor = opener.Slot.GlobalPosition.y;
        at.y = MathF.Max(head.y - BelowEyes * scale, floor + (FloorGap + aim.y - block.Bottom) * scale);
        var rotation = Facing(-look);
        return new Frame(at - rotation * (aim * scale), rotation, scale);
    }

    // A whole block of graphs at once, given its size and middle in the frame's own metres.
    public static Frame InFrontOf(UserRoot opener, float width, float height)
        => InFrontOf(opener, new ShaderGraphBuilder.Bounds(-width * 0.5f, width * 0.5f, height * 0.5f, -height * 0.5f));

    // Kept on the host as the pack's own undo record, so undoing a pack also forgets where it stood.
    public static void Remember(ScriptGraphSpace space, ScriptHost host, System.Collections.Generic.List<IUndoBatch> records,
        Localization.LocaleText step)
    {
        if (space?.Slot is not { IsDestroyed: false } slot || host?.Slot is not { IsDestroyed: false } owner)
            return;
        var at = owner.GlobalPointToLocal(slot.GlobalPosition);
        var turn = (owner.GlobalRotation.Inverse * slot.GlobalRotation).Normalized;
        if (host.GraphAt.Value != at)
        {
            var before = host.GraphAt.Value;
            host.GraphAt.Value = at;
            records.Add(new FieldEditUndoBatch(host.GraphAt, before, at, step));
        }
        if (host.GraphTurn.Value != turn)
        {
            var before = host.GraphTurn.Value;
            host.GraphTurn.Value = turn;
            records.Add(new FieldEditUndoBatch(host.GraphTurn, before, turn, step));
        }
    }

    private static bool Remembered(ScriptHost host, out float3 origin, out floatQ rotation)
    {
        origin = default;
        rotation = floatQ.Identity;
        if (!HasRemembered(host) || host.Slot is not { IsDestroyed: false } owner)
            return false;
        origin = owner.LocalPointToGlobal(host.GraphAt.Value);
        rotation = (owner.GlobalRotation * host.GraphTurn.Value.Normalized).Normalized;
        return true;
    }

    // A graph standing where its slot is, still close enough to read, in front of the opener and turned toward them.
    public static bool InReach(UserRoot opener, Slot space, ShaderGraphBuilder.Bounds block)
    {
        float scale = space.GlobalScale.x;
        return InReach(opener, space.LocalPointToGlobal(Middle(block)), space.GlobalRotation, scale > 1e-3f ? scale : 1f);
    }

    // Whether an unpacked graph stands where it was last left.
    public static bool IsBack(ScriptGraphSpace space, ScriptHost host)
        => HasRemembered(host) && space?.Slot is { } slot && ReferenceEquals(slot.Parent, host.Slot)
           && (slot.LocalPosition.Value - host.GraphAt.Value).LengthSquared < 1e-8f
           && MathF.Abs(floatQ.Dot(slot.LocalRotation.Value, host.GraphTurn.Value.Normalized)) > 0.99999f;

    // Close enough to read, in front of the opener, and turned toward them rather than away.
    private static bool InReach(UserRoot opener, float3 centre, floatQ rotation, float scale)
    {
        var head = opener.HeadPosition;
        var to = centre - head;
        if (to.Length > Reach * scale)
            return false;
        var look = Flat(opener.HeadRotation * float3.Backward, float3.Backward);
        if (float3.Dot(Flat(to, look), look) < InFront)
            return false;
        return float3.Dot(Flat(rotation * float3.Forward, -look), Flat(-to, -look)) > 0.25f;
    }

    private static Frame AboveObject(Slot item, ShaderGraphBuilder.Bounds block, float3? facing)
    {
        var at = item.GlobalPosition + new float3(0f, Lift, 0f);
        var rotation = item.GlobalRotation;
        if (facing is float3 point)
        {
            var toward = Flat(point - at, float3.Zero);
            if (toward.LengthSquared > 0.5f)
                rotation = Facing(toward);
        }
        // Bottom centre of the block on the point.
        var bottom = new float3((block.Left + block.Right) * 0.5f, block.Bottom, 0f);
        return new Frame(at - rotation * bottom, rotation, 1f);
    }

    // The yaw that turns a space's readable front, its +Z, toward a direction.
    private static floatQ Facing(float3 toward) => floatQ.AxisAngle(float3.Up, MathF.Atan2(toward.x, toward.z));

    private static float3 Middle(ShaderGraphBuilder.Bounds block)
        => new((block.Left + block.Right) * 0.5f, (block.Top + block.Bottom) * 0.5f, 0f);

    private static float3 Flat(float3 v, float3 fallback)
    {
        v.y = 0f;
        return v.LengthSquared > 1e-6f ? v.Normalized : fallback;
    }

    private static float SizeOf(UserRoot opener)
    {
        float scale = opener.GlobalScale;
        return scale > 1e-3f && float.IsFinite(scale) ? scale : 1f;
    }
}
