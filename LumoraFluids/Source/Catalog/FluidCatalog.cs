// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Fluids;

// The fluid entries in Create New, added at boot. The catalog's rule holds here too: every entry attaches the whole
// set or it does not ship. A fluid entry is a system with its substance, a renderer wearing a fluid material,
// something that puts fluid there, a thin frame showing the system's bounds and a handle to grab it by. A system
// with no source is an empty box; one with no renderer is fluid nobody sees; one with nothing to grab is stuck
// wherever it was dropped.
//
// What stands on the floor is dropped onto whatever is below it and stood upright, facing the way it was made: the
// browser makes the slot in front of the eyes, and a puddle made there would start a metre up. -xlinka
public static class FluidCatalog
{
    public const string FluidPath = "Fluid";
    public const string ToolsPath = "Tools/Fluids";
    public const string MaterialsPath = "Materials/Fluids";

    public static readonly string[] FluidEntries =
    {
        "Goo Puddle", "Water Tap", "Honey Jar", "Paint Bucket", "Goo Wall", "Slime Pit", "Fluid Plate", "Empty Fluid", "Fluid Rules",
    };

    public const string SpongeName = "Sponge";

    private const float FrameBar = 0.003f;
    private const float Wall = 0.008f;
    private const float FloorReach = 3f;

    private static readonly colorHDR FrameTint = new(1f, 1f, 1f, 0.3f);
    private static readonly colorHDR HandleTint = new(0.72f, 0.74f, 0.80f, 1f);
    private static readonly colorHDR GlassTint = new(0.85f, 0.92f, 1f, 0.25f);
    private static readonly colorHDR TrayTint = new(0.55f, 0.57f, 0.62f, 1f);

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;

        CreateNewCatalog.Add(FluidPath, "Goo Puddle", GooPuddle);
        CreateNewCatalog.Add(FluidPath, "Water Tap", WaterTap);
        CreateNewCatalog.Add(FluidPath, "Honey Jar", slot => Vessel(slot, FluidPreset.Honey, new float3(0.09f, 0.12f, 0.09f), 0.6f, 0.5f, grabbable: true));
        CreateNewCatalog.Add(FluidPath, "Paint Bucket", slot => Vessel(slot, FluidPreset.Paint, new float3(0.2f, 0.18f, 0.2f), 5f, 2f, grabbable: true));
        CreateNewCatalog.Add(FluidPath, "Goo Wall", GooWall);
        CreateNewCatalog.Add(FluidPath, "Slime Pit", SlimePit);
        CreateNewCatalog.Add(FluidPath, "Fluid Plate", FluidPlate);
        CreateNewCatalog.Add(FluidPath, "Empty Fluid", EmptyFluid, opensInspector: true);
        CreateNewCatalog.Add(FluidPath, "Fluid Rules", FluidRules);

        foreach (var kind in Enum.GetValues<FluidPreset>())
        {
            if (kind == FluidPreset.Custom)
                continue;
            var pick = kind;
            CreateNewCatalog.Add(ToolsPath, FluidTool.PresetName(pick), slot => slot.AttachComponent<FluidTool>().UsePreset(pick));
        }
        CreateNewCatalog.Add(ToolsPath, SpongeName, Sponge);

        // The material tool's New and Convert lists read this node too.
        CreateNewCatalog.Add(MaterialsPath, "Fluid", slot => MaterialOrb.Build(slot, slot.AttachComponent<FluidMaterial>()));
    }

    // RECIPES

    // Slime as a disc 35 cm across and 3 cm deep, 2.9 L, already settled. A tenth-of-a-centimetre spacing cap keeps
    // it three particles deep on every tier, which is what makes strands off a foot.
    private static void GooPuddle(Slot slot)
    {
        OnFloor(slot);
        var system = System(slot, FluidPreset.Slime, new float3(0.6f, 0.25f, 0.6f), new float3(0f, 0.12f, 0f), maxSpacing: 0.01f);
        Spawn(slot, system, FluidShape.Disc, new float3(0.35f, 0.03f, 0.35f), new float3(0f, 0.015f, 0f));
        Handle(slot, new float3(0.32f, 0.03f, 0f));
    }

    // A tap pouring into a basin with a drain, so it runs for ever and floods nothing.
    private static void WaterTap(Slot slot)
    {
        OnFloor(slot);
        var system = System(slot, FluidPreset.Water, new float3(0.6f, 0.8f, 0.5f), new float3(0f, 0.4f, 0f));
        var basin = Tray(slot, "Basin", new float3(0.5f, 0.15f, 0.4f), TrayTint);
        var drain = basin.AddSlot("Drain");
        drain.LocalPosition.Value = new float3(0f, 0.025f, 0f);
        var sink = drain.AttachComponent<FluidDrain>();
        sink.System.Target = system;
        sink.Shape.Value = FluidShape.Box;
        sink.Size.Value = new float3(0.08f, 0.04f, 0.08f);

        var post = slot.AddSlot("Tap");
        post.LocalPosition.Value = new float3(0f, 0.35f, -0.17f);
        Solid(post, new float3(0.03f, 0.7f, 0.03f), TrayTint, collider: true);
        var spout = slot.AddSlot("Spout");
        spout.LocalPosition.Value = new float3(0f, 0.62f, -0.1f);
        // An emitter pours along its slot's forward; this one faces the basin.
        spout.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, MathF.PI * 0.5f);
        var emitter = spout.AttachComponent<FluidEmitter>();
        emitter.System.Target = system;
        emitter.Rate.Value = 0.25f;
        emitter.Speed.Value = 0.6f;
        emitter.Radius.Value = 0.008f;
        emitter.StartFlow();
        Handle(slot, new float3(0.3f, 0.08f, 0f));
    }

    // A vessel with walls, filled most of the way, its system in the vessel's own frame so a swing never leaks.
    private static void Vessel(Slot slot, FluidPreset kind, float3 inside, float capacity, float litres, bool grabbable)
    {
        OnFloor(slot);
        var system = System(slot, kind, inside + new float3(0.05f, 0.1f, 0.05f), new float3(0f, inside.y * 0.5f + 0.02f, 0f));
        Tray(slot, "Walls", inside, GlassTint);
        var fill = Spawn(slot, system, FluidShape.Box, new float3(inside.x - Wall * 2f, inside.y * 0.75f, inside.z - Wall * 2f),
            new float3(0f, Wall + inside.y * 0.375f, 0f));
        fill.Litres.Value = litres;
        var container = slot.AttachComponent<FluidContainer>();
        container.Contents.Target = system;
        container.Capacity.Value = capacity;
        container.Spout.Value = new float3(0f, inside.y, inside.z * 0.5f);
        if (grabbable)
            slot.AttachComponent<Grabbable>();
    }

    // Slime pressed on a board standing upright: the cling demo.
    private static void GooWall(Slot slot)
    {
        OnFloor(slot);
        var board = slot.AddSlot("Board");
        board.LocalPosition.Value = new float3(0f, 0.4f, 0f);
        Solid(board, new float3(0.6f, 0.8f, 0.03f), TrayTint, collider: true);
        var surface = board.AttachComponent<FluidSurface>();
        surface.Stickiness.Value = 1.5f;
        var system = System(slot, FluidPreset.Slime, new float3(0.7f, 0.9f, 0.3f), new float3(0f, 0.45f, 0.1f), maxSpacing: 0.01f);
        Spawn(slot, system, FluidShape.Box, new float3(0.4f, 0.4f, 0.02f), new float3(0f, 0.45f, 0.026f));
        Handle(slot, new float3(0.34f, 0.05f, 0f));
    }

    // A wide shallow tray of slime with a sensor over it. Slime holds a stuck foot back while the world's SlowPeople
    // rule is on, which it is unless the world says otherwise.
    private static void SlimePit(Slot slot)
    {
        OnFloor(slot);
        var system = System(slot, FluidPreset.Slime, new float3(0.9f, 0.3f, 0.9f), new float3(0f, 0.15f, 0f), maxSpacing: 0.01f);
        Tray(slot, "Pit", new float3(0.8f, 0.08f, 0.8f), TrayTint);
        Spawn(slot, system, FluidShape.Disc, new float3(0.7f, 0.04f, 0.7f), new float3(0f, Wall + 0.02f, 0f));
        var sensing = slot.AddSlot("Sensor");
        sensing.LocalPosition.Value = new float3(0f, 0.1f, 0f);
        var sensor = sensing.AttachComponent<FluidSensor>();
        sensor.System.Target = system;
        sensor.Shape.Value = FluidShape.Box;
        sensor.Size.Value = new float3(0.8f, 0.2f, 0.8f);
        Handle(slot, new float3(0.46f, 0.05f, 0f));
    }

    // Everything at once on one board: a goo puddle, water poured down a short stair, a panel goo slides off, and a
    // tool to try it all with. The regression scene and the trailer scene.
    private static void FluidPlate(Slot slot)
    {
        OnFloor(slot);
        Solid(slot, new float3(1.2f, 0.02f, 0.8f), TrayTint, collider: true, at: new float3(0f, 0.01f, 0f));
        slot.AttachComponent<Grabbable>();

        var puddle = slot.AddSlot("Goo Puddle");
        puddle.LocalPosition.Value = new float3(-0.35f, 0.02f, 0.15f);
        var goo = System(puddle, FluidPreset.Slime, new float3(0.45f, 0.25f, 0.45f), new float3(0f, 0.12f, 0f), maxSpacing: 0.01f);
        Spawn(puddle, goo, FluidShape.Disc, new float3(0.35f, 0.03f, 0.35f), new float3(0f, 0.015f, 0f));

        var stair = slot.AddSlot("Stair");
        stair.LocalPosition.Value = new float3(0.3f, 0.02f, 0.05f);
        for (int step = 0; step < 3; step++)
        {
            var block = stair.AddSlot("Step " + (step + 1));
            float height = 0.06f * (3 - step);
            block.LocalPosition.Value = new float3(0f, height * 0.5f, -0.2f + step * 0.12f);
            Solid(block, new float3(0.25f, height, 0.12f), TrayTint, collider: true);
        }
        var water = System(stair, FluidPreset.Water, new float3(0.4f, 0.5f, 0.6f), new float3(0f, 0.25f, 0f));
        var spout = stair.AddSlot("Spout");
        spout.LocalPosition.Value = new float3(0f, 0.32f, -0.22f);
        spout.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, MathF.PI * 0.5f);
        var emitter = spout.AttachComponent<FluidEmitter>();
        emitter.System.Target = water;
        emitter.Rate.Value = 0.1f;
        emitter.Speed.Value = 0.4f;
        emitter.StartFlow();
        var sink = stair.AddSlot("Drain");
        sink.LocalPosition.Value = new float3(0f, 0.02f, 0.3f);
        var drain = sink.AttachComponent<FluidDrain>();
        drain.System.Target = water;
        drain.Size.Value = new float3(0.25f, 0.04f, 0.08f);

        var panel = slot.AddSlot("Non-stick Panel");
        panel.LocalPosition.Value = new float3(-0.35f, 0.12f, -0.3f);
        panel.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, -MathF.PI * 0.25f);
        Solid(panel, new float3(0.35f, 0.01f, 0.2f), new colorHDR(0.9f, 0.9f, 0.95f, 1f), collider: true);
        panel.AttachComponent<FluidSurface>().Stickiness.Value = 0.05f;

        var tool = slot.AddSlot(FluidTool.PresetName(FluidPreset.Slime));
        tool.LocalPosition.Value = new float3(0.5f, 0.06f, -0.3f);
        tool.AttachComponent<FluidTool>().UsePreset(FluidPreset.Slime);
    }

    // A sponge to hold: a soft yellow block, its collider, and the grabbable the sponge itself makes sure of.
    private static void Sponge(Slot slot)
    {
        Solid(slot, new float3(0.1f, 0.045f, 0.07f), new colorHDR(0.98f, 0.84f, 0.30f, 1f), collider: true);
        slot.AttachComponent<FluidSponge>();
        if (slot.GetComponent<Grabbable>() == null)
            slot.AttachComponent<Grabbable>();
    }

    // A system, its substance, renderer and material and nothing in it: the inspector opens on it.
    private static void EmptyFluid(Slot slot)
    {
        System(slot, FluidPreset.Water, new float3(0.5f, 0.5f, 0.5f), new float3(0f, 0.25f, 0f));
        Handle(slot, new float3(0.28f, 0f, 0f));
    }

    // The world's fluid rules live on the world root, one of them. The slot the browser made for the entry goes
    // either way.
    private static void FluidRules(Slot slot)
    {
        var world = slot.World;
        var root = world?.RootSlot;
        slot.Destroy();
        if (root == null)
            return;
        if (root.GetComponent<FluidWorldRules>() != null)
        {
            ToolNotice.Say(world, FluidLocale.RulesTitle.Resolve(), FluidLocale.RulesExist.Resolve());
            return;
        }
        root.AttachComponent<FluidWorldRules>();
    }

    // PARTS

    private static FluidSystem System(Slot slot, FluidPreset kind, float3 bounds, float3 centre, float maxSpacing = 0f)
    {
        var system = slot.AttachComponent<FluidSystem>();
        var substance = slot.AttachComponent<FluidSubstance>();
        substance.UsePreset(kind);
        system.Substances.Add(substance);
        system.Bounds.Value = bounds;
        system.BoundsCenter.Value = centre;
        if (maxSpacing > 0f)
            system.MaxSpacing.Value = maxSpacing;

        var material = slot.AttachComponent<FluidMaterial>();
        material.Look.Value = kind is FluidPreset.Water or FluidPreset.Oil ? FluidLook.Liquid : FluidLook.Goo;
        // These suspensions scatter light instead of showing the scene through their body. -xlinka
        if (kind is FluidPreset.Paint or FluidPreset.Mud)
            material.Transmission.Value = 0f;
        // The material stays white: its tint multiplies the colour each particle already carries. -xlinka
        var renderer = slot.AttachComponent<FluidRenderer>();
        renderer.System.Target = system;
        renderer.Material.Target = material;

        Frame(slot, bounds, centre);
        return system;
    }

    private static FluidSpawnVolume Spawn(Slot slot, FluidSystem system, FluidShape shape, float3 size, float3 at)
    {
        var holder = slot.AddSlot("Spawn");
        holder.LocalPosition.Value = at;
        var volume = holder.AttachComponent<FluidSpawnVolume>();
        volume.System.Target = system;
        if (system.Substances.Count > 0 && system.Substances[0] is { } first)
            volume.Substance.Target = first;
        volume.Shape.Value = shape;
        volume.Size.Value = size;
        volume.StartSettled.Value = true;
        return volume;
    }

    // Twelve thin bars along the edges of the bounds, one mesh and one material between them.
    private static void Frame(Slot slot, float3 size, float3 centre)
    {
        var frame = slot.AddSlot("Bounds");
        frame.LocalPosition.Value = centre;
        var mesh = frame.AttachComponent<BoxMesh>();
        mesh.Size.Value = float3.One;
        var material = frame.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = FrameTint;
        material.BlendMode.Value = BlendMode.Alpha;
        var half = size * 0.5f;
        for (int axis = 0; axis < 3; axis++)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                float a = (corner & 1) == 0 ? -1f : 1f;
                float b = (corner & 2) == 0 ? -1f : 1f;
                var (position, scale) = axis switch
                {
                    0 => (new float3(0f, a * half.y, b * half.z), new float3(size.x + FrameBar, FrameBar, FrameBar)),
                    1 => (new float3(a * half.x, 0f, b * half.z), new float3(FrameBar, size.y + FrameBar, FrameBar)),
                    _ => (new float3(a * half.x, b * half.y, 0f), new float3(FrameBar, FrameBar, size.z + FrameBar)),
                };
                var bar = frame.AddSlot("Edge");
                bar.LocalPosition.Value = position;
                bar.LocalScale.Value = scale;
                var renderer = bar.AttachComponent<MeshRenderer>();
                renderer.Mesh.Target = mesh;
                renderer.Material.Target = material;
                renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            }
        }
    }

    // A knob beside the fluid to grab it all by. Its collider is a trigger, so it never pushes fluid or people; the
    // grab finds the grabbable on the entry's own slot above it.
    private static void Handle(Slot slot, float3 at)
    {
        var knob = slot.AddSlot("Handle");
        knob.LocalPosition.Value = at;
        var mesh = knob.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 0.025f;
        mesh.Segments.Value = 16;
        mesh.Rings.Value = 10;
        var material = knob.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = HandleTint;
        var renderer = knob.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        var collider = knob.AttachComponent<SphereCollider>();
        collider.Radius.Value = 0.025f;
        collider.Type.Value = ColliderType.Trigger;
        if (slot.GetComponent<Grabbable>() == null)
            slot.AttachComponent<Grabbable>();
    }

    // A floor and four walls as solid boxes, open at the top.
    private static Slot Tray(Slot slot, string name, float3 inside, colorHDR tint)
    {
        var tray = slot.AddSlot(name);
        Solid(tray.AddSlot("Floor"), new float3(inside.x, Wall, inside.z), tint, collider: true, at: new float3(0f, Wall * 0.5f, 0f));
        float h = inside.y;
        Solid(tray.AddSlot("Wall"), new float3(inside.x, h, Wall), tint, collider: true, at: new float3(0f, h * 0.5f, (inside.z - Wall) * 0.5f));
        Solid(tray.AddSlot("Wall"), new float3(inside.x, h, Wall), tint, collider: true, at: new float3(0f, h * 0.5f, -(inside.z - Wall) * 0.5f));
        Solid(tray.AddSlot("Wall"), new float3(Wall, h, inside.z), tint, collider: true, at: new float3((inside.x - Wall) * 0.5f, h * 0.5f, 0f));
        Solid(tray.AddSlot("Wall"), new float3(Wall, h, inside.z), tint, collider: true, at: new float3(-(inside.x - Wall) * 0.5f, h * 0.5f, 0f));
        return tray;
    }

    private static void Solid(Slot slot, float3 size, colorHDR tint, bool collider, float3? at = null)
    {
        if (at is { } place)
            slot.LocalPosition.Value = place;
        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = size;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        if (tint.a < 1f)
        {
            var glass = slot.AttachComponent<UnlitMaterial>();
            glass.TintColor.Value = tint;
            glass.BlendMode.Value = BlendMode.Alpha;
            renderer.Material.Target = glass;
        }
        else
        {
            var material = slot.AttachComponent<PBS_Metallic>();
            material.AlbedoColor.Value = tint;
            material.Smoothness.Value = 0.4f;
            renderer.Material.Target = material;
        }
        if (collider)
            slot.AttachComponent<BoxCollider>().Size.Value = size;
    }

    // Down onto what is below, within a few metres, standing upright and turned only about the vertical.
    private static void OnFloor(Slot slot)
    {
        var forward = slot.Forward;
        float yaw = MathF.Atan2(forward.x, forward.z);
        slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, yaw);
        var world = slot.World;
        var from = slot.GlobalPosition;
        if (world?.Physics.Raycast(from, -float3.Up, FloorReach) is { } hit)
            slot.GlobalPosition = hit.Point;
    }
}
