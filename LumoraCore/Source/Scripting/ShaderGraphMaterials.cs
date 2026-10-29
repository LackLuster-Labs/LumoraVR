// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Scripting;

// Material orbs whose shader is a shader graph. A new one is a sphere wearing a CustomShaderMaterial whose
// shader is the graph above it, holding only a Material Output, open to build on. It is all one object on
// the orb's own slot, so it is carried, copied, saved and sampled by the material tool like any material,
// and its graph goes with it. Closing the graph packs it in place, its slot still on, and the material keeps
// following it; switching the graph's slot off freezes the material on the shader it last made. -xlinka
public static class ShaderGraphMaterials
{
    public const string GraphSlotName = "Shader Graph";
    public const string OrbName = "Shader Graph Material";
    public const float OrbRadius = 0.12f;

    public static readonly LocaleText OpenStep = LocaleText.Keyed("Loom.Undo.OpenGraph", "Open Shader Graph");
    public static readonly LocaleText CloseStep = LocaleText.Keyed("Loom.Undo.CloseGraph", "Close Shader Graph");

    // Built on a slot a Create New entry or a script made for it. The whole thing is one undo step for
    // whoever made the slot.
    public static ShaderGraphSource BuildOrb(Slot slot, bool open = true)
    {
        slot.Name.Value = OrbName;
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<SphereMesh>();
        mesh.Radius.Value = OrbRadius;
        mesh.Segments.Value = 32;
        mesh.Rings.Value = 16;

        var collider = slot.AttachComponent<SphereCollider>();
        collider.Radius.Value = OrbRadius;
        collider.Type.Value = ColliderType.Trigger;

        var source = slot.AttachComponent<ShaderGraphSource>();
        var material = slot.AttachComponent<CustomShaderMaterial>();
        material.Shader.Target = source;
        source.Material.Target = material;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        var space = slot.AddSlot(GraphSlotName).AttachComponent<ScriptGraphSpace>();
        space.Kind.Value = GraphKind.Shader;
        space.ShaderSource.Target = source;
        source.Graph.Target = space;
        Place(space, slot.World?.LocalUser?.Root);

        // The output a little right of the graph's middle, so new nodes have room on its left.
        var output = ScriptGraphEditing.MakeNode(space, "MaterialOutput", null, new float3(0.02f, 0f, 0f), out var error);
        if (output == null)
            Logging.Logger.Warn($"Shader graph: a new material's output was not made: {error}");

        space.Packed.Value = !open;
        return source;
    }

    // Opens a material's graph above it, turned to whoever opens it and switched on, or packs it away where it
    // is. One undo step for whoever does it.
    public static bool SetOpen(ShaderGraphSource source, bool open)
    {
        if (source?.Graph.Target is not { IsDestroyed: false } space || space.World is not { } world)
            return false;
        var slot = space.Slot;
        var step = open ? OpenStep : CloseStep;
        var records = new List<IUndoBatch>();
        bool done = true;
        try
        {
            if (open)
            {
                var position = slot.LocalPosition.Value;
                var rotation = slot.LocalRotation.Value;
                var scale = slot.LocalScale.Value;
                Place(space, world.LocalUser?.Root);
                Note(records, slot.LocalPosition, position, step);
                Note(records, slot.LocalRotation, rotation, step);
                Note(records, slot.LocalScale, scale, step);
                if (space.Packed.Value)
                {
                    space.Packed.Value = false;
                    records.Add(new FieldEditUndoBatch(space.Packed, true, false, step));
                }
                if (!slot.ActiveSelf.Value)
                {
                    slot.ActiveSelf.Value = true;
                    records.Add(new FieldEditUndoBatch(slot.ActiveSelf, false, true, step));
                }
            }
            else if (!space.Packed.Value)
            {
                space.Packed.Value = true;
                records.Add(new FieldEditUndoBatch(space.Packed, false, true, step));
            }
        }
        catch (UnauthorizedAccessException)
        {
            done = false;
        }
        if (records.Count > 0 && ScriptGraphEditing.FindUndo(world) is { } undo && CompositeUndoBatch.Combine(step, records) is { } batch)
            undo.Record(batch);
        return done && (open ? !space.Packed.Value && slot.ActiveSelf.Value : space.Packed.Value);
    }

    private static void Note<T>(List<IUndoBatch> records, Sync<T> field, T before, LocaleText step)
    {
        if (!EqualityComparer<T>.Default.Equals(field.Value, before))
            records.Add(new FieldEditUndoBatch(field, before, field.Value, step));
    }

    // The material a graph makes, found from anything on or under its object, or from an orb carrying it.
    public static ShaderGraphSource? FindOn(Slot? slot)
    {
        for (var at = slot; at != null && !at.IsRootSlot; at = at.Parent)
        {
            if (at.GetComponent<ShaderGraphSource>() is { IsDestroyed: false } source)
                return source;
            if (at.GetComponent<MaterialOrb>()?.Material is CustomShaderMaterial { Shader.Target: ShaderGraphSource { IsDestroyed: false } worn })
                return worn;
        }
        return null;
    }

    // Above the orb, a metre across at any orb scale, turned to face whoever opens it. A graph that came along
    // with something its material paints, rather than on its own orb, has no orb to stand over: it opens a
    // little way in front of them instead.
    private static void Place(ScriptGraphSpace space, UserRoot? opener)
    {
        var parent = space.Slot.Parent;
        if (parent == null)
            return;
        float3? facing = opener?.HeadSlot != null ? opener.HeadPosition : null;
        float3 at;
        if (space.ShaderSource.Target?.Slot is { } orb && ReferenceEquals(orb, parent))
        {
            at = orb.GlobalPosition + new float3(0f, OrbRadius + 0.3f, 0f);
        }
        else if (opener?.HeadSlot != null)
        {
            // The head looks down its minus Z.
            var forward = opener.HeadRotation * float3.Backward;
            forward.y = 0f;
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
            at = opener.HeadPosition + forward * 1.2f - float3.Up * 0.2f;
        }
        else
        {
            at = parent.GlobalPosition;
        }
        space.Slot.GlobalPosition = at;
        if (facing is float3 point)
        {
            var toward = point - at;
            toward.y = 0f;
            if (toward.LengthSquared > 1e-6f)
                space.Slot.GlobalRotation = floatQ.LookRotation(toward.Normalized, float3.Up).Inverse;
        }
        space.Slot.GlobalScale = float3.One;
    }
}
