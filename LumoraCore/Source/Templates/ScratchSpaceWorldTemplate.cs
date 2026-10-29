// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Magnets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Touch;
using Lumora.Core.Components.UI;
using Lumora.Core.Components.Utility;
using Lumora.Core.Components.Variables;
using Lumora.Core.Math;
using Lumora.Core.Physics;
// Aliased, not imported: the simulation's own emitter types are named after the shapes they emit from,
// so an open import would make every emitter name in this file ambiguous with its datamodel component.
using SimEmitters = Lumora.Simulation.Particles.Emitters;
using SimModules = Lumora.Simulation.Particles.Modules;
// Strand alignment and UV mode live a namespace up from the modules that carry them.
using SimStrands = Lumora.Simulation.Particles;

namespace Lumora.Core.Templates;

// The engine's own test bench. Every material, every mesh generator and every interaction system
// gets one visible instance here, so "did that still work after the refactor" is a walk around a
// room instead of a hunt through a save file.
//
// Layout rule: the user spawns at the origin facing -Z, and the first three metres in every direction
// stay empty so you can turn around before you walk into anything. Each area gets a coloured floor
// plate, a name on the plate edge facing spawn, and at least four metres of bare floor between it and
// its neighbours. Inside an area nothing sits closer than a metre and a half to the thing next to it,
// with one deliberate exception: the shader orb grid is a CONTACT SHEET, and spreading thirty balls to
// a metre and a half apart turns "did every material still compile" into a hike. -xlinka
//
// Compass from spawn, near ring first:
//   ahead (-Z)          materials orb grid
//   front-left          rendering
//   front-right         meshes
//   right               physics and soft bodies
//   behind (+Z)         panels
//   left                UI gallery (every dashboard screen and import dialog)
//   back-left           interaction
//   back-right          variables and utility
//   far ahead (-Z)      particles, one plate per emitter type
//   far behind (+Z)     lights and text
internal sealed class ScratchSpaceWorldTemplate : WorldTemplateDefinition
{
    private const string FontPath = "res://Assets/Fonts/FiraCode/FiraCode-SemiBold.ttf";

    // Every area's root, in one place, because a position typed inline in eight different builders is
    // how the last layout ended up overlapping itself.
    private static readonly float3 MaterialsOrigin = new(0f, 1.2f, -8f);
    private static readonly float3 RenderingOrigin = new(-10.5f, 0f, -9f);
    private static readonly float3 MeshesOrigin = new(12.5f, 0f, -11f);
    private static readonly float3 PhysicsOrigin = new(10.5f, 0f, -3f);
    // Interaction sits further out than it used to. Its plate is the biggest in the world at twelve by
    // thirteen, and the UI gallery had nowhere else to go on the left that kept four metres of bare floor
    // between the two. -xlinka
    private static readonly float3 InteractionOrigin = new(-16.5f, 0f, 17f);
    private static readonly float3 VariablesOrigin = new(14f, 0f, 10.5f);
    private static readonly float3 PanelsOrigin = new(0f, 0f, 11f);
    private static readonly float3 ParticlesOrigin = new(0f, 0f, -22f);
    private static readonly float3 UiGalleryOrigin = new(-16.5f, 0f, 1.8f);
    private static readonly float3 LightsOrigin = new(0f, 0f, 22f);

    public ScratchSpaceWorldTemplate() : base("Scratch") { }

    // Spawn, light and ground first, then an area a frame, so opening the world never stops one frame for all
    // of it. -xlinka
    protected override IEnumerable<Action> Steps(World world)
    {
        yield return () =>
        {
            CreateSpawn(world);
            CreateLighting(world);
            CreateGround(world);
        };
        // The panels area needs a real material to inspect, and the first orb's is as real as any.
        PBS_Metallic showcaseMaterial = null!;
        yield return () => showcaseMaterial = CreateShaderOrbs(world);
        yield return () => CreatePhysicsShowcase(world);
        yield return () => CreateRenderingShowcase(world);
        yield return () => CreateMeshGallery(world);
        yield return () => CreateInteractionShowcase(world);
        yield return () => CreateVariableShowcase(world);
        foreach (var step in PanelShowcaseSteps(world, () => showcaseMaterial))
            yield return step;
        yield return () => CreateParticleShowcase(world);
        yield return () => CreateUiGallery(world);
        yield return () => CreateLightsAndText(world);

        yield return () =>
        {
            // Dead last, and it has to be: half the areas rotate or re-pose a parent AFTER its labels are
            // built, and a label inherits that. -xlinka
            AlignSigns(world);

            // Every system in the world at once, rather than typed into two builders that both spawn them.
            foreach (var system in world.RootSlot.GetComponentsInChildren<ParticleSystem>())
                system.MaxViewDistance.Value = ParticleViewDistance;

            BoundAreaProps(world);
        };
    }

    // Props were the one thing the view-distance pass never covered. Particles, signs and panels all got a
    // band; the meshes they stand on did not, so most of the renderers in the world drew from anywhere -
    // the orb contact sheet, the gallery stands, the emitter plinths, every showcase prop.
    //
    // One band per area root rather than a number on each renderer: LodDistanceCull pushes it to everything
    // underneath and re-pushes on structure changes, so a prop built later (or spawned by a tool) inherits
    // it instead of being missed. Bands COMBINE by tightest, so a sign already held to SignViewDistance
    // inside a bounded area comes down to the area's band and vanishes with the thing it labels rather
    // than hanging in the air over an empty plate.
    //
    // The exclusions are the things you navigate by. The floor, the coloured area plates and their names
    // are how you find an area from the far side of the world, and the gizmos and the spawn belong to
    // whoever is standing there. -xlinka
    private const float PropViewFloor = 60f;
    private const float PropFadeMargin = 4f;

    // SIZE-AWARE, not a blanket band: scenery must never vanish off the horizon. Only small clutter
    // earns an automatic leash (100x its own size, floored at 60m), anything structural draws forever,
    // and the flat frame savings come from shadow range and the per-kind sign/panel/particle bounds
    // instead. -xlinka
    private static void BoundAreaProps(World world)
    {
        foreach (var area in world.RootSlot.Children)
        {
            switch (area.Name.Value)
            {
                case "SpawnArea":
                case "Ground":
                case "Area Plates":
                case "DirectionalLight":
                case "Gizmos":
                    continue;
            }

            if (area.GetComponent<LodDistanceCull>() != null)
                continue;

            var cull = area.AttachComponent<LodDistanceCull>();
            cull.SizeBased.Value = true;
            cull.MaxDistance.Value = PropViewFloor;
            cull.FadeMargin.Value = PropFadeMargin;
            // The areas are laid out in world units and never scaled; reading a scale off the root would
            // only let a stray resize move the band.
            cull.IgnoreScale.Value = true;
        }
    }

    // How far each kind of thing is worth drawing, in metres from the viewer.
    //
    // Typed here rather than into forty builders, and split by kind because the kinds stop being worth it
    // at different distances. A sign is unreadable long before it stops being drawn. A panel's fifteen
    // pixel text is gone at half a sign's range and it costs a hundred and fifty odd chunk meshes to say
    // so. A particle system is the only one that keeps burning CPU while nobody is looking at it, so it
    // gets the shortest leash. Every number is comfortably past the far edge of the area it applies to,
    // so nothing here changes what you see while you are standing in front of it. -xlinka
    private const float SignViewDistance = 45f;
    private const float PanelViewDistance = 40f;
    private const float ParticleViewDistance = 35f;
    private const float SunShadowDistance = 40f;
    private const float LampFadeBegin = 10f;
    private const float LampFadeLength = 4f;

    // Cull every canvas standing on this host past PanelViewDistance.
    //
    // Called with the stand's host slot, not with a canvas, because at build time there usually is not a
    // canvas yet: a dialog builds its panel in OnStart, which is the first update, and a spawned tool
    // panel builds on the same schedule. Sweeping the world at the end of the build would set the
    // distance on the eleven dashboards and quietly miss every import dialog in the gallery, which are
    // the heaviest panels in the place. So it waits and looks again until one turns up.
    //
    // The canvas text samples over in Materials and Lights do not get this. They are there to be read
    // side by side with the world-space text renderer standing next to them, so they have to keep
    // drawing for exactly as long as the props they are being compared against. -xlinka
    private static void CullPanelBeyond(Slot host) => CullPanelBeyond(host, PanelWaitAttempts);

    private static void CullPanelBeyond(Slot host, int attemptsLeft)
    {
        var world = host.World;
        if (world == null)
            return;

        // RunInSeconds, not RunInUpdates. RunInUpdates does not actually wait: the synchronous queue is
        // drained in a while loop, so its countdown action re-queues itself and gets picked straight back
        // up inside the same drain, and the whole count burns off in the update that scheduled it. The
        // delayed-action list is a real timer. -xlinka
        world.RunInSeconds(PanelWaitStep, () =>
        {
            if (host.IsDestroyed)
                return;

            // Look again rather than guess at a number of frames. A dashboard has its canvas on the first
            // look and an import dialog takes several more, and a fixed wait tuned to today's startup
            // order is the kind of thing that silently stops covering the dialogs the moment something
            // upstream defers by one more frame. Bounded so a stand that never grows a canvas stops
            // asking instead of walking its subtree for the rest of the session. -xlinka
            bool found = false;
            foreach (var canvas in host.GetComponentsInChildren<Canvas>())
            {
                canvas.MaxViewDistance.Value = PanelViewDistance;
                found = true;
            }

            if (!found && attemptsLeft > 0)
                CullPanelBeyond(host, attemptsLeft - 1);
        });
    }

    private const float PanelWaitStep = 1f / 30f;
    private const int PanelWaitAttempts = 60;

    private static void CreateSpawn(World world)
    {
        var spawnSlot = world.RootSlot.AddSlot("SpawnArea");
        spawnSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();
    }

    private static void CreateLighting(World world)
    {
        // 40 degrees up and a little behind spawn's left shoulder, so what you face at spawn is lit. This was
        // the grid's old rotation copied over: 10 degrees up dead ahead, backlighting everything in view, and
        // shining from a different side of the sky than the sun the sky drew. The sky now takes its sun from
        // this rotation. -xlinka
        var sunRotation = floatQ.Euler(-0.611f, -0.698f, 0f);
        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = sunRotation;
        var dirLight = lightSlot.AttachComponent<Light>();
        dirLight.Type.Value = LightType.Directional;
        dirLight.LightColor.Value = new color(1.00f, 0.91f, 0.82f, 1f);
        // Tuned under the stray bootstrap sun (see LocalHomeWorldTemplate); carries its share now.
        dirLight.Intensity.Value = 1.5f;
        dirLight.Shadows.Value = ShadowType.Soft;
        // The world is a hundred metres of ground plate with eleven areas parked around the origin, and
        // the sun's default reach is the whole of it, four cascades deep. Nothing past forty metres is
        // close enough for its shadow to read as anything, so past forty metres it does not get one, and
        // two cascades over forty metres is a tighter near split than four over a hundred was. -xlinka
        dirLight.ShadowMaxDistance.Value = SunShadowDistance;
        dirLight.ShadowSplits.Value = ShadowSplitMode.Two;

        var skySlot = world.RootSlot.AddSlot("GradientSkybox");
        var skybox = skySlot.AttachComponent<GradientSkybox>();
        skybox.TopColor.Value = new color(0.18f, 0.22f, 0.34f, 1f);
        skybox.HorizonColor.Value = new color(0.44f, 0.48f, 0.56f, 1f);
        skybox.BottomColor.Value = new color(0.18f, 0.16f, 0.14f, 1f);
        skybox.SunColor.Value = new color(1.00f, 0.88f, 0.68f, 1f);
        skybox.SunDirection.Value = sunRotation * float3.Forward;
        skybox.SunSize.Value = 0.018f;
        skybox.SunIntensity.Value = 0.75f;
        skybox.SunGlowPower.Value = 24f;
        skybox.AmbientEnergy.Value = 0.85f;
    }

    // The floor's WALKABLE SURFACE is y=0, and both halves of it have to agree on that.
    //
    // They used to disagree by half the slab. A BoxMesh is centred on its slot, so a 0.1 m slab hung on
    // a slot at y=0 draws its top at +0.05; the collider was then shoved down by that same 0.05 to put
    // the physics top at 0. Everything you dropped came to rest five centimetres inside the visible
    // floor, which is what the beads sinking to their waterline actually was. Dropping the whole slot
    // by half the thickness and taking the collider offset back out puts both tops on y=0 with no
    // fudge factor in either, and keeps SquishyBody.GroundY = 0 an honest statement. -xlinka
    private static void CreateGround(World world)
    {
        const float thickness = 0.1f;

        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, -thickness * 0.5f, 0f);
        groundSlot.Tag.Value = "floor";

        var groundMesh = groundSlot.AttachComponent<BoxMesh>();
        groundMesh.Size.Value = new float3(100f, thickness, 100f);
        groundMesh.UVScale.Value = new float3(100f, 1f, 100f);

        var groundMaterial = groundSlot.AttachComponent<GridSpaceGroundMaterial>();
        groundMaterial.HorizonColor.Value = new colorHDR(0.39f, 0.42f, 0.48f, 1f); // this sky just under its horizon line

        var groundRenderer = groundSlot.AttachComponent<MeshRenderer>();
        groundRenderer.Mesh.Target = groundMesh;
        groundRenderer.Material.Target = groundMaterial;
        groundRenderer.ShadowCastMode.Value = ShadowCastMode.Off;

        var groundCollider = groundSlot.AttachComponent<BoxCollider>();
        groundCollider.CharacterCollider.Value = true;
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Size.Value = groundMesh.Size.Value;
    }

    // SHARED HELPERS

    // One font for the whole world. Text with no font on it renders NOTHING at all, so every sign in
    // here has to be able to reach a provider, and forty providers pulling the same file is forty
    // copies of it sitting in memory for no reason.
    private static FontProvider SharedFont(World world)
    {
        var slot = world.RootSlot.FindChildOrAdd("Shared Assets");
        var existing = slot.GetComponent<FontProvider>();
        if (existing != null)
            return existing;

        var font = slot.AttachComponent<FontProvider>();
        font.URL.Value = new Uri(FontPath);
        font.FallbackURLs.Add(new Uri(FontPath));
        return font;
    }

    // Every label in the world comes out of here, because half of them used to read backwards.
    //
    // A TextRenderer's readable face points down its own +Z, so the yaw that turns one toward spawn is
    // the angle of the flattened spawn-ward vector. It is atan2(x, z) and not atan2(z, x) because yaw
    // here is measured off +Z, not off +X. floatQ.LookRotation is not an option at any price: it hands
    // back the INVERSE rotation, and a sign built on it goes edge-on the moment it is off-axis.
    //
    // Signs behind spawn were the visible half of this. They were dropped in with identity rotation,
    // which points +Z away from the origin, so you walked round the back of the world and read every
    // one of them mirrored. -xlinka
    private static TextRenderer Sign(Slot parent, string text, float3 localPosition, float size)
    {
        var slot = parent.AddSlot(SignName);
        slot.LocalPosition.Value = localPosition;

        var label = slot.AttachComponent<TextRenderer>();
        label.Text.Value = text;
        label.Size.Value = size;
        label.Color.Value = new color(0.93f, 0.96f, 1f, 1f);
        label.Font.Target = SharedFont(parent.World);
        label.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0.9f);
        label.OutlineThickness.Value = 1.05f;
        label.MaxViewDistance.Value = SignViewDistance;

        PlaceSign(slot);
        return label;
    }

    // The name is the contract: every slot called this is a spawn-facing label and gets re-placed by the
    // pass at the end of the build. The two signs that deliberately point elsewhere (FaceUser, FaceTarget)
    // carry their own names and their own drivers, so the pass never touches them. -xlinka
    private const string SignName = "Sign";

    private static void FaceSpawn(Slot slot)
    {
        var toSpawn = -slot.GlobalPosition;
        toSpawn.y = 0f;
        if (toSpawn.LengthSquared <= 1e-6f)
            return;
        slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toSpawn.x, toSpawn.z));
    }

    // Every label in the world, re-placed once everything else exists.
    //
    // Two things go wrong if a sign is only placed when it is created. A label dropped at a post's own XZ
    // is INSIDE the post - which is most of the "why is that name buried in a pole" from the walkthrough -
    // and a label whose parent gets turned or re-posed afterwards inherits that turn, so it hangs at an
    // angle or edge-on. Doing the placement here, after the last builder has run, means the pass sees the
    // final parent transforms and every solid a label might be standing in. It is idempotent: the offset
    // is computed from what the sign is actually inside RIGHT NOW, so a sign already clear of everything
    // is left exactly where it is. -xlinka
    private static void AlignSigns(World world)
    {
        var signs = new System.Collections.Generic.List<Slot>();
        CollectSigns(world.RootSlot, signs);
        foreach (var sign in signs)
            PlaceSign(sign);
    }

    private static void CollectSigns(Slot slot, System.Collections.Generic.List<Slot> into)
    {
        if (slot.Name.Value == SignName)
            into.Add(slot);
        foreach (var child in slot.Children)
            CollectSigns(child, into);
        foreach (var child in slot.LocalChildren)
            CollectSigns(child, into);
    }

    // Step the label out of whatever it is standing in, then square it up to spawn.
    private static void PlaceSign(Slot sign)
    {
        var parent = sign.Parent;
        if (parent != null)
        {
            var local = sign.LocalPosition.Value;
            float clearance = 0f;
            float postTop = float.MinValue;

            foreach (var sibling in parent.Children)
            {
                if (ReferenceEquals(sibling, sign))
                    continue;
                if (!TryGetFootprint(sibling, out var center, out var half, out float bottom, out float top, out bool isPost))
                    continue;
                // Only labels the solid actually swallows get moved, which means inside it on all three
                // axes. A label hanging UNDER a readout cube or standing in front of a console is where
                // its author put it and stays there.
                if (MathF.Abs(local.x - center.x) > half.x || MathF.Abs(local.z - center.y) > half.y)
                    continue;
                if (local.y > top || local.y < bottom)
                    continue;

                clearance = MathF.Max(clearance, MathF.Max(half.x, half.y) + SignClearance);
                if (isPost)
                    postTop = MathF.Max(postTop, top);
            }

            if (clearance > 0f)
            {
                var toSpawn = parent.GlobalDirectionToLocal(-sign.GlobalPosition);
                toSpawn.y = 0f;
                if (toSpawn.LengthSquared > 1e-6f)
                    local += toSpawn.Normalized * clearance;
                // A leg is the one thing worth clearing vertically as well: the label belongs on top of
                // the post, under whatever the post is holding up, not halfway down the pole.
                if (postTop > local.y)
                    local.y = postTop + 0.03f;
                sign.LocalPosition.Value = local;
            }
        }

        FaceSpawn(sign);

        // Aiming at spawn only works while you are STANDING on spawn. Walk past a label and you are
        // behind it, and a text quad seen from behind is not blank, it is the same text backwards -
        // which is the whole world reading in mirror writing as soon as you cross the middle. The yaw
        // above stays as the built-in/headless answer; this turns it to whoever is actually looking.
        // Yaw-only and it only writes when the angle really changed, so a standing viewer costs
        // nothing. -xlinka
        if (sign.GetComponent<FaceLocalUser>() == null)
            sign.AttachComponent<FaceLocalUser>();
    }

    // Twelve centimetres of daylight past the edge of whatever the label was inside.
    private const float SignClearance = 0.12f;

    // The horizontal footprint of a sibling a label could be buried in, in the parent's local space.
    // Cylinders and spheres answer with their radius, boxes with their half extents; anything without one
    // of those meshes is not something a label can be inside. -xlinka
    private static bool TryGetFootprint(Slot slot, out float2 center, out float2 half, out float bottom, out float top, out bool isPost)
    {
        var position = slot.LocalPosition.Value;
        center = new float2(position.x, position.z);
        half = float2.Zero;
        bottom = 0f;
        top = 0f;
        isPost = false;

        var cylinder = slot.GetComponent<CylinderMesh>();
        if (cylinder != null)
        {
            half = new float2(cylinder.Radius.Value, cylinder.Radius.Value);
            bottom = position.y - cylinder.Height.Value * 0.5f;
            top = position.y + cylinder.Height.Value * 0.5f;
            isPost = slot.Name.Value == "Post";
            return true;
        }

        var box = slot.GetComponent<BoxMesh>();
        if (box != null)
        {
            var size = box.Size.Value;
            half = new float2(size.x * 0.5f, size.z * 0.5f);
            bottom = position.y - size.y * 0.5f;
            top = position.y + size.y * 0.5f;
            return true;
        }

        var sphere = slot.GetComponent<SphereMesh>();
        if (sphere != null)
        {
            half = new float2(sphere.Radius.Value, sphere.Radius.Value);
            bottom = position.y - sphere.Radius.Value;
            top = position.y + sphere.Radius.Value;
            return true;
        }

        return false;
    }

    // A flat coloured slab under each area, and the area's name standing on the edge you approach from.
    // The complaint that started this pass was that the place reads as one undifferentiated field of
    // props; a plate is the cheapest thing that says "this lot belongs together" from across the room.
    // The slab's underside sits exactly on y=0 so it never fights the ground for depth. -xlinka
    private static Slot AreaPlate(World world, string label, float2 center, float2 size, colorHDR tint)
    {
        const float thickness = 0.02f;

        var plates = world.RootSlot.FindChildOrAdd("Area Plates");
        var slot = plates.AddSlot(label + " Plate");
        slot.LocalPosition.Value = new float3(center.x, thickness * 0.5f, center.y);

        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = new float3(size.x, thickness, size.y);
        mesh.UVScale.Value = new float3(size.x, 1f, size.y);

        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = tint;
        material.Metallic.Value = 0f;
        material.Smoothness.Value = 0.18f;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        // Walk out from the plate centre toward spawn until the ray leaves the slab, then stand the
        // name a little past that edge. Both plates and areas are axis-aligned, so the exit is just the
        // nearer of the two axis crossings.
        float length = MathF.Sqrt(center.x * center.x + center.y * center.y);
        if (length > 1e-4f)
        {
            var toSpawn = new float2(-center.x / length, -center.y / length);
            float exit = float.MaxValue;
            if (MathF.Abs(toSpawn.x) > 1e-4f)
                exit = MathF.Min(exit, size.x * 0.5f / MathF.Abs(toSpawn.x));
            if (MathF.Abs(toSpawn.y) > 1e-4f)
                exit = MathF.Min(exit, size.y * 0.5f / MathF.Abs(toSpawn.y));
            if (exit < float.MaxValue)
            {
                float reach = exit + 0.45f;
                Sign(slot, label, new float3(toSpawn.x * reach, 0.24f, toSpawn.y * reach), 0.3f);
            }
        }

        return slot;
    }

    // A leg from the floor up to whatever is standing on it. Most of the "why is that hovering" props
    // were always meant to be mounted on something and simply never got the mount.
    private static void Post(Slot parent, float2 localXZ, float topY, float radius = 0.05f)
    {
        if (topY <= 0.05f)
            return;

        var slot = parent.AddSlot("Post");
        slot.LocalPosition.Value = new float3(localXZ.x, topY * 0.5f, localXZ.y);

        var mesh = slot.AttachComponent<CylinderMesh>();
        mesh.Radius.Value = radius;
        mesh.Height.Value = topY;
        mesh.Segments.Value = 14;

        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.22f, 0.23f, 0.28f, 1f);
        material.Metallic.Value = 0.5f;
        material.Smoothness.Value = 0.35f;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
    }

    // One preview per material component and per shader VARIANT, because a variant is a different
    // compiled shader: the toon outline, the wireframe overlay, the transparent vertex-colour pass
    // and the additive unlit pass each get their own body or nothing proves they still compile.
    // -xlinka
    private static PBS_Metallic CreateShaderOrbs(World world)
    {
        var root = world.RootSlot.AddSlot("ShaderMaterialOrbs");
        root.LocalPosition.Value = MaterialsOrigin;

        // Six columns of 0.8 and five rows of 0.8, so the sheet covers 4 by 3.2 and the plate takes a
        // little more than that on every side.
        AreaPlate(world, "Materials", new float2(MaterialsOrigin.x, MaterialsOrigin.z - 1.6f),
            new float2(5.6f, 4.6f), new colorHDR(0.20f, 0.17f, 0.26f, 1f));
        Sign(root, "Materials\nevery shader, one ball each", new float3(0f, 1.9f, 1.4f), 0.24f);

        var assets = root.AddSlot("Orb Assets");

        var checker = assets.AttachComponent<CheckerTextureProvider>();
        checker.Width.Value = 128;
        checker.Height.Value = 128;
        checker.CellSize.Value = 16;
        checker.ColorA.Value = new color(0.10f, 0.12f, 0.16f, 1f);
        checker.ColorB.Value = new color(0.86f, 0.90f, 0.98f, 1f);

        // A matcap is normally a photographed sphere. A vertical ramp is not that, but it is a real
        // texture in the matcap slot, so the sampling path and the view-space normal lookup are both
        // exercised and the ball reads as top-lit. -xlinka
        var matcapRamp = assets.AttachComponent<GradientStripTexture>();
        matcapRamp.From.Value = new color(0.06f, 0.08f, 0.16f, 1f);
        matcapRamp.To.Value = new color(1.00f, 0.94f, 0.78f, 1f);
        matcapRamp.Size.Value = 128;
        matcapRamp.Orientation.Value = GradientStripTexture.StripOrientation.Vertical;

        // A shadow ramp is read left to right off the wrapped N.L, so this one runs horizontally: dark
        // cool end at U 0, warm lit end at U 1. It is the ramp path on the toon stand, not a decoration.
        var toonRamp = assets.AttachComponent<GradientStripTexture>();
        toonRamp.From.Value = new color(0.20f, 0.16f, 0.34f, 1f);
        toonRamp.To.Value = new color(1.00f, 0.92f, 0.82f, 1f);
        toonRamp.Size.Value = 64;
        toonRamp.Exp.Value = 2.2f;
        toonRamp.Orientation.Value = GradientStripTexture.StripOrientation.Horizontal;

        var font = assets.AttachComponent<FontProvider>();
        font.URL.Value = new Uri(FontPath);
        font.FallbackURLs.Add(new Uri(FontPath));

        int index = 0;
        var first = CreateOrb<PBS_Metallic>(root, "PBS_Metallic", index++, material =>
        {
            material.AlbedoColor.Value = new colorHDR(0.86f, 0.32f, 0.22f, 1f);
            material.Metallic.Value = 1.0f;
            material.Smoothness.Value = 0.82f;
        });

        CreateOrb<PBS_Specular>(root, "PBS_Specular", index++, material =>
        {
            material.AlbedoColor.Value = new colorHDR(0.22f, 0.44f, 0.92f, 1f);
            material.SpecularColor.Value = new colorHDR(1.0f, 0.92f, 0.72f, 0.9f);
        });

        CreateOrb<UnlitMaterial>(root, "Unlit", index++, material =>
        {
            material.TintColor.Value = new colorHDR(0.42f, 1.0f, 0.52f, 1f);
            material.UseVertexColor.Value = false;
            material.Culling.Value = Culling.None;
        });

        CreateOrb<UIUnlitMaterial>(root, "UI_Unlit", index++, material =>
        {
            material.TintColor.Value = new colorHDR(0.34f, 0.92f, 1.0f, 0.86f);
            material.UseVertexColor.Value = false;
            material.AlphaClip.Value = false;
            material.BlendMode.Value = BlendMode.Alpha;
            material.Culling.Value = Culling.None;
        });

        CreateOrb<GridSpaceGroundMaterial>(root, "GridSpaceGround", index++, material =>
        {
            material.BaseNearColor.Value = new colorHDR(0.045f, 0.040f, 0.035f, 1f);
            material.MinorLineColor.Value = new colorHDR(0.34f, 0.58f, 0.98f, 1f);
            material.MinorLineWidth.Value = 1.3f;
        });

        CreateOrb<MetaballMaterial>(root, "Metaball", index++, material =>
        {
            material.BlobCount.Value = 36;
            material.VolumeExtents.Value = new float2(1.4f, 1.4f);
            material.VolumeHeight.Value = 1.4f;
            material.VolumeOffset.Value = new float3(0f, -0.3f, 0f);
            material.Culling.Value = Culling.None;
        });

        CreateBubblePreview(root, "LocalHomeRising", index++);

        CreateOrb<FresnelMaterial>(root, "Fresnel", index++, material =>
        {
            material.NearColor.Value = new colorHDR(0.04f, 0.04f, 0.06f, 1f);
            material.FarColor.Value = new colorHDR(0.4f, 0.95f, 1.0f, 1f);
        });

        CreateCustomOrb(root, "UnlitTransparent", index++, "res://Shaders/UnlitTransparent.gdshader", BlendMode.Alpha, Culling.None, material =>
        {
            AddColorParam(material, "albedo_color", new colorHDR(0.34f, 0.92f, 1.0f, 0.45f));
            AddBoolParam(material, "use_vertex_color", false);
        });
        CreateCustomOrb(root, "EngineParticle", index++, "res://Shaders/EngineParticle.gdshader", BlendMode.Additive, Culling.Back, material =>
        {
            AddFloatParam(material, "emission_strength", 1.8f);
        });
        CreateCustomOrb(root, "DebugGrid", index++, "res://Assets/Shaders/DebugGrid.gdshader", BlendMode.Opaque, Culling.Back);

        // Additive is a compile-time render mode, so the unlit material swaps to Unlit_Additive when
        // its blend mode is set. Only a second orb proves that swap still lands. -xlinka
        CreateOrb<UnlitMaterial>(root, "Unlit_Additive", index++, material =>
        {
            material.TintColor.Value = new colorHDR(1.0f, 0.42f, 0.18f, 1f);
            material.UseVertexColor.Value = false;
            material.BlendMode.Value = BlendMode.Additive;
            material.Culling.Value = Culling.None;
        });

        CreateOrb<BlurMaterial>(root, "Blur", index++, material =>
        {
            material.Mode.Value = BlurMode.Gaussian;
            material.Radius.Value = 8f;
            material.Opacity.Value = 0.92f;
            material.TintColor.Value = new colorHDR(0.05f, 0.06f, 0.10f, 0.35f);
            material.Culling.Value = Culling.None;
        });

        CreateQuadPreview<ColorGradientMaterial>(root, "UI_ColorGradient", index++, new float2(0.42f, 0.42f), material =>
        {
            material.Mode.Value = ColorGradientMode.HueStrip;
            material.Vertical.Value = false;
            material.BlendMode.Value = BlendMode.Alpha;
            material.Culling.Value = Culling.None;
        });

        CreateQuadPreview<DualColorMaterial>(root, "UI_DualColor", index++, new float2(0.42f, 0.42f), material =>
        {
            material.Texture.Target = checker;
            material.TintColor.Value = new colorHDR(0.08f, 0.14f, 0.42f, 1f);
            material.SecondColor.Value = new colorHDR(1.0f, 0.68f, 0.16f, 1f);
            material.UseVertexColor.Value = false;
            material.BlendMode.Value = BlendMode.Alpha;
            material.Culling.Value = Culling.None;
        });

        CreateOrb<FlatToonMaterial>(root, "FlatToon", index++, material =>
        {
            material.AlbedoColor.Value = new colorHDR(0.95f, 0.42f, 0.58f, 1f);
            material.ShadeColor.Value = new colorHDR(0.34f, 0.18f, 0.36f, 1f);
            material.Steps.Value = 2;
            material.ShadeThreshold.Value = 0.5f;
            material.RimColor.Value = new colorHDR(0.6f, 0.8f, 1.0f, 1f);
            material.RimPower.Value = 4f;
        });

        // Width above zero is what keeps the inverted hull alive on the next pass; at zero the hull
        // discards itself and Mat_FlatToonOutline never actually shades anything.
        CreateOrb<FlatToonMaterial>(root, "FlatToonOutline", index++, material =>
        {
            material.AlbedoColor.Value = new colorHDR(0.45f, 0.86f, 0.62f, 1f);
            material.ShadeColor.Value = new colorHDR(0.16f, 0.34f, 0.26f, 1f);
            material.Steps.Value = 3;
            material.OutlineColor.Value = new colorHDR(0.02f, 0.02f, 0.04f, 1f);
            material.OutlineWidth.Value = 0.02f;
        });

        // The workhorse: ramp texture in the shading slot, a rim band, an outline hull and a matcap all
        // on the one material, which is the combination imported avatars actually arrive with.
        CreateOrb<ToonMaterial>(root, "Toon", index++, material =>
        {
            material.AlbedoColor.Value = new colorHDR(0.92f, 0.74f, 0.62f, 1f);
            material.ShadowRamp.Target = toonRamp;
            material.ShadowTint.Value = new colorHDR(0.38f, 0.32f, 0.52f, 1f);
            material.ShadowBoundary.Value = 0.52f;
            material.ShadowSoftness.Value = 0.03f;
            material.RimColor.Value = new colorHDR(0.55f, 0.75f, 1.0f, 1f);
            material.RimPower.Value = 3f;
            material.RimBoundary.Value = 0.25f;
            material.OutlineColor.Value = new colorHDR(0.05f, 0.03f, 0.08f, 1f);
            material.OutlineWidth.Value = 0.006f;
            material.MatcapTexture.Target = matcapRamp;
            material.MatcapStrength.Value = 0.25f;
            material.MatcapAdditive.Value = true;
            material.Smoothness.Value = 0.6f;
        });

        CreateOrb<FresnelLerpMaterial>(root, "FresnelLerp", index++, material =>
        {
            material.Lerp.Value = 0.5f;
            material.NearColor0.Value = new colorHDR(0.9f, 0.15f, 0.35f, 1f);
            material.FarColor0.Value = new colorHDR(0.05f, 0.02f, 0.10f, 1f);
            material.Exponent0.Value = 2f;
            material.NearColor1.Value = new colorHDR(0.15f, 0.85f, 0.95f, 1f);
            material.FarColor1.Value = new colorHDR(0.02f, 0.10f, 0.20f, 1f);
            material.Exponent1.Value = 5f;
        });

        CreateOrb<MatcapMaterial>(root, "Matcap", index++, material =>
        {
            material.Matcap.Target = matcapRamp;
            material.TintColor.Value = new colorHDR(1f, 1f, 1f, 1f);
        });

        CreateOrb<OverlayFresnelMaterial>(root, "OverlayFresnel", index++, material =>
        {
            material.Exponent.Value = 3f;
            material.FrontNearColor.Value = new colorHDR(0f, 0f, 0f, 1f);
            material.FrontFarColor.Value = new colorHDR(0.3f, 1.0f, 0.7f, 1f);
            material.BehindNearColor.Value = new colorHDR(0f, 0f, 0f, 1f);
            material.BehindFarColor.Value = new colorHDR(0.9f, 0.25f, 0.25f, 1f);
        });

        CreateOrb<OverlayUnlitMaterial>(root, "OverlayUnlit", index++, material =>
        {
            material.Texture.Target = checker;
            material.FrontTintColor.Value = new colorHDR(0.35f, 0.75f, 1.0f, 1f);
            material.BehindTintColor.Value = new colorHDR(0.9f, 0.35f, 0.15f, 0.6f);
            material.UseVertexColor.Value = false;
        });

        // A flat sheet is the only shape where a different back face reads as anything.
        CreateQuadPreview<PBS_MetallicDualSided>(root, "PBS_MetallicDualSided", index++, new float2(0.44f, 0.44f), material =>
        {
            material.FrontAlbedoColor.Value = new colorHDR(0.95f, 0.85f, 0.30f, 1f);
            material.BackAlbedoColor.Value = new colorHDR(0.20f, 0.35f, 0.90f, 1f);
            material.FrontEmissiveColor.Value = new colorHDR(0.15f, 0.12f, 0.02f, 1f);
            material.Smoothness.Value = 0.4f;
        });

        CreateBoxPreview<PBS_Triplanar>(root, "PBS_Triplanar", index++, new float3(0.42f, 0.42f, 0.42f), material =>
        {
            material.AlbedoTexture.Target = checker;
            material.Tiling.Value = new float3(3f, 3f, 3f);
            material.BlendSharpness.Value = 6f;
            material.WorldSpace.Value = true;
            material.Metallic.Value = 0.1f;
            material.Smoothness.Value = 0.45f;
        });

        CreateVertexColorPreview(root, "PBS_VertexColor", index++, useAlpha: false);
        CreateVertexColorPreview(root, "PBS_VertexColorTransparent", index++, useAlpha: true);

        CreateWireframePreview(root, "Wireframe", index++, overDepth: false);
        CreateWireframePreview(root, "WireframeOverlay", index++, overDepth: true);

        CreateWorldTextPreview(root, "Text_Unlit", index++, font);
        CreateCanvasTextPreview(root, "UI_Text", index++, font);
        CreatePropertyBlockPreview(root, "MainTexturePropertyBlock", index++, checker);

        return first;
    }

    private static T CreateOrb<T>(Slot parent, string name, int index, Action<T>? configure = null)
        where T : MaterialProvider, new()
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 0.18f;
        mesh.Segments.Value = 32;
        mesh.Rings.Value = 16;
        mesh.UVScale.Value = new float2(5f, 2.5f);

        var collider = slot.AttachComponent<SphereCollider>();
        collider.CharacterCollider.Value = true;
        collider.Radius.Value = mesh.Radius.Value;
        collider.Type.Value = ColliderType.Trigger;

        var material = slot.AttachComponent<T>();
        configure?.Invoke(material);

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.On;

        return material;
    }

    // The rising material only makes sense on a BubbleFieldMesh (it reads the bubble index and
    // shape from the mesh), so its preview is a miniature of the home's field, not a box. -xlinka
    private static void CreateBubblePreview(Slot parent, string name, int index)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.AttachComponent<Grabbable>();

        const float extent = 0.2f;
        const float height = 0.42f;
        var offset = new float3(0f, -0.21f, 0f);

        var mesh = slot.AttachComponent<BubbleFieldMesh>();
        mesh.Count.Value = 36;
        mesh.VolumeExtents.Value = new float2(extent, extent);
        mesh.VolumeHeight.Value = height;
        mesh.VolumeOffset.Value = offset;
        mesh.BoundsMargin.Value = 0.1f;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = new float3(extent * 2f, height, extent * 2f);
        collider.Type.Value = ColliderType.Trigger;

        var material = slot.AttachComponent<LocalHomeRisingMaterial>();
        material.BlobCount.Value = 36;
        material.BlobRadius.Value = 0.03f;
        material.VolumeExtents.Value = new float2(extent, extent);
        material.VolumeHeight.Value = height;
        material.VolumeOffset.Value = offset;
        material.RippleRadius.Value = 0.12f;
        material.RippleWidth.Value = 0.01f;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    private static T CreateBoxPreview<T>(Slot parent, string name, int index, float3 size, Action<T>? configure = null)
        where T : MaterialProvider, new()
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = size;
        mesh.UVScale.Value = new float3(2f, 2f, 2f);

        var collider = slot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = size;
        collider.Type.Value = ColliderType.Trigger;

        var material = slot.AttachComponent<T>();
        configure?.Invoke(material);

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.On;

        return material;
    }

    private static T CreateQuadPreview<T>(Slot parent, string name, int index, float2 size, Action<T>? configure = null)
        where T : MaterialProvider, new()
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<QuadMesh>();
        mesh.Size.Value = size;
        mesh.DualSided.Value = false;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = new float3(size.x, size.y, 0.02f);
        collider.Type.Value = ColliderType.Trigger;

        var material = slot.AttachComponent<T>();
        configure?.Invoke(material);

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        return material;
    }

    // The vertex colours live on the mesh, not the material, so the preview has to be a quad that
    // actually carries them. The alpha variant swaps the shader for a blended one. -xlinka
    private static void CreateVertexColorPreview(Slot parent, string name, int index, bool useAlpha)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<QuadMesh>();
        mesh.Size.Value = new float2(0.44f, 0.44f);
        mesh.DualSided.Value = true;
        mesh.UseVertexColors.Value = true;
        float alpha = useAlpha ? 0.15f : 1f;
        mesh.UpperLeftColor.Value = new color(0.95f, 0.25f, 0.35f, 1f);
        mesh.UpperRightColor.Value = new color(0.25f, 0.85f, 0.45f, alpha);
        mesh.LowerLeftColor.Value = new color(0.30f, 0.45f, 0.95f, alpha);
        mesh.LowerRightColor.Value = new color(0.95f, 0.90f, 0.30f, 1f);

        var collider = slot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = new float3(0.44f, 0.44f, 0.02f);
        collider.Type.Value = ColliderType.Trigger;

        var material = slot.AttachComponent<PBS_VertexColor>();
        material.AlbedoColor.Value = colorHDR.White;
        material.Metallic.Value = 0f;
        material.Smoothness.Value = 0.3f;
        material.UseVertexAlpha.Value = useAlpha;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    // Wires only exist where the triangles were unwelded and their corner coordinates baked into the
    // vertex colour channel, which is what WireframeBarycentrics does. Without it the surface draws
    // and the lines silently do not. -xlinka
    private static void CreateWireframePreview(Slot parent, string name, int index, bool overDepth)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<IcoSphereMesh>();
        mesh.Radius.Value = 0.18f;
        mesh.Subdivisions.Value = overDepth ? 1 : 2;
        mesh.FlatShading.Value = false;
        mesh.WireframeBarycentrics.Value = true;

        var collider = slot.AttachComponent<SphereCollider>();
        collider.CharacterCollider.Value = true;
        collider.Radius.Value = 0.18f;
        collider.Type.Value = ColliderType.Trigger;

        var material = slot.AttachComponent<WireframeMaterial>();
        material.LineColor.Value = overDepth
            ? new colorHDR(1.0f, 0.55f, 0.15f, 1f)
            : new colorHDR(0.35f, 1.0f, 0.85f, 1f);
        material.LineWidth.Value = 1.6f;
        material.UseVertexBarycentric.Value = true;
        material.FillColor.Value = new colorHDR(0.03f, 0.04f, 0.06f, 0.65f);
        material.DrawOverDepth.Value = overDepth;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    // TextRenderer builds its own renderer slot and TextMaterial, so this is the only honest way to
    // put the world text shader on screen.
    private static TextRenderer CreateWorldTextPreview(Slot parent, string name, int index, FontProvider font)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);

        var text = slot.AttachComponent<TextRenderer>();
        text.Text.Value = "Text";
        text.Size.Value = 0.11f;
        text.Color.Value = new color(0.95f, 0.98f, 1f, 1f);
        text.Font.Target = font;
        text.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0.9f);
        text.OutlineThickness.Value = 0.9f;
        return text;
    }

    private static void CreateCanvasTextPreview(Slot parent, string name, int index, FontProvider font)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.LocalScale.Value = new float3(0.0016f, 0.0016f, 0.0016f);

        var rect = slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-140f, -50f);
        rect.OffsetMax.Value = new float2(140f, 50f);
        slot.AttachComponent<Canvas>();

        var background = slot.AttachComponent<Helio.UI.Image>();
        background.Tint.Value = new color(0.04f, 0.05f, 0.07f, 0.9f);

        var labelSlot = slot.AddSlot("Label");
        FillRect(labelSlot.AttachComponent<RectTransform>());
        var label = labelSlot.AttachComponent<Helio.UI.Text>();
        label.Content.Value = "UI_Text";
        label.Size.Value = 34f;
        label.Color.Value = new color(0.85f, 0.95f, 1f, 1f);
        label.Font.Target = font;
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
    }

    // A property block is a per-renderer override of one material's uniforms, so the point of the
    // preview is that this orb shows the checker while the material it shares stays untextured.
    private static void CreatePropertyBlockPreview(Slot parent, string name, int index, CheckerTextureProvider checker)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = GetOrbPosition(index);
        PreviewLabel(parent, name, index);
        slot.AttachComponent<Grabbable>();

        var mesh = slot.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 0.18f;
        mesh.Segments.Value = 32;
        mesh.Rings.Value = 16;
        mesh.UVScale.Value = new float2(3f, 1.5f);

        var collider = slot.AttachComponent<SphereCollider>();
        collider.CharacterCollider.Value = true;
        collider.Radius.Value = mesh.Radius.Value;
        collider.Type.Value = ColliderType.Trigger;

        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.75f, 0.78f, 0.85f, 1f);
        material.Metallic.Value = 0.2f;
        material.Smoothness.Value = 0.55f;

        var block = slot.AttachComponent<MainTexturePropertyBlock>();
        block.Texture.Target = checker;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.MaterialPropertyBlocks.Add().Target = block;
        renderer.ShadowCastMode.Value = ShadowCastMode.On;
    }

    private static void CreateCustomOrb(Slot parent, string name, int index, string shaderPath, BlendMode blendMode, Culling culling, Action<CustomShaderMaterial>? configure = null)
    {
        CreateOrb<CustomShaderMaterial>(parent, name, index, material =>
        {
            material.ShaderPath.Value = shaderPath;
            material.BlendMode.Value = blendMode;
            material.Culling.Value = culling;
            configure?.Invoke(material);
        });
    }

    private static void AddColorParam(CustomShaderMaterial material, string name, colorHDR value)
    {
        var param = material.Parameters.Add();
        param.Name.Value = name;
        param.Type.Value = ShaderUniformType.Vec4;
        param.IsColor.Value = true;
        param.Value.Value = new float4(value.r, value.g, value.b, value.a);
    }

    private static void AddFloatParam(CustomShaderMaterial material, string name, float value)
    {
        var param = material.Parameters.Add();
        param.Name.Value = name;
        param.Type.Value = ShaderUniformType.Float;
        param.Value.Value = new float4(value, 0f, 0f, 0f);
    }

    private static void AddBoolParam(CustomShaderMaterial material, string name, bool value)
    {
        var param = material.Parameters.Add();
        param.Name.Value = name;
        param.Type.Value = ShaderUniformType.Bool;
        param.Value.Value = new float4(value ? 1f : 0f, 0f, 0f, 0f);
    }

    private static float3 GetOrbPosition(int index)
    {
        const int columns = 6;
        const float spacing = 0.8f;
        int column = index % columns;
        int row = index / columns;
        float x = (column - (columns - 1) * 0.5f) * spacing;
        // Rows walk AWAY from spawn. The grid is five rows deep, and rows marching toward the origin
        // would have parked the back half of it inside the spawn circle. -xlinka
        float z = -row * spacing;
        return new float3(x, 0f, z);
    }

    // A ball with no name on it is a ball. Every preview in the sheet gets its shader's name on the
    // floor-facing side so you can tell the fresnel from the fresnel-lerp without opening a panel.
    private static void PreviewLabel(Slot parent, string name, int index)
    {
        var position = GetOrbPosition(index);
        Sign(parent, name, new float3(position.x, position.y - 0.32f, position.z), 0.055f);
    }

    // PHYSICS AND SOFT BODIES
    //
    // Jolt's rigid bodies on the left of the plate, our own CPU soft bodies on the right, so the two
    // simulations are side by side and a regression in either one is obvious from the same spot.
    private static void CreatePhysicsShowcase(World world)
    {
        var root = world.RootSlot.AddSlot("Physics And Soft Bodies");
        root.LocalPosition.Value = PhysicsOrigin;

        AreaPlate(world, "Physics", new float2(PhysicsOrigin.x, PhysicsOrigin.z),
            new float2(7.4f, 5.2f), new colorHDR(0.22f, 0.24f, 0.16f, 1f));
        Sign(root, "Physics and soft bodies", new float3(0f, 2.6f, 0f), 0.24f);

        CreateFallingStack(root, new float3(-2.6f, 0f, 0f));
        CreateSquishyJelly(root, new float3(-0.6f, 0f, 0f));
        CreateDrapeTest(root, new float3(1.8f, 0f, 0f));
        CreateCurtains(root, new float3(-1.9f, 0f, 1.9f));
        CreateBanner(root, new float3(1.6f, 0f, 1.9f));
    }

    private static void CreateFallingStack(Slot parent, float3 localOrigin)
    {
        var stack = parent.AddSlot("Falling Cubes");
        stack.LocalPosition.Value = localOrigin;

        Sign(stack, "Falling cubes (RigidBody)\ngrab one and drop it", new float3(0f, 2.0f, 0f), 0.1f);

        CreatePropCube(stack, "Cube A", new float3(0f, 1.2f, 0f), new colorHDR(0.9f, 0.45f, 0.2f, 1f));
        CreatePropCube(stack, "Cube B", new float3(0.15f, 2.0f, 0.1f), new colorHDR(0.3f, 0.8f, 0.45f, 1f));
        CreatePropCube(stack, "Cube C", new float3(-0.12f, 2.8f, -0.08f), new colorHDR(0.4f, 0.5f, 0.95f, 1f));
    }

    private static void CreatePropCube(Slot parent, string name, float3 position, colorHDR tint)
    {
        var cube = parent.AddSlot(name);
        cube.LocalPosition.Value = position;

        var mesh = cube.AttachComponent<BoxMesh>();
        mesh.Size.Value = float3.One * 0.35f;

        var material = cube.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = tint;
        material.Metallic.Value = 0.1f;
        material.Smoothness.Value = 0.5f;

        var renderer = cube.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        var collider = cube.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = mesh.Size.Value;

        var body = cube.AttachComponent<RigidBody>();
        body.Mass.Value = 1f;

        cube.AttachComponent<Grabbable>();
    }

    // A jelly ball that falls from head height, lands and squishes. This is OUR sim, not Jolt: the
    // particles collide against a ground PLANE at y=0, which is only the right answer because the
    // floor's visible top and its collider top both sit there now.
    private static void CreateSquishyJelly(Slot parent, float3 localOrigin)
    {
        var jelly = parent.AddSlot("Squishy Jelly");
        jelly.LocalPosition.Value = localOrigin + new float3(0f, 1.8f, 0f);

        var jellyMesh = jelly.AttachComponent<SphereMesh>();
        jellyMesh.Radius.Value = 0.4f;
        jellyMesh.Segments.Value = 18;

        var jellyMat = jelly.AttachComponent<PBS_Metallic>();
        jellyMat.AlbedoColor.Value = new colorHDR(0.45f, 0.9f, 0.6f, 1f);
        jellyMat.Metallic.Value = 0f;
        jellyMat.Smoothness.Value = 0.55f;

        var squishy = jelly.AttachComponent<SquishyBody>();
        squishy.SourceMesh.Target = jellyMesh;
        squishy.Material.Target = jellyMat;
        squishy.Stiffness.Value = 0.3f;        // light - shape retention does the shaping
        squishy.Damping.Value = 0.06f;
        squishy.Iterations.Value = 6;
        squishy.Pressure.Value = 0f;
        squishy.ShapeRetention.Value = 0.3f;   // holds the ball form, squishes + springs back, never explodes
        squishy.ParticleRadius.Value = 0.05f;
        squishy.GroundY.Value = 0f;             // the scratch floor top sits at y=0 (no raycast, just a plane)
        // On, so it answers to the player and to anything thrown at it. It used to be off for cost, but
        // the resting-contact resolve is per moving particle and this ball is a coarse sphere: it is the
        // demo people walk up to and shove, and a jelly that ignores you is not a jelly. -xlinka
        squishy.CollideWithWorld.Value = true;

        Sign(parent, "Jelly ball (SquishyBody)\nfalls and squashes", localOrigin + new float3(0f, 2.6f, 0f), 0.1f);
    }

    // A horizontal cloth dropped onto a solid box so it drapes like a tablecloth. The grid is built on
    // the local XY plane with its normal on +Z, so it gets rotated -90 about X to lie flat.
    private static void CreateDrapeTest(Slot parent, float3 localOrigin)
    {
        var table = parent.AddSlot("Drape Box");
        table.LocalPosition.Value = localOrigin + new float3(0f, 0.35f, 0f);
        var tableMesh = table.AttachComponent<BoxMesh>();
        tableMesh.Size.Value = new float3(0.7f, 0.7f, 0.7f);
        var tableMat = table.AttachComponent<PBS_Metallic>();
        tableMat.AlbedoColor.Value = new colorHDR(0.5f, 0.5f, 0.55f, 1f);
        var tableRenderer = table.AttachComponent<MeshRenderer>();
        tableRenderer.Mesh.Target = tableMesh;
        tableRenderer.Material.Target = tableMat;
        var tableCollider = table.AttachComponent<BoxCollider>();
        tableCollider.CharacterCollider.Value = true;
        tableCollider.Type.Value = ColliderType.Static;
        tableCollider.Size.Value = tableMesh.Size.Value;
        // The cloth finds this BoxCollider (and any other world collider) through CollideWithWorld's
        // resting-contact resolve - no per-object soft collider needed anymore. -xlinka

        var cloth = parent.AddSlot("Squishy Cloth");
        cloth.LocalPosition.Value = localOrigin + new float3(0f, 1.3f, 0f);
        cloth.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, -MathF.PI * 0.5f);

        var clothMesh = cloth.AttachComponent<GridMesh>();
        clothMesh.Size.Value = new float2(1.6f, 1.6f);
        clothMesh.SegmentsX.Value = 20;
        clothMesh.SegmentsY.Value = 20;

        var clothMat = cloth.AttachComponent<PBS_Metallic>();
        clothMat.AlbedoColor.Value = new colorHDR(0.9f, 0.35f, 0.55f, 1f);
        clothMat.Metallic.Value = 0f;
        clothMat.Smoothness.Value = 0.4f;
        clothMat.Culling.Value = Culling.None;

        var clothSquishy = cloth.AttachComponent<SquishyBody>();
        clothSquishy.SourceMesh.Target = clothMesh;
        clothSquishy.Material.Target = clothMat;
        clothSquishy.Stiffness.Value = 0.9f;   // stiff edges hold the sheet; the folds come from draping
        clothSquishy.Damping.Value = 0.18f;    // bleeds off swing energy so it settles instead of buzzing
        clothSquishy.Iterations.Value = 14;
        clothSquishy.Pressure.Value = 0f;      // cloth, not a balloon
        clothSquishy.ParticleRadius.Value = 0.05f;
        clothSquishy.GroundY.Value = 0f;
        clothSquishy.CollideWithWorld.Value = true; // rests on the box AND any orb/prop/avatar with a collider
        // no pins - it's a free sheet that falls onto the box

        Sign(parent, "Cloth over a box (SquishyBody)", localOrigin + new float3(0f, 2.2f, 0f), 0.1f);
    }

    // Two panels on a rail with a gap you can walk through. This is the pinned-and-windy half of the
    // cloth story: the drape test has no pins and no wind, so nothing there ever moves once it has
    // settled. A curtain hangs from its top row forever, breathes on the wind, and gets shoved out of
    // the way when you walk into it. Wind also keeps the body awake, which is the point of putting it
    // where people walk. -xlinka
    private static void CreateCurtains(Slot parent, float3 localOrigin)
    {
        var root = parent.AddSlot("Curtains");
        root.LocalPosition.Value = localOrigin;

        var rail = root.AddSlot("Rail");
        rail.LocalPosition.Value = new float3(0f, 2.25f, 0f);
        var railMesh = rail.AttachComponent<CylinderMesh>();
        railMesh.Radius.Value = 0.03f;
        railMesh.Height.Value = 2.6f;
        railMesh.Segments.Value = 12;
        rail.LocalRotation.Value = floatQ.AxisAngleRad(float3.Forward, MathF.PI * 0.5f);
        var railMat = rail.AttachComponent<PBS_Metallic>();
        railMat.AlbedoColor.Value = new colorHDR(0.42f, 0.34f, 0.24f, 1f);
        railMat.Metallic.Value = 0.6f;
        railMat.Smoothness.Value = 0.5f;
        var railRenderer = rail.AttachComponent<MeshRenderer>();
        railRenderer.Mesh.Target = railMesh;
        railRenderer.Material.Target = railMat;

        CurtainPanel(root, new float3(-0.66f, 1.35f, 0f), new colorHDR(0.72f, 0.28f, 0.32f, 1f), 2.2f);
        CurtainPanel(root, new float3(0.66f, 1.35f, 0f), new colorHDR(0.72f, 0.28f, 0.32f, 1f), 1.8f);

        Sign(root, "Curtains (SquishyBody)\npinned top edge, wind, walk through them", new float3(0f, 2.7f, 0f), 0.1f);
    }

    // One panel. The grid is built on the local XY plane with its normal on +Z, which is already the way
    // a curtain hangs, so unlike the drape test this needs no rotation.
    private static void CurtainPanel(Slot parent, float3 localPosition, in colorHDR tint, float windZ)
    {
        var panel = parent.AddSlot("Curtain Panel");
        panel.LocalPosition.Value = localPosition;

        var mesh = panel.AttachComponent<GridMesh>();
        mesh.Size.Value = new float2(1.1f, 1.8f);
        mesh.SegmentsX.Value = 14;
        mesh.SegmentsY.Value = 22;

        var material = panel.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = tint;
        material.Metallic.Value = 0f;
        material.Smoothness.Value = 0.25f;
        material.Culling.Value = Culling.None;

        // No MeshRenderer on the panel: the body draws the simulated sheet on its own child, and a second
        // renderer on the grid would stand a flat copy in front of it.
        var cloth = panel.AttachComponent<SquishyBody>();
        cloth.SourceMesh.Target = mesh;
        cloth.Material.Target = material;
        cloth.Stiffness.Value = 0.75f;
        cloth.BendStiffness.Value = 0.08f;    // soft enough to fold around a shoulder
        cloth.Damping.Value = 0.03f;          // light, so a shove swings back past rest instead of creeping home
        cloth.Friction.Value = 0.4f;          // a hand drags the fabric along instead of gluing it in place
        cloth.Iterations.Value = 12;
        cloth.Pressure.Value = 0f;
        cloth.ShapeRetention.Value = 0f;      // a sheet, not a shape that springs back
        cloth.ParticleRadius.Value = 0.04f;
        cloth.GroundY.Value = 0f;
        cloth.CollideWithWorld.Value = true;
        // The grid's local Y runs -0.9 to +0.9, so this pins the top row and nothing else.
        cloth.PinAboveLocalY.Value = 0.85f;
        // Wind is an acceleration on a face-on sheet, so this is a lean of about twelve degrees that the
        // gusts swing between a few and twenty.
        cloth.Wind.Value = new float3(0f, 0f, windZ);
        cloth.WindGustiness.Value = 0.8f;
    }

    // A hanging banner: narrow, top-pinned, and in a stiffer breeze than the curtains so it visibly
    // ripples rather than just leaning. Same component, three different looks across the plate.
    private static void CreateBanner(Slot parent, float3 localOrigin)
    {
        var root = parent.AddSlot("Banner");
        root.LocalPosition.Value = localOrigin;

        var pole = root.AddSlot("Pole");
        pole.LocalPosition.Value = new float3(0f, 1.3f, 0f);
        var poleMesh = pole.AttachComponent<CylinderMesh>();
        poleMesh.Radius.Value = 0.035f;
        poleMesh.Height.Value = 2.6f;
        poleMesh.Segments.Value = 12;
        var poleMat = pole.AttachComponent<PBS_Metallic>();
        poleMat.AlbedoColor.Value = new colorHDR(0.3f, 0.32f, 0.36f, 1f);
        poleMat.Metallic.Value = 0.7f;
        poleMat.Smoothness.Value = 0.55f;
        var poleRenderer = pole.AttachComponent<MeshRenderer>();
        poleRenderer.Mesh.Target = poleMesh;
        poleRenderer.Material.Target = poleMat;

        var arm = root.AddSlot("Arm");
        arm.LocalPosition.Value = new float3(0.35f, 2.5f, 0f);
        arm.LocalRotation.Value = floatQ.AxisAngleRad(float3.Forward, MathF.PI * 0.5f);
        var armMesh = arm.AttachComponent<CylinderMesh>();
        armMesh.Radius.Value = 0.025f;
        armMesh.Height.Value = 0.75f;
        armMesh.Segments.Value = 10;
        var armRenderer = arm.AttachComponent<MeshRenderer>();
        armRenderer.Mesh.Target = armMesh;
        armRenderer.Material.Target = poleMat;

        var banner = root.AddSlot("Banner Cloth");
        banner.LocalPosition.Value = new float3(0.35f, 1.75f, 0f);

        var mesh = banner.AttachComponent<GridMesh>();
        mesh.Size.Value = new float2(0.62f, 1.4f);
        mesh.SegmentsX.Value = 10;
        mesh.SegmentsY.Value = 20;

        var material = banner.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.25f, 0.45f, 0.85f, 1f);
        material.Metallic.Value = 0f;
        material.Smoothness.Value = 0.3f;
        material.Culling.Value = Culling.None;

        var cloth = banner.AttachComponent<SquishyBody>();
        cloth.SourceMesh.Target = mesh;
        cloth.Material.Target = material;
        cloth.Stiffness.Value = 0.8f;
        cloth.BendStiffness.Value = 0.06f;
        cloth.Damping.Value = 0.02f;   // less bleed than the curtains, so the ripple carries
        cloth.Friction.Value = 0.4f;
        cloth.Iterations.Value = 12;
        cloth.Pressure.Value = 0f;
        cloth.ShapeRetention.Value = 0f;
        cloth.ParticleRadius.Value = 0.035f;
        cloth.GroundY.Value = 0f;
        cloth.CollideWithWorld.Value = true;
        cloth.PinAboveLocalY.Value = 0.65f;   // local Y runs -0.7 to +0.7
        cloth.Wind.Value = new float3(0.3f, 0f, 3.2f);
        cloth.WindGustiness.Value = 0.8f;

        Sign(root, "Banner (SquishyBody)\ntop-pinned, stiffer breeze", new float3(0f, 2.9f, 0f), 0.1f);
    }

    // RENDERING
    //
    // Probe, sky arbiter, both LOD paths, the phased particle pipeline and a world-space sign, all on
    // one plate so a rendering regression has one place to show up.
    private static void CreateRenderingShowcase(World world)
    {
        var root = world.RootSlot.AddSlot("Rendering Showcase");
        root.LocalPosition.Value = RenderingOrigin;

        AreaPlate(world, "Rendering", new float2(RenderingOrigin.x, RenderingOrigin.z),
            new float2(7f, 4.6f), new colorHDR(0.16f, 0.22f, 0.26f, 1f));
        Sign(root, "Rendering\nprobe, sky, LOD, particles", new float3(0f, 2.7f, 0f), 0.24f);

        var shared = root.AttachComponent<PBS_Metallic>();
        shared.AlbedoColor.Value = new colorHDR(0.72f, 0.74f, 0.80f, 1f);
        shared.Metallic.Value = 0.35f;
        shared.Smoothness.Value = 0.6f;

        // The probe belongs over the orb grid, which is a long way the other way, so its slot carries
        // the whole offset rather than the area root. Box projection is on because the grid is a wall
        // of reflective spheres and infinity-mapped reflections read wrong on them.
        var probeSlot = root.AddSlot("Orb Reflection Probe");
        probeSlot.LocalPosition.Value = MaterialsOrigin - RenderingOrigin + new float3(0f, 0.3f, -1.4f);
        var probe = probeSlot.AttachComponent<ReflectionProbe>();
        probe.Size.Value = new float3(6f, 3.5f, 5f);
        probe.UpdateMode.Value = ProbeUpdateMode.Once;
        probe.BoxProjection.Value = true;
        probe.Intensity.Value = 1f;
        probe.BlendDistance.Value = 1.5f;

        // A marker where the probe actually is, standing on the materials plate rather than here: an
        // invisible component in a "look at the rendering" area is a thing nobody can find.
        Sign(probeSlot, "Reflection probe (ReflectionProbe)\ncovers the orb grid", new float3(0f, 1.6f, 1.9f), 0.09f);

        // Deliberately inactive. A Skybox always outranks a GradientSkybox, so leaving this enabled
        // would swap the whole world's sky the moment the template loaded. Tick the slot active to
        // watch the arbiter hand the environment over. -xlinka
        var skySlot = root.AddSlot("Cubemap Sky (disabled)");
        var cubemap = skySlot.AttachComponent<GradientCubemap>();
        cubemap.Size.Value = 256;
        cubemap.TopColor.Value = new color(0.10f, 0.16f, 0.42f, 1f);
        cubemap.HorizonColor.Value = new color(0.85f, 0.55f, 0.35f, 1f);
        cubemap.BottomColor.Value = new color(0.06f, 0.05f, 0.05f, 1f);
        cubemap.DrawSun.Value = true;
        cubemap.SunColor.Value = new color(1f, 0.92f, 0.72f, 1f);
        cubemap.SunDirection.Value = new float3(0.5f, 0.35f, -0.79f);
        var sky = skySlot.AttachComponent<Skybox>();
        sky.Cubemap.Target = cubemap;
        sky.Priority.Value = 0;
        sky.AmbientFromSky.Value = true;
        sky.ReflectionsFromSky.Value = true;
        skySlot.ActiveSelf.Value = false;

        Sign(root, "Spare sky, switched off (Skybox)\nturn the slot on to swap the sky",
            new float3(-2.4f, 1.9f, 1.2f), 0.09f);

        // Three IcoSpheres of falling subdivision under one group. The bands are distances in metres,
        // so walking backwards from the group swaps 4 -> 2 -> 0 and then culls.
        var lodSlot = root.AddSlot("LOD Group");
        lodSlot.LocalPosition.Value = new float3(-2.2f, 1.3f, 0f);
        Post(root, new float2(-2.2f, 0f), 0.98f);
        var lodGroup = lodSlot.AttachComponent<LodGroup>();
        lodGroup.CrossfadeMargin.Value = 0.5f;
        lodGroup.CullBeyondLast.Value = true;
        lodGroup.AddLevel(8f, CreateLodLevel(lodSlot, "LOD 0 (fine)", 4, shared));
        lodGroup.AddLevel(18f, CreateLodLevel(lodSlot, "LOD 1 (mid)", 2, shared));
        lodGroup.AddLevel(40f, CreateLodLevel(lodSlot, "LOD 2 (coarse)", 0, shared));
        Sign(root, "LOD group (LodGroup)\nwalk away and watch it get simpler", new float3(-2.2f, 2.0f, 0f), 0.09f);

        var cullSlot = root.AddSlot("Distance Culled Prop");
        cullSlot.LocalPosition.Value = new float3(0f, 1.3f, 0f);
        Post(root, new float2(0f, 0f), 1.02f);
        var cullMesh = cullSlot.AttachComponent<TorusMesh>();
        cullMesh.MajorRadius.Value = 0.28f;
        cullMesh.MinorRadius.Value = 0.09f;
        cullMesh.MajorSegments.Value = 32;
        cullMesh.MinorSegments.Value = 14;
        var cullRenderer = cullSlot.AttachComponent<MeshRenderer>();
        cullRenderer.Mesh.Target = cullMesh;
        cullRenderer.Material.Target = shared;
        var cull = cullSlot.AttachComponent<LodDistanceCull>();
        cull.MaxDistance.Value = 14f;
        cull.FadeMargin.Value = 2f;
        Sign(root, "Vanishing ring (LodDistanceCull)\ngone past 14 m", new float3(0f, 2.0f, 0f), 0.09f);

        CreateParticleFountain(root, new float3(2.2f, 0.05f, 0f));
        Sign(root, "Fountain (ParticleSystem)", new float3(2.2f, 2.0f, 0f), 0.09f);
    }

    private static Slot CreateLodLevel(Slot parent, string name, int subdivisions, PBS_Metallic material)
    {
        var slot = parent.AddSlot(name);
        var mesh = slot.AttachComponent<IcoSphereMesh>();
        mesh.Radius.Value = 0.32f;
        mesh.Subdivisions.Value = subdivisions;
        mesh.FlatShading.Value = subdivisions == 0;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        return slot;
    }

    // Emitter and modules are separate components pointed at the system, which is the phased
    // pipeline's whole point: attaching the cone emitter retires the system's built-in disc spray,
    // and the speed/lifetime initializers are what actually launch the particles after that. The
    // size and colour envelope stays with the system because nothing here overrides appearance.
    // -xlinka
    private static void CreateParticleFountain(Slot parent, float3 localPosition)
    {
        var slot = parent.AddSlot("Particle Fountain");
        slot.LocalPosition.Value = localPosition;

        var system = slot.AttachComponent<ParticleSystem>();
        // Lifetime initializers used to be dead weight (see ParticleSimulation.FinishNewParticles), so
        // this cap was sized for particles that quietly died on the emitter's 1 s default. Now the
        // authored 1.4-2.1 s range actually lands, so the rate has to come down to keep the live count
        // modest instead of the cap.
        system.MaxParticles.Value = 380;
        system.Gravity.Value = -3.2f;
        system.StartSize.Value = 0.05f;
        system.EndSize.Value = 0.012f;
        system.StartColor.Value = new colorHDR(0.55f, 0.90f, 1.00f, 0.95f);
        system.EndColor.Value = new colorHDR(0.20f, 0.35f, 0.95f, 0f);
        system.EmissionStrength.Value = 2.2f;
        system.FixedTimeStep.Value = 1f / 90f;

        var nozzle = slot.AddSlot("Nozzle");
        nozzle.LocalPosition.Value = new float3(0f, 0.05f, 0f);
        var cone = nozzle.AttachComponent<ConeEmitter>();
        cone.System.Target = system;
        cone.Rate.Value = 130f;
        cone.Radius.Value = 0.05f;
        cone.Angle.Value = 12f;

        var speed = slot.AttachComponent<ParticleSpeedRangeInitializer>();
        speed.System.Target = system;
        speed.MinSpeed.Value = 2.3f;
        speed.MaxSpeed.Value = 3.0f;

        // Was 1.4-2.1 - a seeded run can land close enough to the cell edge (a per-system random seed
        // means every run is a slightly different draw) that the old range only cleared the bound by a
        // few centimetres. Trimmed for real headroom, not just a headroom that happened to hold once.
        var life = slot.AttachComponent<ParticleLifetimeRangeInitializer>();
        life.System.Target = system;
        life.MinLifetime.Value = 1.2f;
        life.MaxLifetime.Value = 1.8f;

        var drag = slot.AttachComponent<ParticleDrag>();
        drag.System.Target = system;
        drag.Drag.Value = 0.35f;

        var turbulence = slot.AttachComponent<ParticleTurbulence>();
        turbulence.System.Target = system;
        turbulence.Strength.Value = 0.7f;
        turbulence.Frequency.Value = 1.4f;
        turbulence.ScrollSpeed.Value = 0.5f;
    }

    // MESHES
    //
    // Every procedural generator in one row on one shared material, so a broken generator shows up as
    // a hole in the row rather than as a mesh nobody happened to use.
    private static void CreateMeshGallery(World world)
    {
        var root = world.RootSlot.AddSlot("Mesh Gallery");
        root.LocalPosition.Value = MeshesOrigin;

        // Six columns of 1.6 and three rows of 1.6 covers 8 by 3.2; the plate takes the margin.
        AreaPlate(world, "Meshes", new float2(MeshesOrigin.x, MeshesOrigin.z - 1.6f),
            new float2(9.6f, 5.2f), new colorHDR(0.26f, 0.22f, 0.16f, 1f));
        Sign(root, "Meshes\nevery generator we ship", new float3(0f, 2.6f, 1.7f), 0.24f);

        var shared = root.AttachComponent<PBS_Metallic>();
        shared.AlbedoColor.Value = new colorHDR(0.80f, 0.62f, 0.35f, 1f);
        shared.Metallic.Value = 0.55f;
        shared.Smoothness.Value = 0.7f;
        shared.Culling.Value = Culling.None;

        int index = 0;

        var torus = NewMeshStand(root, "Torus", index++).AttachComponent<TorusMesh>();
        torus.MajorRadius.Value = 0.20f;
        torus.MinorRadius.Value = 0.07f;
        torus.MajorSegments.Value = 32;
        torus.MinorSegments.Value = 14;
        BindMesh(torus, shared);

        var tube = NewMeshStand(root, "Tube", index++).AttachComponent<TubeMesh>();
        tube.Radius.Value = 0.11f;
        tube.Length.Value = 0.42f;
        tube.Sides.Value = 22;
        tube.Segments.Value = 2;
        tube.Caps.Value = true;
        BindMesh(tube, shared);

        var bent = NewMeshStand(root, "BentTube", index++).AttachComponent<BentTubeMesh>();
        bent.Radius.Value = 0.05f;
        bent.BendRadius.Value = 0.20f;
        bent.Angle.Value = 150f;
        bent.Sides.Value = 16;
        bent.Segments.Value = 24;
        bent.Caps.Value = true;
        BindMesh(bent, shared);

        var bezier = NewMeshStand(root, "BezierTube", index++).AttachComponent<BezierTubeMesh>();
        bezier.Point0.Value = new float3(-0.22f, -0.20f, 0f);
        bezier.Point1.Value = new float3(-0.22f, 0.22f, 0.2f);
        bezier.Point2.Value = new float3(0.22f, -0.22f, -0.2f);
        bezier.Point3.Value = new float3(0.22f, 0.20f, 0f);
        bezier.Radius.Value = 0.04f;
        bezier.Sides.Value = 14;
        bezier.Samples.Value = 28;
        bezier.Caps.Value = true;
        BindMesh(bezier, shared);

        var curvedPlane = NewMeshStand(root, "CurvedPlane", index++).AttachComponent<CurvedPlaneMesh>();
        curvedPlane.Size.Value = new float2(0.45f, 0.34f);
        curvedPlane.Curvature.Value = 0.6f;
        curvedPlane.Segments.Value = 20;
        BindMesh(curvedPlane, shared);

        var beam = NewMeshStand(root, "CurvedBeam", index++).AttachComponent<CurvedBeamMesh>();
        beam.Radius.Value = 0.035f;
        beam.Sides.Value = 12;
        beam.Segments.Value = 24;
        beam.StartPoint.Value = new float3(-0.22f, -0.2f, 0f);
        beam.DirectTargetPoint.Value = new float3(0.22f, 0.2f, 0f);
        beam.ActualTargetPoint.Value = new float3(0.22f, 0.2f, 0f);
        beam.StartPointColor.Value = new color(0.2f, 0.9f, 1f, 1f);
        beam.EndPointColor.Value = new color(1f, 0.4f, 0.7f, 1f);
        beam.Capped.Value = true;
        BindMesh(beam, shared);

        var ring = NewMeshStand(root, "Ring", index++).AttachComponent<RingMesh>();
        ring.InnerRadius.Value = 0.11f;
        ring.OuterRadius.Value = 0.21f;
        ring.Height.Value = 0.06f;
        ring.Segments.Value = 36;
        BindMesh(ring, shared);

        var segment = NewMeshStand(root, "Segment", index++).AttachComponent<SegmentMesh>();
        segment.Radius.Value = 0.045f;
        segment.Sides.Value = 12;
        segment.PointA.Value = new float3(-0.2f, -0.2f, 0f);
        segment.PointB.Value = new float3(0.2f, 0.2f, 0f);
        segment.PointAColor.Value = new color(1f, 0.85f, 0.3f, 1f);
        segment.PointBColor.Value = new color(0.3f, 0.6f, 1f, 1f);
        BindMesh(segment, shared);

        var stripe = NewMeshStand(root, "Stripe", index++).AttachComponent<StripeMesh>();
        stripe.Width.Value = 0.12f;
        stripe.Length.Value = 0.44f;
        stripe.RoundedEnds.Value = true;
        stripe.CapSegments.Value = 10;
        stripe.DualSided.Value = true;
        BindMesh(stripe, shared);

        var arrow = NewMeshStand(root, "Arrow", index++).AttachComponent<ArrowMesh>();
        arrow.ShaftRadius.Value = 0.04f;
        arrow.ShaftLength.Value = 0.26f;
        arrow.TipRadius.Value = 0.09f;
        arrow.TipLength.Value = 0.13f;
        arrow.Segments.Value = 18;
        BindMesh(arrow, shared);

        var bevel = NewMeshStand(root, "BevelBox", index++).AttachComponent<BevelBoxMesh>();
        bevel.Size.Value = new float3(0.34f, 0.34f, 0.34f);
        bevel.Bevel.Value = 0.06f;
        bevel.BevelSegments.Value = 4;
        BindMesh(bevel, shared);

        var capsule = NewMeshStand(root, "Capsule", index++).AttachComponent<CapsuleMesh>();
        capsule.Radius.Value = 0.10f;
        capsule.Height.Value = 0.54f;
        capsule.Segments.Value = 22;
        capsule.Rings.Value = 8;
        BindMesh(capsule, shared);

        var cone = NewMeshStand(root, "Cone", index++).AttachComponent<ConeMesh>();
        cone.RadiusBase.Value = 0.17f;
        cone.RadiusTop.Value = 0.0f;
        cone.Height.Value = 0.36f;
        cone.Segments.Value = 26;
        BindMesh(cone, shared);

        var cylinder = NewMeshStand(root, "Cylinder", index++).AttachComponent<CylinderMesh>();
        cylinder.Radius.Value = 0.13f;
        cylinder.Height.Value = 0.34f;
        cylinder.Segments.Value = 26;
        cylinder.Caps.Value = true;
        BindMesh(cylinder, shared);

        var ico = NewMeshStand(root, "IcoSphere", index++).AttachComponent<IcoSphereMesh>();
        ico.Radius.Value = 0.19f;
        ico.Subdivisions.Value = 2;
        ico.FlatShading.Value = true;
        BindMesh(ico, shared);

        var quad = NewMeshStand(root, "Quad", index++).AttachComponent<QuadMesh>();
        quad.Size.Value = new float2(0.38f, 0.38f);
        quad.DualSided.Value = true;
        BindMesh(quad, shared);

        var hull = NewMeshStand(root, "ConvexHull", index++).AttachComponent<ConvexHullMesh>();
        AddHullPoints(hull.Points);
        hull.FlatShading.Value = true;
        BindMesh(hull, shared);

        CreateHullProp(root, index);
    }

    // Every generator stands on its own leg with its own name plate. Eighteen nameless shapes on
    // invisible pedestals is exactly the "what am I even looking at" the walkthrough turned up.
    private static Slot NewMeshStand(Slot parent, string name, int index)
    {
        const int columns = 6;
        const float spacing = 1.6f;
        const float standHeight = 1.1f;
        int column = index % columns;
        int row = index / columns;
        float x = (column - (columns - 1) * 0.5f) * spacing;
        float z = -row * spacing;

        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = new float3(x, standHeight, z);

        Post(parent, new float2(x, z), standHeight - 0.2f, 0.045f);
        Sign(parent, name, new float3(x, standHeight - 0.42f, z), 0.075f);
        return slot;
    }

    private static void BindMesh(ProceduralMesh mesh, MaterialProvider material)
    {
        var renderer = mesh.Slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
    }

    // A lopsided scatter, because a symmetric cloud hides a hull solver that quietly dropped a face.
    private static void AddHullPoints(SyncFieldList<float3> points)
    {
        points.Add(new float3(0.20f, 0.16f, 0.14f));
        points.Add(new float3(-0.18f, 0.20f, 0.10f));
        points.Add(new float3(0.06f, -0.22f, 0.19f));
        points.Add(new float3(-0.21f, -0.12f, -0.16f));
        points.Add(new float3(0.17f, -0.09f, -0.21f));
        points.Add(new float3(0.02f, 0.24f, -0.12f));
        points.Add(new float3(-0.09f, 0.03f, 0.24f));
        points.Add(new float3(0.24f, 0.02f, -0.03f));
        points.Add(new float3(-0.24f, -0.05f, 0.06f));
        points.Add(new float3(0.05f, -0.18f, -0.05f));
    }

    // The hull mesh and the hull collider are fed the SAME point cloud, so what you grab is the shape
    // you see rather than a box drawn around it.
    private static void CreateHullProp(Slot parent, int index)
    {
        var slot = NewMeshStand(parent, "ConvexHull Prop (grabbable)", index);

        var mesh = slot.AttachComponent<ConvexHullMesh>();
        AddHullPoints(mesh.Points);
        mesh.FlatShading.Value = true;

        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.35f, 0.85f, 0.95f, 1f);
        material.Metallic.Value = 0.2f;
        material.Smoothness.Value = 0.55f;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        var collider = slot.AttachComponent<ConvexHullCollider>();
        collider.CharacterCollider.Value = true;
        AddHullPoints(collider.Points);
        collider.Type.Value = ColliderType.Trigger;

        slot.AttachComponent<Grabbable>();
    }

    // INTERACTION
    //
    // Magnets, touch controls, grabbable plumbing, the dev tool, a seat and the blink surfaces. Four
    // bands three metres apart running away from spawn, rather than one long left-to-right row: the row
    // was eleven metres wide and you had to walk its whole length to find out what was on it.
    private static void CreateInteractionShowcase(World world)
    {
        var root = world.RootSlot.AddSlot("Interaction Showcase");
        root.LocalPosition.Value = InteractionOrigin;

        AreaPlate(world, "Interaction", new float2(InteractionOrigin.x, InteractionOrigin.z),
            new float2(12f, 13f), new colorHDR(0.16f, 0.24f, 0.19f, 1f));
        Sign(root, "Interaction\nmagnets, buttons, grabbing, seats, blink", new float3(0f, 3f, -5.6f), 0.26f);

        CreateDevToolStand(root, new float3(-3.4f, 0f, -4.5f));
        CreateToolRack(root);
        CreateMagnetBench(root);
        CreateTouchBench(root);
        CreateGrabbableBench(root);
        CreateSeatBench(root);
        CreateBlinkPlatforms(root);
    }

    // A grabbable dev tool on a post: grip it with the hand and the dev actions come alive (the radial
    // menu shows Inspector while it is equipped).
    private static void CreateDevToolStand(Slot parent, float3 localPosition)
    {
        var toolSlot = parent.AddSlot("Dev Tool");
        toolSlot.LocalPosition.Value = localPosition + new float3(0f, 1.05f, 0f);

        // DevToolItem builds its own cone visual - adding one here doubled it.
        var collider = toolSlot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = new float3(0.12f, 0.26f, 0.12f);

        toolSlot.AttachComponent<Grabbable>();
        toolSlot.AttachComponent<DevToolItem>();

        Post(parent, new float2(localPosition.x, localPosition.z), 0.9f, 0.06f);
        Sign(parent, "Dev tool (DevToolItem)\ngrab it to unlock the dev menu",
            localPosition + new float3(0f, 1.7f, 0f), 0.09f);
    }

    // The equippable tools, one per notch on a shelf down the right-hand edge of the area.
    //
    // Running along Z rather than across X on purpose: the two benches in the middle of this area
    // already reach out to x=2.4 either side, and a rack laid across the front of them would have you
    // walking through the grabbable demo to reach the meter. A column beside them is reachable from
    // the aisle without crossing anything.
    //
    // Each one is an ordinary grabbable prop with a tool component on it, which is the whole equip
    // story: grip it, or click it and take the "Equip" confirm. The tools build their own visuals in
    // OnStart, so all this has to supply is a body to hit and a name to read. -xlinka
    private static void CreateToolRack(Slot parent)
    {
        var bench = parent.AddSlot("Tools");
        bench.LocalPosition.Value = new float3(4.6f, 0f, 0f);

        Sign(bench, "Tools\ngrab one, then point and click", new float3(0f, 2.2f, 0f), 0.11f);

        const float shelfHeight = 1.0f;
        const float spacing = 0.5f;
        const float length = spacing * 7f;

        var shelf = bench.AddSlot("Shelf");
        shelf.LocalPosition.Value = new float3(0f, shelfHeight, 0f);
        var shelfMesh = shelf.AttachComponent<BoxMesh>();
        shelfMesh.Size.Value = new float3(0.4f, 0.07f, length);
        var shelfMat = shelf.AttachComponent<PBS_Metallic>();
        shelfMat.AlbedoColor.Value = new colorHDR(0.14f, 0.16f, 0.19f, 1f);
        shelfMat.Metallic.Value = 0.3f;
        shelfMat.Smoothness.Value = 0.35f;
        var shelfRenderer = shelf.AttachComponent<MeshRenderer>();
        shelfRenderer.Mesh.Target = shelfMesh;
        shelfRenderer.Material.Target = shelfMat;
        var shelfCollider = shelf.AttachComponent<BoxCollider>();
        shelfCollider.CharacterCollider.Value = true;
        shelfCollider.Type.Value = ColliderType.Static;
        shelfCollider.Size.Value = shelfMesh.Size.Value;

        Post(bench, new float2(0f, -length * 0.5f + 0.2f), shelfHeight - 0.035f, 0.05f);
        Post(bench, new float2(0f, length * 0.5f - 0.2f), shelfHeight - 0.035f, 0.05f);

        float z = -length * 0.5f + spacing * 0.5f;
        RackTool<ShapeTool>(bench, "Shape Tool", "Shape (ShapeTool)\nplaces a primitive on the surface", new float3(0f, shelfHeight + 0.16f, z));
        z += spacing;
        RackTool<LightTool>(bench, "Light Tool", "Light (LightTool)\nhold after the click to size it", new float3(0f, shelfHeight + 0.16f, z));
        z += spacing;
        RackTool<MaterialTool>(bench, "Material Tool", "Material (MaterialTool)\nsecondary loads a material, primary paints", new float3(0f, shelfHeight + 0.16f, z));
        z += spacing;
        RackTool<GlueTool>(bench, "Glue Tool", "Glue (GlueTool)\nclick A, then click B", new float3(0f, shelfHeight + 0.16f, z));
        z += spacing;
        RackTool<DuplicatorTool>(bench, "Duplicator", "Duplicator (DuplicatorTool)\ncopies what you point at", new float3(0f, shelfHeight + 0.16f, z));
        z += spacing;
        RackTool<MeterTool>(bench, "Meter Tool", "Meter (MeterTool)\ntwo clicks, one distance", new float3(0f, shelfHeight + 0.16f, z));
        z += spacing;
        RackTool<BrushTool>(bench, "Brush", "Brush (BrushTool)\nhold and draw; the ring picks the tip", new float3(0f, shelfHeight + 0.16f, z));
    }

    // The tool component brings its own Grabbable (ToolItem attaches one on attach) and its own
    // visual, so the body here is just something for the laser to land on.
    private static void RackTool<T>(Slot bench, string name, string caption, float3 localPosition)
        where T : ToolItem, new()
    {
        var slot = bench.AddSlot(name);
        slot.LocalPosition.Value = localPosition;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = new float3(0.1f, 0.12f, 0.1f);

        slot.AttachComponent<T>();

        Sign(bench, caption, localPosition + new float3(0f, 0.38f, 0f), 0.06f);
    }

    private static void CreateMagnetBench(Slot parent)
    {
        var bench = parent.AddSlot("Magnets");
        bench.LocalPosition.Value = new float3(1.4f, 0f, -4.5f);

        Sign(bench, "Magnets\nput a cube on the pad and let go", new float3(0f, 2.2f, 0f), 0.11f);

        var pedestal = bench.AddSlot("Pedestal");
        pedestal.LocalPosition.Value = new float3(0f, 0.45f, 0f);
        var pedestalMesh = pedestal.AttachComponent<CylinderMesh>();
        pedestalMesh.Radius.Value = 0.22f;
        pedestalMesh.Height.Value = 0.9f;
        pedestalMesh.Segments.Value = 24;
        var pedestalMat = pedestal.AttachComponent<PBS_Metallic>();
        pedestalMat.AlbedoColor.Value = new colorHDR(0.30f, 0.32f, 0.38f, 1f);
        pedestalMat.Metallic.Value = 0.4f;
        pedestalMat.Smoothness.Value = 0.4f;
        var pedestalRenderer = pedestal.AttachComponent<MeshRenderer>();
        pedestalRenderer.Mesh.Target = pedestalMesh;
        pedestalRenderer.Material.Target = pedestalMat;
        var pedestalCollider = pedestal.AttachComponent<CylinderCollider>();
        pedestalCollider.CharacterCollider.Value = true;
        pedestalCollider.Type.Value = ColliderType.Static;
        pedestalCollider.Radius.Value = 0.22f;
        pedestalCollider.Height.Value = 0.9f;

        // The socket is a pose, not geometry: whatever it accepts is reparented under this slot and
        // snapped onto it, so the socket sits where the cube should end up.
        var socketSlot = bench.AddSlot("Socket");
        socketSlot.LocalPosition.Value = new float3(0f, 1.0f, 0f);
        var socket = socketSlot.AttachComponent<MagnetSocket>();
        socket.MaxDistance.Value = 0.35f;
        socket.MaxAngle.Value = 180f;
        socket.SettleTime.Value = 0.12f;
        socket.AutoAttach.Value = true;
        socket.TagWhitelist.Add("scratch-magnet");
        socketSlot.AttachComponent<MagnetPoint>();
        Sign(bench, "Magnet socket (MagnetSocket)", new float3(0f, 1.5f, 0f), 0.075f);

        CreateMagnetCube(bench, "Magnet Cube A", new float3(-1.6f, 1.05f, 0f), new colorHDR(0.95f, 0.55f, 0.20f, 1f));
        CreateMagnetCube(bench, "Magnet Cube B", new float3(1.6f, 1.05f, 0f), new colorHDR(0.30f, 0.85f, 0.55f, 1f));
        Sign(bench, "Magnet cube (Magnet)", new float3(-1.6f, 1.45f, 0f), 0.07f);
        Sign(bench, "Magnet cube (Magnet)", new float3(1.6f, 1.45f, 0f), 0.07f);
    }

    private static void CreateMagnetCube(Slot parent, string name, float3 position, colorHDR tint)
    {
        var cube = parent.AddSlot(name);
        cube.LocalPosition.Value = position;

        var mesh = cube.AttachComponent<BoxMesh>();
        mesh.Size.Value = float3.One * 0.22f;
        var material = cube.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = tint;
        material.Metallic.Value = 0.1f;
        material.Smoothness.Value = 0.5f;
        var renderer = cube.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        var collider = cube.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = mesh.Size.Value;
        collider.Type.Value = ColliderType.Trigger;

        cube.AttachComponent<Grabbable>();

        Post(parent, new float2(position.x, position.z), position.y - 0.11f, 0.04f);

        var magnet = cube.AttachComponent<Magnet>();
        magnet.Radius.Value = 0.5f;
        magnet.KeepUpright.Value = true;
        magnet.UseGuides.Value = true;
        magnet.AllowAutoAttach.Value = true;
        magnet.Tags.Add("scratch-magnet");
    }

    // Every control here is wired twice, because a control answers in two different ways.
    //
    // The STATE half is a synced flag or depth read through the value utilities into something you can
    // see - a light, a tint, a scale - and it costs nothing to leave running. The ACTION half is a
    // TouchResponder claiming one of the control's bound delegates, which is what fires once on a
    // press instead of every frame, and what a duplicate or a save actually carries with it. Both
    // chains are the demo as much as the button is. -xlinka
    private static void CreateTouchBench(Slot parent)
    {
        var bench = parent.AddSlot("Touch Controls");
        bench.LocalPosition.Value = new float3(0f, 0f, -1.5f);

        // Every control on this console is built on the slab's -Z face, so the console has to be turned
        // to put that face back toward spawn. Left unrotated you walk up to the area and meet its back.
        var consoleToSpawn = -bench.GlobalPosition;
        consoleToSpawn.y = 0f;
        if (consoleToSpawn.LengthSquared > 1e-6f)
            bench.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-consoleToSpawn.x, -consoleToSpawn.z));

        Sign(bench, "Touch controls\npress them with a hand or the laser", new float3(0f, 2.6f, 0f), 0.11f);

        // One word over each control, because two lines of explanation at this pitch would overlap the
        // control next door. The legend under the slab carries what each one actually changes.
        Sign(bench, "plunger", new float3(-0.5f, 1.42f, -0.06f), 0.035f);
        Sign(bench, "switch", new float3(-0.17f, 1.42f, -0.07f), 0.035f);
        Sign(bench, "flip", new float3(0.17f, 1.42f, -0.07f), 0.035f);
        Sign(bench, "pad", new float3(0.52f, 1.42f, -0.06f), 0.035f);
        Sign(bench,
            "plunger  repaints the readout cube and the bulb\nswitch   lamp brightness while held, power on press\nflip     lamp colour, and its resting brightness\npad      contact depth scales and lifts the cube",
            new float3(0f, 0.58f, -0.06f), 0.045f);

        // Two legs, because a console-sized slab hanging in the air with nothing under it is the other
        // half of the "everything floats" report.
        Post(bench, new float2(-0.62f, 0f), 0.75f, 0.05f);
        Post(bench, new float2(0.62f, 0f), 0.75f, 0.05f);

        var panel = bench.AddSlot("Panel");
        panel.LocalPosition.Value = new float3(0f, 1.1f, 0f);
        var panelMesh = panel.AttachComponent<BoxMesh>();
        panelMesh.Size.Value = new float3(1.5f, 0.7f, 0.08f);
        var panelMat = panel.AttachComponent<PBS_Metallic>();
        panelMat.AlbedoColor.Value = new colorHDR(0.10f, 0.11f, 0.14f, 1f);
        panelMat.Metallic.Value = 0.2f;
        panelMat.Smoothness.Value = 0.35f;
        var panelRenderer = panel.AttachComponent<MeshRenderer>();
        panelRenderer.Mesh.Target = panelMesh;
        panelRenderer.Material.Target = panelMat;
        var panelCollider = panel.AttachComponent<BoxCollider>();
        panelCollider.CharacterCollider.Value = true;
        panelCollider.Type.Value = ColliderType.Static;
        panelCollider.Size.Value = panelMesh.Size.Value;

        // The lamp both controls on the left drive.
        var lampSlot = bench.AddSlot("Lamp");
        lampSlot.LocalPosition.Value = new float3(0f, 2.0f, 0f);
        var lamp = lampSlot.AttachComponent<Light>();
        lamp.Type.Value = LightType.Point;
        lamp.LightColor.Value = new color(0.9f, 0.9f, 1f, 1f);
        lamp.Intensity.Value = 1f;
        lamp.Range.Value = 6f;
        lamp.Shadows.Value = ShadowType.None;

        // A Light has no geometry, so a colour change on it alone is only visible on whatever it
        // happens to be lighting. The bulb gives the plunger's colour cycle a surface of its own.
        var bulbSlot = lampSlot.AddSlot("Bulb");
        var bulbMesh = bulbSlot.AttachComponent<SphereMesh>();
        bulbMesh.Radius.Value = 0.09f;
        bulbMesh.Segments.Value = 20;
        bulbMesh.Rings.Value = 12;
        var bulbMat = bulbSlot.AttachComponent<PBS_Metallic>();
        bulbMat.AlbedoColor.Value = new colorHDR(0.05f, 0.05f, 0.06f, 1f);
        bulbMat.EmissiveColor.Value = new colorHDR(0.90f, 0.90f, 1.00f, 1f);
        var bulbRenderer = bulbSlot.AttachComponent<MeshRenderer>();
        bulbRenderer.Mesh.Target = bulbMesh;
        bulbRenderer.Material.Target = bulbMat;

        // Plunger: depth 0..Depth remapped to 0..1, fed into a gradient that repaints the readout
        // cube. Pressing it walks the cube through the ramp instead of flipping it.
        var plungerSlot = panel.AddSlot("Plunger Button");
        plungerSlot.LocalPosition.Value = new float3(-0.5f, 0.14f, -0.05f);
        var plungerCollider = plungerSlot.AttachComponent<BoxCollider>();
        plungerCollider.CharacterCollider.Value = true;
        plungerCollider.Size.Value = new float3(0.2f, 0.2f, 0.1f);
        plungerCollider.Type.Value = ColliderType.Trigger;
        var cap = plungerSlot.AddSlot("Plunger");
        var capMesh = cap.AttachComponent<CylinderMesh>();
        capMesh.Radius.Value = 0.08f;
        capMesh.Height.Value = 0.06f;
        capMesh.Segments.Value = 20;
        var capMat = cap.AttachComponent<PBS_Metallic>();
        capMat.AlbedoColor.Value = new colorHDR(0.85f, 0.25f, 0.30f, 1f);
        var capRenderer = cap.AttachComponent<MeshRenderer>();
        capRenderer.Mesh.Target = capMesh;
        capRenderer.Material.Target = capMat;
        cap.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, MathF.PI * 0.5f);
        var plunger = plungerSlot.AttachComponent<PlungerButton>();
        plunger.PressAxis.Value = float3.Forward;
        plunger.Depth.Value = 0.05f;
        plunger.Plunger.Target = cap;

        var readout = bench.AddSlot("Plunger Readout");
        readout.LocalPosition.Value = new float3(-0.5f, 1.65f, 0f);
        var readoutMesh = readout.AttachComponent<BoxMesh>();
        readoutMesh.Size.Value = new float3(0.22f, 0.22f, 0.22f);
        var readoutMat = readout.AttachComponent<PBS_Metallic>();
        readoutMat.AlbedoColor.Value = new colorHDR(0.2f, 0.2f, 0.2f, 1f);
        readoutMat.Metallic.Value = 0.1f;
        readoutMat.Smoothness.Value = 0.6f;
        var readoutRenderer = readout.AttachComponent<MeshRenderer>();
        readoutRenderer.Mesh.Target = readoutMesh;
        readoutRenderer.Material.Target = readoutMat;

        var gradient = readout.AttachComponent<ValueGradient<colorHDR>>();
        gradient.Interpolate.Value = true;
        gradient.AddStop(0f, new colorHDR(0.08f, 0.10f, 0.30f, 1f));
        gradient.AddStop(0.5f, new colorHDR(0.95f, 0.75f, 0.15f, 1f));
        gradient.AddStop(1f, new colorHDR(1.00f, 0.20f, 0.25f, 1f));
        gradient.Target.DriveTarget(readoutMat.AlbedoColor);

        var depthMap = readout.AttachComponent<RangeMap1D>();
        depthMap.Source.Target = plunger.CurrentDepth;
        depthMap.SourceMin.Value = 0f;
        depthMap.SourceMax.Value = plunger.Depth.Value;
        depthMap.Clamp.Value = true;
        depthMap.TargetMin.Value = 0f;
        depthMap.TargetMax.Value = 1f;
        depthMap.Target.DriveTarget(gradient.Progress);

        // The readout above tracks how far the cap has sunk. This is the other half: one shot per
        // press, off the plunger's Pressed delegate. The responder claims that delegate itself on the
        // authority, so nothing here has to hand it a handler.
        var plungerColor = plungerSlot.AttachComponent<TouchCycleValue<colorHDR>>();
        plungerColor.Response.Value = TouchResponse.Pressed;
        plungerColor.TargetValue.Target = bulbMat.EmissiveColor;
        plungerColor.Values.Add(new colorHDR(0.90f, 0.90f, 1.00f, 1f));
        plungerColor.Values.Add(new colorHDR(1.00f, 0.45f, 0.25f, 1f));
        plungerColor.Values.Add(new colorHDR(0.30f, 1.00f, 0.55f, 1f));
        plungerColor.Values.Add(new colorHDR(0.45f, 0.55f, 1.00f, 1f));

        // Switch: momentary press drives the lamp's brightness.
        var switchSlot = panel.AddSlot("Touch Switch");
        switchSlot.LocalPosition.Value = new float3(-0.17f, 0.14f, -0.06f);
        var switchMesh = switchSlot.AttachComponent<BoxMesh>();
        switchMesh.Size.Value = new float3(0.18f, 0.18f, 0.05f);
        var switchMat = switchSlot.AttachComponent<PBS_Metallic>();
        switchMat.AlbedoColor.Value = new colorHDR(0.25f, 0.55f, 0.85f, 1f);
        var switchRenderer = switchSlot.AttachComponent<MeshRenderer>();
        switchRenderer.Mesh.Target = switchMesh;
        switchRenderer.Material.Target = switchMat;
        var switchCollider = switchSlot.AttachComponent<BoxCollider>();
        switchCollider.CharacterCollider.Value = true;
        switchCollider.Size.Value = switchMesh.Size.Value;
        switchCollider.Type.Value = ColliderType.Trigger;
        var touchSwitch = switchSlot.AttachComponent<TouchSwitch>();

        var brightness = switchSlot.AttachComponent<BoolToValue<float>>();
        brightness.TrueValue.Value = 4f;
        brightness.FalseValue.Value = 1f;
        brightness.Target.DriveTarget(lamp.Intensity);
        var brightnessCopy = switchSlot.AttachComponent<CopyValue<bool>>();
        brightnessCopy.Source.Target = touchSwitch.IsPressed;
        brightnessCopy.Target.DriveTarget(brightness.State);

        // The brightness chain above is momentary and follows the flag. The press itself latches the
        // whole light on and off.
        var switchPower = switchSlot.AttachComponent<TouchToggleBool>();
        switchPower.Response.Value = TouchResponse.Pressed;
        switchPower.TargetValue.Target = lamp.Enabled;

        // Flip: latching toggle, drives the lamp's colour.
        var flipSlot = panel.AddSlot("Touch Flip");
        flipSlot.LocalPosition.Value = new float3(0.17f, 0.14f, -0.06f);
        var flipMesh = flipSlot.AttachComponent<BoxMesh>();
        flipMesh.Size.Value = new float3(0.18f, 0.18f, 0.05f);
        var flipMat = flipSlot.AttachComponent<PBS_Metallic>();
        flipMat.AlbedoColor.Value = new colorHDR(0.85f, 0.55f, 0.20f, 1f);
        var flipRenderer = flipSlot.AttachComponent<MeshRenderer>();
        flipRenderer.Mesh.Target = flipMesh;
        flipRenderer.Material.Target = flipMat;
        var flipCollider = flipSlot.AttachComponent<BoxCollider>();
        flipCollider.CharacterCollider.Value = true;
        flipCollider.Size.Value = flipMesh.Size.Value;
        flipCollider.Type.Value = ColliderType.Trigger;
        var flip = flipSlot.AttachComponent<TouchFlip>();

        var lampColor = flipSlot.AttachComponent<BoolToValue<color>>();
        lampColor.TrueValue.Value = new color(0.35f, 1.0f, 0.65f, 1f);
        lampColor.FalseValue.Value = new color(1.0f, 0.82f, 0.60f, 1f);
        lampColor.Target.DriveTarget(lamp.LightColor);
        var flipCopy = flipSlot.AttachComponent<CopyValue<bool>>();
        flipCopy.Source.Target = flip.State;
        flipCopy.Target.DriveTarget(lampColor.State);

        // Stepping lamp.Intensity directly would be pointless: the switch's BoolToValue holds that
        // field's drive link and would write over the step on the very next frame. The step lands on
        // the value that drive FEEDS instead, so the flip really does move the lamp's resting
        // brightness and the momentary punch on top of it still works. -xlinka
        var flipBrightness = flipSlot.AttachComponent<TouchStepValue<float>>();
        flipBrightness.Response.Value = TouchResponse.Pressed;
        flipBrightness.TargetValue.Target = brightness.FalseValue;
        flipBrightness.Delta.Value = 0.75f;
        flipBrightness.Min.Value = 0.5f;
        flipBrightness.Max.Value = 3.5f;
        flipBrightness.UseRange.Value = true;
        flipBrightness.Wrap.Value = true;

        // Pad: reports how deep the contact is, mapped straight onto a cube's scale.
        var padSlot = panel.AddSlot("Touch Pad");
        padSlot.LocalPosition.Value = new float3(0.52f, 0.14f, -0.05f);
        var padMesh = padSlot.AttachComponent<BoxMesh>();
        padMesh.Size.Value = new float3(0.32f, 0.24f, 0.04f);
        var padMat = padSlot.AttachComponent<PBS_Metallic>();
        padMat.AlbedoColor.Value = new colorHDR(0.55f, 0.30f, 0.85f, 1f);
        var padRenderer = padSlot.AttachComponent<MeshRenderer>();
        padRenderer.Mesh.Target = padMesh;
        padRenderer.Material.Target = padMat;
        var padCollider = padSlot.AttachComponent<BoxCollider>();
        padCollider.CharacterCollider.Value = true;
        padCollider.Size.Value = padMesh.Size.Value;
        padCollider.Type.Value = ColliderType.Trigger;
        var pad = padSlot.AttachComponent<TouchPad>();

        var padCube = bench.AddSlot("Pad Readout");
        padCube.LocalPosition.Value = new float3(0.52f, 1.65f, 0f);
        var padCubeMesh = padCube.AttachComponent<BoxMesh>();
        padCubeMesh.Size.Value = new float3(0.2f, 0.2f, 0.2f);
        var padCubeMat = padCube.AttachComponent<PBS_Metallic>();
        padCubeMat.AlbedoColor.Value = new colorHDR(0.60f, 0.35f, 0.90f, 1f);
        var padCubeRenderer = padCube.AttachComponent<MeshRenderer>();
        padCubeRenderer.Mesh.Target = padCubeMesh;
        padCubeRenderer.Material.Target = padCubeMat;

        var padMap = padCube.AttachComponent<RangeMap3D>();
        padMap.Source.Target = pad.ContactDepth;
        padMap.SourceMin.Value = 0f;
        padMap.SourceMax.Value = 0.04f;
        padMap.Clamp.Value = true;
        padMap.TargetMin.Value = new float3(0.6f, 0.6f, 0.6f);
        padMap.TargetMax.Value = new float3(1.8f, 1.8f, 1.8f);
        padMap.Target.DriveTarget(padCube.LocalScale);

        // Two responders on one control, separated only by which response they ask for: contact lifts
        // the cube, letting go drops it. They share a slot and a source and still never cross-fire,
        // because the fan-out only reaches responders wanting the same response as the one that fired.
        var padLift = padSlot.AttachComponent<TouchSetValue<float3>>();
        padLift.Response.Value = TouchResponse.Pressed;
        padLift.TargetValue.Target = padCube.LocalPosition;
        padLift.Value.Value = new float3(0.52f, 1.95f, 0f);

        var padDrop = padSlot.AttachComponent<TouchSetValue<float3>>();
        padDrop.Response.Value = TouchResponse.Released;
        padDrop.TargetValue.Target = padCube.LocalPosition;
        padDrop.Value.Value = new float3(0.52f, 1.65f, 0f);
    }

    private static void CreateGrabbableBench(Slot parent)
    {
        var bench = parent.AddSlot("Grabbables");
        bench.LocalPosition.Value = new float3(0f, 0f, 1.5f);

        Sign(bench, "Grabbing\nfour things that happen when you let go", new float3(0f, 2.4f, 0f), 0.11f);

        // The dispenser hands out copies of an inactive template, so the template itself never shows
        // up in the world and never gets grabbed by mistake.
        var templateRoot = bench.AddSlot("Templates");
        var template = templateRoot.AddSlot("Dispensed Cube");
        template.ActiveSelf.Value = false;
        var templateMesh = template.AttachComponent<BoxMesh>();
        templateMesh.Size.Value = float3.One * 0.18f;
        var templateMat = template.AttachComponent<PBS_Metallic>();
        templateMat.AlbedoColor.Value = new colorHDR(0.95f, 0.85f, 0.35f, 1f);
        var templateRenderer = template.AttachComponent<MeshRenderer>();
        templateRenderer.Mesh.Target = templateMesh;
        templateRenderer.Material.Target = templateMat;
        var templateCollider = template.AttachComponent<BoxCollider>();
        templateCollider.CharacterCollider.Value = true;
        templateCollider.Size.Value = templateMesh.Size.Value;
        template.AttachComponent<Grabbable>();

        var dispenser = bench.AddSlot("Grab Spawner");
        dispenser.LocalPosition.Value = new float3(-2.4f, 1.05f, 0f);
        Post(bench, new float2(-2.4f, 0f), 0.9f);
        Sign(bench, "Dispenser (GrabSpawner)\ngrab it for a fresh cube", new float3(-2.4f, 1.65f, 0f), 0.08f);
        var dispenserMesh = dispenser.AttachComponent<ConeMesh>();
        dispenserMesh.RadiusBase.Value = 0.22f;
        dispenserMesh.RadiusTop.Value = 0.10f;
        dispenserMesh.Height.Value = 0.30f;
        dispenserMesh.Segments.Value = 22;
        var dispenserMat = dispenser.AttachComponent<PBS_Metallic>();
        dispenserMat.AlbedoColor.Value = new colorHDR(0.25f, 0.65f, 0.75f, 1f);
        var dispenserRenderer = dispenser.AttachComponent<MeshRenderer>();
        dispenserRenderer.Mesh.Target = dispenserMesh;
        dispenserRenderer.Material.Target = dispenserMat;
        var dispenserCollider = dispenser.AttachComponent<BoxCollider>();
        dispenserCollider.CharacterCollider.Value = true;
        dispenserCollider.Size.Value = new float3(0.4f, 0.3f, 0.4f);
        dispenserCollider.Type.Value = ColliderType.Trigger;
        var spawner = dispenser.AttachComponent<GrabSpawner>();
        spawner.Template.Target = template;
        spawner.SpawnParent.Target = bench;
        spawner.MaxInstances.Value = 8;
        spawner.ActivateInstance.Value = true;
        spawner.EnableGrabbable.Value = true;
        spawner.SpawnAtHand.Value = true;
        spawner.DestroyOnReturn.Value = true;
        spawner.ReturnRadius.Value = 0.4f;

        // Shelf: anything released while its GrabParenter fires lands under the shelf slot.
        var shelf = bench.AddSlot("Grab Parenter Shelf");
        shelf.LocalPosition.Value = new float3(-0.8f, 0.9f, 0f);
        Post(bench, new float2(-0.8f, 0f), 0.87f);
        Sign(bench, "Shelf (GrabParenter)\ndrop the capsule to re-home it", new float3(-0.8f, 1.6f, 0f), 0.08f);
        var shelfMesh = shelf.AttachComponent<BoxMesh>();
        shelfMesh.Size.Value = new float3(0.6f, 0.06f, 0.4f);
        var shelfMat = shelf.AttachComponent<PBS_Metallic>();
        shelfMat.AlbedoColor.Value = new colorHDR(0.45f, 0.42f, 0.38f, 1f);
        var shelfRenderer = shelf.AttachComponent<MeshRenderer>();
        shelfRenderer.Mesh.Target = shelfMesh;
        shelfRenderer.Material.Target = shelfMat;
        var shelfCollider = shelf.AttachComponent<BoxCollider>();
        shelfCollider.CharacterCollider.Value = true;
        shelfCollider.Type.Value = ColliderType.Static;
        shelfCollider.Size.Value = shelfMesh.Size.Value;

        var shelfItem = bench.AddSlot("Shelf Item");
        shelfItem.LocalPosition.Value = new float3(-0.8f, 1.11f, 0f);
        var shelfItemMesh = shelfItem.AttachComponent<CapsuleMesh>();
        shelfItemMesh.Radius.Value = 0.07f;
        shelfItemMesh.Height.Value = 0.22f;
        shelfItemMesh.Segments.Value = 18;
        var shelfItemMat = shelfItem.AttachComponent<PBS_Metallic>();
        shelfItemMat.AlbedoColor.Value = new colorHDR(0.85f, 0.35f, 0.55f, 1f);
        var shelfItemRenderer = shelfItem.AttachComponent<MeshRenderer>();
        shelfItemRenderer.Mesh.Target = shelfItemMesh;
        shelfItemRenderer.Material.Target = shelfItemMat;
        var shelfItemCollider = shelfItem.AttachComponent<CapsuleCollider>();
        shelfItemCollider.CharacterCollider.Value = true;
        shelfItemCollider.Type.Value = ColliderType.Trigger;
        shelfItemCollider.Radius.Value = 0.07f;
        shelfItemCollider.Height.Value = 0.22f;
        shelfItem.AttachComponent<Grabbable>();
        var parenter = shelfItem.AttachComponent<GrabParenter>();
        parenter.NewParent.Target = shelf;
        parenter.KeepGlobalTransform.Value = true;

        // GrabCheck only reports. Its bools are the honest place to hang anything that wants to know
        // whether a thing is in someone's hand.
        var checkItem = bench.AddSlot("Grab Check Item");
        checkItem.LocalPosition.Value = new float3(0.8f, 1.05f, 0f);
        Post(bench, new float2(0.8f, 0f), 0.92f);
        Sign(bench, "Held-or-not readout (GrabCheck)", new float3(0.8f, 1.55f, 0f), 0.08f);
        var checkMesh = checkItem.AttachComponent<IcoSphereMesh>();
        checkMesh.Radius.Value = 0.13f;
        checkMesh.Subdivisions.Value = 2;
        var checkMat = checkItem.AttachComponent<PBS_Metallic>();
        checkMat.AlbedoColor.Value = new colorHDR(0.40f, 0.90f, 0.60f, 1f);
        var checkRenderer = checkItem.AttachComponent<MeshRenderer>();
        checkRenderer.Mesh.Target = checkMesh;
        checkRenderer.Material.Target = checkMat;
        var checkCollider = checkItem.AttachComponent<SphereCollider>();
        checkCollider.CharacterCollider.Value = true;
        checkCollider.Radius.Value = 0.13f;
        checkCollider.Type.Value = ColliderType.Trigger;
        checkItem.AttachComponent<Grabbable>();
        checkItem.AttachComponent<GrabCheck>();

        // Snap-home item: let go of it anywhere and it glides back to the pose captured at start.
        var resetItem = bench.AddSlot("Snap Home Item");
        resetItem.LocalPosition.Value = new float3(2.4f, 1.05f, 0f);
        Post(bench, new float2(2.4f, 0f), 0.94f);
        Sign(bench, "Snap-home block (GrabTransformReset)\nlet go of it anywhere and it comes back", new float3(2.4f, 1.6f, 0f), 0.08f);
        var resetMesh = resetItem.AttachComponent<BevelBoxMesh>();
        resetMesh.Size.Value = float3.One * 0.22f;
        resetMesh.Bevel.Value = 0.04f;
        resetMesh.BevelSegments.Value = 3;
        var resetMat = resetItem.AttachComponent<PBS_Metallic>();
        resetMat.AlbedoColor.Value = new colorHDR(0.95f, 0.45f, 0.25f, 1f);
        var resetRenderer = resetItem.AttachComponent<MeshRenderer>();
        resetRenderer.Mesh.Target = resetMesh;
        resetRenderer.Material.Target = resetMat;
        var resetCollider = resetItem.AttachComponent<BoxCollider>();
        resetCollider.CharacterCollider.Value = true;
        resetCollider.Size.Value = float3.One * 0.22f;
        resetCollider.Type.Value = ColliderType.Trigger;
        resetItem.AttachComponent<Grabbable>();
        var reset = resetItem.AttachComponent<GrabTransformReset>();
        reset.CaptureOnStart.Value = true;
        reset.SmoothTime.Value = 0.25f;
        reset.ResetPosition.Value = true;
        reset.ResetRotation.Value = true;
        reset.ResetScale.Value = false;
    }

    private static void CreateSeatBench(Slot parent)
    {
        var bench = parent.AddSlot("Seating");
        bench.LocalPosition.Value = new float3(-3.6f, 0f, 4.5f);

        Sign(bench, "Seat\njump to stand up", new float3(0f, 1.6f, 0f), 0.11f);

        var seatSlot = bench.AddSlot("Bench");
        seatSlot.LocalPosition.Value = new float3(0f, 0.45f, 0f);
        var seatMesh = seatSlot.AttachComponent<BoxMesh>();
        seatMesh.Size.Value = new float3(1.2f, 0.12f, 0.5f);
        var seatMat = seatSlot.AttachComponent<PBS_Metallic>();
        seatMat.AlbedoColor.Value = new colorHDR(0.55f, 0.40f, 0.28f, 1f);
        seatMat.Metallic.Value = 0f;
        seatMat.Smoothness.Value = 0.3f;
        var seatRenderer = seatSlot.AttachComponent<MeshRenderer>();
        seatRenderer.Mesh.Target = seatMesh;
        seatRenderer.Material.Target = seatMat;
        var seatCollider = seatSlot.AttachComponent<BoxCollider>();
        seatCollider.CharacterCollider.Value = true;
        seatCollider.Type.Value = ColliderType.Static;
        seatCollider.Size.Value = seatMesh.Size.Value;

        // Legs, so the plank is a bench rather than a plank hanging in mid air.
        Post(bench, new float2(-0.5f, 0f), 0.39f, 0.045f);
        Post(bench, new float2(0.5f, 0f), 0.39f, 0.045f);

        var seat = seatSlot.AttachComponent<Seat>();
        seat.MinScale.Value = 0.5f;
        seat.MaxScale.Value = 2f;
        seat.PreserveUpOnExit.Value = true;

        // The laser trigger is what makes the bench clickable from across the room; the seat itself
        // has no pointer surface of its own.
        var trigger = seatSlot.AttachComponent<SeatLaserTrigger>();
        trigger.TargetSeat.Target = seat;
        trigger.AllowSit.Value = true;
        trigger.AllowRelease.Value = true;
    }

    // Blink teleport reads these tags off whatever the arc lands on: a LandingSurface is an explicit
    // yes, a LandingBlock is an explicit no over the same ground.
    private static void CreateBlinkPlatforms(Slot parent)
    {
        var bench = parent.AddSlot("Blink Platforms");
        bench.LocalPosition.Value = new float3(1.6f, 0f, 4.5f);

        Sign(bench, "Blink teleport\naim the arc at a pad and let go", new float3(1.6f, 3.1f, 0f), 0.11f);

        for (int i = 0; i < 3; i++)
        {
            float x = i * 1.6f;
            float y = 0.5f + i * 0.55f;
            var platform = bench.AddSlot($"Landing Surface {i}");
            platform.LocalPosition.Value = new float3(x, y, 0f);
            Post(bench, new float2(x, 0f), y - 0.06f, 0.07f);
            Sign(bench, "Blink landing pad\n(LandingSurface)", new float3(x, y + 0.45f, 0f), 0.075f);
            var mesh = platform.AttachComponent<CylinderMesh>();
            mesh.Radius.Value = 0.55f;
            mesh.Height.Value = 0.12f;
            mesh.Segments.Value = 26;
            var material = platform.AttachComponent<PBS_Metallic>();
            material.AlbedoColor.Value = new colorHDR(0.25f, 0.60f, 0.90f, 1f);
            material.Metallic.Value = 0.1f;
            material.Smoothness.Value = 0.5f;
            var renderer = platform.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            renderer.Material.Target = material;
            var collider = platform.AttachComponent<CylinderCollider>();
            collider.CharacterCollider.Value = true;
            collider.Type.Value = ColliderType.Static;
            collider.Radius.Value = 0.55f;
            collider.Height.Value = 0.12f;
            platform.AttachComponent<LandingSurface>();
        }

        var blocked = bench.AddSlot("Landing Block Zone");
        blocked.LocalPosition.Value = new float3(-1.6f, 0.5f, 0f);
        Post(bench, new float2(-1.6f, 0f), 0.44f, 0.07f);
        Sign(bench, "No-blink zone (LandingBlock)\nthe arc refuses to land here", new float3(-1.6f, 0.95f, 0f), 0.075f);
        var blockedMesh = blocked.AttachComponent<CylinderMesh>();
        blockedMesh.Radius.Value = 0.55f;
        blockedMesh.Height.Value = 0.12f;
        blockedMesh.Segments.Value = 26;
        var blockedMat = blocked.AttachComponent<PBS_Metallic>();
        blockedMat.AlbedoColor.Value = new colorHDR(0.85f, 0.20f, 0.20f, 1f);
        var blockedRenderer = blocked.AttachComponent<MeshRenderer>();
        blockedRenderer.Mesh.Target = blockedMesh;
        blockedRenderer.Material.Target = blockedMat;
        var blockedCollider = blocked.AttachComponent<CylinderCollider>();
        blockedCollider.CharacterCollider.Value = true;
        blockedCollider.Type.Value = ColliderType.Static;
        blockedCollider.Radius.Value = 0.55f;
        blockedCollider.Height.Value = 0.12f;
        blocked.AttachComponent<LandingBlock>();
    }

    // VARIABLES AND UTILITY
    //
    // The scope binds by NAME: nothing here holds a reference to the variable, the two signs just ask
    // for "Accent" and get whatever the scope is carrying. Change the colour on the ValueVariable and
    // both signs follow. -xlinka
    private static void CreateVariableShowcase(World world)
    {
        var root = world.RootSlot.AddSlot("Variables And Utility");
        root.LocalPosition.Value = VariablesOrigin;

        AreaPlate(world, "Variables and utility", new float2(VariablesOrigin.x, VariablesOrigin.z),
            new float2(9.6f, 13f), new colorHDR(0.25f, 0.19f, 0.19f, 1f));
        Sign(root, "Variables and utility\none named value, lots of readers", new float3(0f, 3f, -5.6f), 0.26f);

        var font = SharedFont(world);

        var scope = root.AttachComponent<VariableScope>();
        scope.ScopeName.Value = "Scratch";

        var accent = root.AttachComponent<ValueVariable<color>>();
        accent.VariableName.Value = "Scratch/Accent";
        accent.Value.Value = new color(0.35f, 0.95f, 0.80f, 1f);

        CreateVariableReader(root, "Reader A", new float3(-3.2f, 1.5f, -4.5f), font);
        CreateVariableReader(root, "Reader B", new float3(-1.6f, 1.5f, -4.5f), font);
        Sign(root, "reads the Accent variable\n(ValueVariableDriver)", new float3(-3.2f, 1.15f, -4.5f), 0.075f);
        Sign(root, "reads the same Accent variable", new float3(-1.6f, 1.15f, -4.5f), 0.075f);
        Sign(root, "Accent lives here (ValueVariable)\nchange it and both readers follow",
            new float3(-2.4f, 2.1f, -4.5f), 0.085f);

        // Rotator drives rotation, Oscillator3D drives position, and both are drives rather than
        // per-frame writes so a save keeps the wiring rather than the pose.
        var spinner = root.AddSlot("Rotator");
        spinner.LocalPosition.Value = new float3(-1.6f, 1.2f, -1.5f);
        Post(root, new float2(-1.6f, -1.5f), 0.9f);
        Sign(root, "Spinning ring (Rotator)", new float3(-1.6f, 1.7f, -1.5f), 0.08f);
        var spinnerMesh = spinner.AttachComponent<TorusMesh>();
        spinnerMesh.MajorRadius.Value = 0.24f;
        spinnerMesh.MinorRadius.Value = 0.07f;
        spinnerMesh.MajorSegments.Value = 30;
        spinnerMesh.MinorSegments.Value = 12;
        var spinnerMat = spinner.AttachComponent<PBS_Metallic>();
        spinnerMat.AlbedoColor.Value = new colorHDR(0.90f, 0.60f, 0.20f, 1f);
        spinnerMat.Metallic.Value = 0.6f;
        spinnerMat.Smoothness.Value = 0.7f;
        var spinnerRenderer = spinner.AttachComponent<MeshRenderer>();
        spinnerRenderer.Mesh.Target = spinnerMesh;
        spinnerRenderer.Material.Target = spinnerMat;
        var rotator = spinner.AttachComponent<Rotator>();
        rotator.Axis.Value = new float3(0.2f, 1f, 0.1f);
        rotator.Speed.Value = 60f;
        rotator.Rotation.DriveTarget(spinner.LocalRotation);

        var bobber = root.AddSlot("Oscillator");
        bobber.LocalPosition.Value = new float3(0f, 1.2f, -1.5f);
        Sign(root, "Bobbing capsule (Oscillator3D)", new float3(0f, 2.2f, -1.5f), 0.08f);
        var bobberMesh = bobber.AttachComponent<CapsuleMesh>();
        bobberMesh.Radius.Value = 0.10f;
        bobberMesh.Height.Value = 0.5f;
        bobberMesh.Segments.Value = 20;
        var bobberMat = bobber.AttachComponent<PBS_Metallic>();
        bobberMat.AlbedoColor.Value = new colorHDR(0.30f, 0.70f, 0.95f, 1f);
        var bobberRenderer = bobber.AttachComponent<MeshRenderer>();
        bobberRenderer.Mesh.Target = bobberMesh;
        bobberRenderer.Material.Target = bobberMat;
        var oscillator = bobber.AttachComponent<Oscillator3D>();
        oscillator.Min.Value = new float3(0f, 0.9f, -1.5f);
        oscillator.Max.Value = new float3(0f, 1.9f, -1.5f);
        oscillator.Speed.Value = new float3(0f, 1.4f, 0f);
        oscillator.Target.DriveTarget(bobber.LocalPosition);

        // TimeSine feeds the gradient, the gradient repaints the bar. One chain, two components on
        // the list, and the bar is the thing you can actually see moving.
        var bar = root.AddSlot("Gradient Bar");
        bar.LocalPosition.Value = new float3(1.6f, 1.2f, -1.5f);
        Post(root, new float2(1.6f, -1.5f), 1.12f);
        Sign(root, "Colour bar (TimeSine into ValueGradient)", new float3(1.6f, 1.7f, -1.5f), 0.08f);
        var barMesh = bar.AttachComponent<StripeMesh>();
        barMesh.Width.Value = 0.16f;
        barMesh.Length.Value = 0.9f;
        barMesh.RoundedEnds.Value = true;
        barMesh.CapSegments.Value = 10;
        barMesh.DualSided.Value = true;
        var barMat = bar.AttachComponent<PBS_Metallic>();
        barMat.AlbedoColor.Value = new colorHDR(0.5f, 0.5f, 0.5f, 1f);
        barMat.Metallic.Value = 0f;
        barMat.Smoothness.Value = 0.4f;
        var barRenderer = bar.AttachComponent<MeshRenderer>();
        barRenderer.Mesh.Target = barMesh;
        barRenderer.Material.Target = barMat;

        var barGradient = bar.AttachComponent<ValueGradient<colorHDR>>();
        barGradient.Interpolate.Value = true;
        barGradient.AddStop(0f, new colorHDR(0.10f, 0.20f, 0.85f, 1f));
        barGradient.AddStop(0.33f, new colorHDR(0.15f, 0.90f, 0.60f, 1f));
        barGradient.AddStop(0.66f, new colorHDR(0.95f, 0.85f, 0.20f, 1f));
        barGradient.AddStop(1f, new colorHDR(0.95f, 0.20f, 0.35f, 1f));
        barGradient.Target.DriveTarget(barMat.AlbedoColor);

        var sine = bar.AttachComponent<TimeSine>();
        sine.Speed.Value = 1.2f;
        sine.Min.Value = 0f;
        sine.Max.Value = 1f;
        sine.Target.DriveTarget(barGradient.Progress);

        CreateFacingSigns(root, font);
        CreateSpawnerAndPulse(root);
        CreateWiringBench(root, font);
    }

    // The wiring components that have something to show: a scrolling texture, a layout that owns its
    // children's positions, and a label nothing writes to by hand. All three are drives, so the save
    // keeps the wiring and not the frame it was saved on. -xlinka
    private static void CreateWiringBench(Slot parent, FontProvider font)
    {
        // Scrolling belt. The panner drives the material's texture offset, which every material already
        // carries and already pushes to its shader, so a conveyor needs no per-frame texture work.
        var belt = parent.AddSlot("Scrolling Belt");
        belt.LocalPosition.Value = new float3(3.2f, 1.3f, -1.5f);
        FaceSpawn(belt);
        Post(parent, new float2(3.2f, -1.5f), 1.05f);
        Sign(parent, "Scrolling texture (UVPanner)", new float3(3.2f, 1.75f, -1.5f), 0.08f);

        var beltMesh = belt.AttachComponent<QuadMesh>();
        beltMesh.Size.Value = new float2(1f, 0.4f);
        beltMesh.DualSided.Value = true;

        var beltChecker = belt.AttachComponent<CheckerTextureProvider>();
        beltChecker.Width.Value = 128;
        beltChecker.Height.Value = 128;
        beltChecker.CellSize.Value = 16;
        beltChecker.ColorA.Value = new color(0.15f, 0.17f, 0.22f, 1f);
        beltChecker.ColorB.Value = new color(0.35f, 0.85f, 0.75f, 1f);

        var beltMaterial = belt.AttachComponent<UnlitMaterial>();
        beltMaterial.Texture.Target = beltChecker;
        beltMaterial.TextureScale.Value = new float2(3f, 1f);

        var beltRenderer = belt.AttachComponent<MeshRenderer>();
        beltRenderer.Mesh.Target = beltMesh;
        beltRenderer.Material.Target = beltMaterial;
        beltRenderer.ShadowCastMode.Value = ShadowCastMode.Off;

        var panner = belt.AttachComponent<UVPanner>();
        panner.Speed.Value = new float2(-0.25f, 0f);
        panner.Offset.DriveTarget(beltMaterial.TextureOffset);

        // Grid. The children have no positions of their own - the aligner drives them, and it picks up
        // anything dropped in as a child of this slot.
        var grid = parent.AddSlot("Aligned Grid");
        grid.LocalPosition.Value = new float3(3.2f, 1.5f, 0f);
        FaceSpawn(grid);
        Post(parent, new float2(3.2f, 0f), 1.05f);
        Sign(parent, "Grid layout (ObjectGridAligner)\nchildren dropped in here get placed",
            new float3(3.2f, 2.05f, 0f), 0.08f);

        var aligner = grid.AttachComponent<ObjectGridAligner>();
        aligner.ItemsPerRow.Value = 3;
        aligner.CellSize.Value = new float2(0.26f, 0.26f);
        aligner.RowAxis.Value = LayoutAxis.XPositive;
        aligner.ColumnAxis.Value = LayoutAxis.YNegative;
        aligner.RowAlignment.Value = LayoutAlign.Center;
        aligner.ColumnAlignment.Value = LayoutAlign.Center;

        for (int i = 0; i < 6; i++)
        {
            var cell = grid.AddSlot($"Cell {i}");
            var cellMesh = cell.AttachComponent<BoxMesh>();
            cellMesh.Size.Value = float3.One * 0.18f;
            var cellMaterial = cell.AttachComponent<PBS_Metallic>();
            cellMaterial.AlbedoColor.Value = new colorHDR(0.30f + i * 0.10f, 0.55f, 0.85f - i * 0.08f, 1f);
            cellMaterial.Metallic.Value = 0.2f;
            cellMaterial.Smoothness.Value = 0.5f;
            var cellRenderer = cell.AttachComponent<MeshRenderer>();
            cellRenderer.Mesh.Target = cellMesh;
            cellRenderer.Material.Target = cellMaterial;
        }

        // Clock. The driver re-formats on its own interval and only writes when the string actually
        // changed, so the text mesh is not rebuilt every frame.
        var clock = parent.AddSlot("Wall Clock");
        clock.LocalPosition.Value = new float3(3.2f, 1.5f, 1.5f);
        FaceSpawn(clock);
        Post(parent, new float2(3.2f, 1.5f), 1.4f);
        Sign(parent, "Local time (CurrentDateTimeTextDriver)", new float3(3.2f, 1.85f, 1.5f), 0.08f);

        var clockText = clock.AttachComponent<TextRenderer>();
        clockText.Text.Value = "--:--:--";
        clockText.Size.Value = 0.2f;
        clockText.Font.Target = font;
        clockText.Color.Value = new color(0.95f, 0.90f, 0.55f, 1f);
        clockText.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0.9f);
        clockText.OutlineThickness.Value = 1f;

        var clockDriver = clock.AttachComponent<CurrentDateTimeTextDriver>();
        clockDriver.Format.Value = "HH:mm:ss";
        clockDriver.UpdateInterval.Value = 0.25f;
        clockDriver.Target.DriveTarget(clockText.Text);
    }

    // Not built through Sign, because the whole point is that the variable drives this label's colour
    // and that needs the TextRenderer in hand. It still has to be turned to face spawn like every other
    // sign: these two sit behind the origin, and left flat they read mirrored. -xlinka
    private static void CreateVariableReader(Slot parent, string name, float3 position, FontProvider font)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = position;
        FaceSpawn(slot);

        var text = slot.AttachComponent<TextRenderer>();
        text.Text.Value = name;
        text.Size.Value = 0.14f;
        text.Font.Target = font;
        text.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0.9f);
        text.OutlineThickness.Value = 1f;

        var driver = slot.AttachComponent<ValueVariableDriver<color>>();
        driver.VariableName.Value = "Scratch/Accent";
        driver.DefaultValue.Value = new color(0.6f, 0.6f, 0.6f, 1f);
        driver.Target.DriveTarget(text.Color);
    }
    // The two signs that are NOT allowed to face spawn, because turning to look at something is the
    // whole thing they demonstrate. Both get a plain-language caption next to them so nobody reads a
    // sign pointing the wrong way as a bug. -xlinka
    private static void CreateFacingSigns(Slot parent, FontProvider font)
    {
        // FaceUser tracks whoever is nearest; FaceTarget aims at a fixed slot. Two different answers
        // to "point at something", so both get a demo.
        var facingUser = parent.AddSlot("Sign Facing User");
        facingUser.LocalPosition.Value = new float3(-3.2f, 1.6f, 1.5f);
        Post(parent, new float2(-3.2f, 1.5f), 1.5f);
        var userText = facingUser.AttachComponent<TextRenderer>();
        userText.Text.Value = "this sign faces you";
        userText.Size.Value = 0.11f;
        userText.Color.Value = new color(0.95f, 0.95f, 0.75f, 1f);
        userText.Font.Target = font;
        var faceUser = facingUser.AttachComponent<FaceUser>();
        faceUser.Mode.Value = FacingUserMode.NearestUser;
        faceUser.FaceHead.Value = true;
        faceUser.YawOnly.Value = true;
        faceUser.Rotation.DriveTarget(facingUser.LocalRotation);
        Sign(parent, "Turns to watch you (FaceUser)", new float3(-3.2f, 2.05f, 1.5f), 0.075f);

        // The anchor used to be an empty slot, so the second sign appeared to be aiming at nothing.
        // A red ball on a post is the target, visible from anywhere on the plate.
        var anchorBase = parent.AddSlot("Face Target Anchor");
        anchorBase.LocalPosition.Value = new float3(0f, 0f, 1.5f);
        Post(anchorBase, float2.Zero, 1.5f, 0.045f);

        var anchor = anchorBase.AddSlot("Anchor Ball");
        anchor.LocalPosition.Value = new float3(0f, 1.6f, 0f);
        var anchorMesh = anchor.AttachComponent<SphereMesh>();
        anchorMesh.Radius.Value = 0.1f;
        anchorMesh.Segments.Value = 20;
        anchorMesh.Rings.Value = 12;
        var anchorMat = anchor.AttachComponent<UnlitMaterial>();
        anchorMat.TintColor.Value = new colorHDR(1.0f, 0.16f, 0.16f, 1f);
        anchorMat.UseVertexColor.Value = false;
        var anchorRenderer = anchor.AttachComponent<MeshRenderer>();
        anchorRenderer.Mesh.Target = anchorMesh;
        anchorRenderer.Material.Target = anchorMat;
        Sign(anchorBase, "the red anchor", new float3(0f, 1.95f, 0f), 0.075f);

        var facingTarget = parent.AddSlot("Sign Facing Target");
        facingTarget.LocalPosition.Value = new float3(-1.6f, 1.6f, 1.5f);
        Post(parent, new float2(-1.6f, 1.5f), 1.5f);
        var targetText = facingTarget.AttachComponent<TextRenderer>();
        targetText.Text.Value = "this sign faces the red anchor";
        targetText.Size.Value = 0.1f;
        targetText.Color.Value = new color(0.75f, 0.90f, 0.95f, 1f);
        targetText.Font.Target = font;
        var faceTarget = facingTarget.AttachComponent<FaceTarget>();
        faceTarget.Target.Target = anchor;
        faceTarget.Up.Value = float3.Up;
        faceTarget.YawOnly.Value = false;
        faceTarget.Rotation.DriveTarget(facingTarget.LocalRotation);
        Sign(parent, "Aims at a fixed slot (FaceTarget)", new float3(-1.6f, 2.05f, 1.5f), 0.075f);
    }

    private static void CreateSpawnerAndPulse(Slot parent)
    {
        var spawnerSlot = parent.AddSlot("Random Spawner");
        spawnerSlot.LocalPosition.Value = new float3(0f, 1.5f, 4.5f);

        // The beads used to roll off into the distance forever. Now they land in something.
        CreateCatchBasin(parent, new float2(0f, 4.5f), 2.4f, 0.45f);

        var templateRoot = spawnerSlot.AddSlot("Templates");
        var template = templateRoot.AddSlot("Spawned Bead");
        template.ActiveSelf.Value = false;
        var templateMesh = template.AttachComponent<IcoSphereMesh>();
        templateMesh.Radius.Value = 0.09f;
        templateMesh.Subdivisions.Value = 1;
        var templateMat = template.AttachComponent<PBS_Metallic>();
        templateMat.AlbedoColor.Value = new colorHDR(0.95f, 0.55f, 0.85f, 1f);
        var templateRenderer = template.AttachComponent<MeshRenderer>();
        templateRenderer.Mesh.Target = templateMesh;
        templateRenderer.Material.Target = templateMat;
        var templateCollider = template.AttachComponent<SphereCollider>();
        templateCollider.CharacterCollider.Value = true;
        templateCollider.Radius.Value = 0.09f;
        template.AttachComponent<RigidBody>().Mass.Value = 0.4f;
        template.AttachComponent<Grabbable>();

        // Radius stays well inside the basin's inner half-width so every bead drops into the box
        // rather than onto its rim.
        var points = spawnerSlot.AttachComponent<CirclePointGenerator>();
        points.Radius.Value = 0.6f;
        points.Shell.Value = false;

        var spawner = spawnerSlot.AttachComponent<RandomSpawner>();
        spawner.AddTemplate(template);
        spawner.SpawnParent.Target = spawnerSlot;
        spawner.PointGenerator.Target = points;
        spawner.Repeat.Value = true;
        spawner.MinInterval.Value = 3f;
        spawner.MaxInterval.Value = 6f;
        spawner.MaxAlive.Value = 12;

        Sign(parent, "Bead spawner (RandomSpawner)\nspawns beads, keeps the newest 12",
            new float3(0f, 2.3f, 4.5f), 0.09f);

        // The pulse drives a slot's active state, which is the cheapest visible bool in the engine.
        var blinkerSlot = parent.AddSlot("Random Pulse");
        blinkerSlot.LocalPosition.Value = new float3(-3.2f, 1.5f, 4.5f);
        Post(parent, new float2(-3.2f, 4.5f), 1.36f);
        var lampSlot = blinkerSlot.AddSlot("Blinker");
        var lampMesh = lampSlot.AttachComponent<IcoSphereMesh>();
        lampMesh.Radius.Value = 0.14f;
        lampMesh.Subdivisions.Value = 2;
        var lampMat = lampSlot.AttachComponent<UnlitMaterial>();
        lampMat.TintColor.Value = new colorHDR(1.0f, 0.35f, 0.25f, 1f);
        lampMat.UseVertexColor.Value = false;
        var lampRenderer = lampSlot.AttachComponent<MeshRenderer>();
        lampRenderer.Mesh.Target = lampMesh;
        lampRenderer.Material.Target = lampMat;

        var pulse = blinkerSlot.AttachComponent<RandomPulse>();
        pulse.MinInterval.Value = 0.6f;
        pulse.MaxInterval.Value = 2.5f;
        pulse.PulseLength.Value = 0.25f;
        pulse.Target.DriveTarget(lampSlot.ActiveSelf);

        Sign(parent, "Blinker (RandomPulse)\nflashes on its own schedule",
            new float3(-3.2f, 1.95f, 4.5f), 0.08f);
    }

    // An open-topped box under the spawner: a floor slab and four walls, all static colliders, so the
    // beads pile up where you can see them instead of rolling to the edge of the world. Everything is
    // built off the INNER size, and the floor slab's underside sits on y=0 like every other prop here.
    // -xlinka
    private static void CreateCatchBasin(Slot parent, float2 localXZ, float inner, float wallHeight)
    {
        const float wall = 0.08f;

        var basin = parent.AddSlot("Catch Basin");
        basin.LocalPosition.Value = new float3(localXZ.x, 0f, localXZ.y);

        var material = basin.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.30f, 0.33f, 0.40f, 1f);
        material.Metallic.Value = 0.35f;
        material.Smoothness.Value = 0.45f;

        AddBasinBox(basin, material, "Basin Floor",
            new float3(0f, wall * 0.5f, 0f),
            new float3(inner, wall, inner));

        float span = inner + wall * 2f;
        float offset = (inner + wall) * 0.5f;
        float centreY = wall + wallHeight * 0.5f;

        AddBasinBox(basin, material, "Basin Wall -X",
            new float3(-offset, centreY, 0f), new float3(wall, wallHeight, span));
        AddBasinBox(basin, material, "Basin Wall +X",
            new float3(offset, centreY, 0f), new float3(wall, wallHeight, span));
        AddBasinBox(basin, material, "Basin Wall -Z",
            new float3(0f, centreY, -offset), new float3(span, wallHeight, wall));
        AddBasinBox(basin, material, "Basin Wall +Z",
            new float3(0f, centreY, offset), new float3(span, wallHeight, wall));

        Sign(basin, "Catch basin\nthe beads collect in here", new float3(0f, wallHeight + 0.3f, 0f), 0.08f);
    }

    private static void AddBasinBox(Slot parent, MaterialProvider material, string name, float3 position, float3 size)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = position;

        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = size;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        // Eight-centimetre slabs lying on a floor plate that already casts nothing. Their shadow is a
        // smudge under themselves and they are five draws into the sun for it. -xlinka
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Type.Value = ColliderType.Static;
        collider.Size.Value = size;
    }

    // PANELS
    //
    // Real instances of every in-world tool panel, built at world load, each on its own stand two
    // metres from its neighbours and turned to face spawn.
    //
    // Every one of these has a Spawn that poses itself off the LOCAL USER'S HEAD, and at template-build
    // time there is no head to pose off, so each Spawn quietly leaves the panel at the origin. That is
    // why the pose is written again here afterwards instead of trusted to the helper. -xlinka
    // The panels area a panel a frame: each lays itself out the frame after it is made, and made together
    // they stopped one frame for all of them.
    private static IEnumerable<Action> PanelShowcaseSteps(World world, Func<PBS_Metallic> showcaseMaterial)
    {
        Slot root = null!;
        Slot inspectorRoot = null!;
        SceneInspectorPanel? inspector = null;

        yield return () =>
        {
            root = world.RootSlot.AddSlot("Panels");
            root.LocalPosition.Value = PanelsOrigin;

            AreaPlate(world, "Panels", new float2(PanelsOrigin.x, PanelsOrigin.z),
                new float2(9.6f, 7f), new colorHDR(0.19f, 0.19f, 0.27f, 1f));
            Sign(root, "Panels\nthe in-world tools, already open", new float3(0f, 3f, -2.6f), 0.26f);

            CreateUIShowcase(world, PanelsOrigin + new float3(-3f, 0f, -2f));
        };

        yield return () =>
        {
            // The scene inspector is pointed at the interaction area rather than at the world root, so it
            // opens on a subtree with something in it instead of on a list of area names.
            inspectorRoot = world.RootSlot.FindChild("Interaction Showcase") ?? world.RootSlot;
            inspector = SceneInspectorPanel.SpawnBuiltIn(world, inspectorRoot);
            if (inspector != null)
            {
                StandPanel(root, inspector.Slot, "Scene inspector (SceneInspectorPanel)\nbrowsing the interaction area",
                    new float3(-1f, 0f, -2f));
            }
        };

        yield return () =>
        {
            var materialPanel = MaterialInspectorPanel.Spawn(world, showcaseMaterial(), float3.Zero, floatQ.Identity);
            StandPanel(root, materialPanel.Slot, "Material inspector (MaterialInspectorPanel)\nediting the first orb's material",
                new float3(1f, 0f, -2f));
        };

        yield return () =>
        {
            // Straight at the same material's albedo. An empty member path means the whole colour rather
            // than one channel of it, which is what the inspector's own colour rows pass.
            var colorPanel = ColorPickerPanel.Spawn(world, showcaseMaterial().AlbedoColor, "", float3.Zero);
            StandPanel(root, colorPanel.Slot, "Colour picker (ColorPickerPanel)\nsame material, albedo field",
                new float3(3f, 0f, -2f));
        };

        yield return () =>
        {
            if (inspector == null)
                return;
            var selector = ComponentSelectorPanel.Spawn(inspector, inspectorRoot);
            selector.CloseAfterAttach.Value = false;
            StandPanel(root, selector.Slot, "Component browser (ComponentSelectorPanel)\nowned by the inspector next door",
                new float3(-2f, 0f, 1.5f));
        };

        yield return () => CreateAvatarStudioStand(world, root, new float3(2f, 0f, 1.5f));
    }

    // Drop a spawned panel onto a stand: a post, a nameplate, and a pose that actually faces spawn.
    private static void StandPanel(Slot areaRoot, Slot panelSlot, string label, float3 localPosition)
    {
        const float panelHeight = 1.5f;

        var stand = areaRoot.AddSlot("Panel Stand");
        stand.LocalPosition.Value = localPosition;
        Post(stand, float2.Zero, panelHeight - 0.35f, 0.055f);
        Sign(stand, label, new float3(0f, 0.35f, 0f), 0.075f);

        panelSlot.GlobalPosition = stand.GlobalPosition + new float3(0f, panelHeight, 0f);
        var toSpawn = -panelSlot.GlobalPosition;
        toSpawn.y = 0f;
        if (toSpawn.LengthSquared > 1e-6f)
            panelSlot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toSpawn.x, toSpawn.z));

        CullPanelBeyond(panelSlot);
    }

    // AvatarStudio is not a panel you place, it is a standing marker figure that carries its own small
    // control canvas, so it gets attached exactly the way the home screen attaches it: a bare slot with
    // a pose on it. It re-anchors itself to the local user on start by design, which is the whole point
    // of the tool, so the stand here is where it waits rather than where it stays. With no rigged model
    // in the world it comes up in its "import a humanoid model first" state, which is a perfectly
    // honest thing for a showcase to be showing. -xlinka
    private static void CreateAvatarStudioStand(World world, Slot areaRoot, float3 localPosition)
    {
        var marker = areaRoot.AddSlot("Avatar Studio Stand");
        marker.LocalPosition.Value = localPosition;
        Sign(marker, "Avatar studio (AvatarStudio)\nmarkers you drag onto a rigged model", new float3(0f, 2.4f, 0f), 0.08f);

        var slot = world.RootSlot.AddSlot("Avatar Studio");
        slot.GlobalPosition = marker.GlobalPosition;
        var toSpawn = -slot.GlobalPosition;
        toSpawn.y = 0f;
        if (toSpawn.LengthSquared > 1e-6f)
            slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toSpawn.x, toSpawn.z));
        slot.AttachComponent<AvatarStudio>();
    }

    // UI
    //
    // One Helio panel carrying every widget the builder can make. If a widget regresses it regresses
    // here first. -xlinka
    private static void CreateUIShowcase(World world, float3 position)
    {
        var stand = world.RootSlot.AddSlot("UI Showcase Stand");
        stand.LocalPosition.Value = position;
        Post(stand, float2.Zero, 0.83f, 0.055f);
        Sign(stand, "Widget sampler (PanelShell)\nevery Helio control on one panel", new float3(0f, 0.35f, 0f), 0.075f);

        var root = world.RootSlot.AddSlot("UI Showcase");
        root.LocalPosition.Value = position + new float3(0f, 1.55f, 0f);
        root.LocalScale.Value = new float3(0.0016f, 0.0016f, 0.0016f);
        var toSpawn = -root.GlobalPosition;
        toSpawn.y = 0f;
        if (toSpawn.LengthSquared > 1e-6f)
            root.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toSpawn.x, toSpawn.z));

        var font = SharedFont(world);

        CullPanelBeyond(root);

        var rounded = root.AttachComponent<RoundedRectTextureProvider>();
        rounded.Size.Value = 64;
        rounded.Radius.Value = 14;

        var panel = root.AttachComponent<PanelShell>();
        panel.Title.Value = "UI Showcase";
        panel.Size.Value = new float2(780f, 900f);
        panel.HeaderHeight.Value = 44f;
        panel.Padding.Value = 14f;
        panel.Font.Target = font;
        panel.RoundedSprite.Target = rounded;
        panel.BackgroundColor.Value = new color(0.020f, 0.024f, 0.032f, 0.92f);
        panel.HeaderColor.Value = new color(0.105f, 0.130f, 0.165f, 0.98f);

        panel.RebuildContent(ui =>
        {
            ui.Font(font);
            ui.RoundedSprite(rounded);
            var layout = ui.VerticalLayout(8f, 10f);
            layout.ForceExpandHeight.Value = false;
            FillRect(layout.RectTransform!);

            var status = ui.Text("Every Helio widget, one panel.", 15f, new color(0.92f, 0.97f, 1f, 1f));
            status.WordWrap.Value = true;
            status.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(status.RectTransform!);
            SetLayoutHeight(status.RectTransform!, 30f, 34f);

            // Handlers come off a component so a duplicated panel drives its own labels. A closure
            // would not survive the copy.
            var actions = panel.Slot.GetComponent<HelioTestActions>() ?? panel.Slot.AttachComponent<HelioTestActions>();
            actions.Status.Target = status;

            var button = ui.Button("Button", actions.OnLaserPressed, new color(0.16f, 0.30f, 0.44f, 0.96f));
            FillRect(button.RectTransform!);
            SetLayoutHeight(button.RectTransform!, 34f, 36f);

            BuildLabeledRow(ui, "Checkbox", () =>
            {
                var checkbox = ui.Checkbox(true, actions.OnCheckboxChanged, new color(0.78f, 0.82f, 0.88f, 1f));
                CenterRect(checkbox.RectTransform!, new float2(24f, 24f));
            });

            BuildLabeledRow(ui, "Slider", () =>
            {
                var slider = ui.Slider(0.4f, 0f, 1f, actions.OnSliderChanged, new color(0.18f, 0.24f, 0.30f, 0.96f));
                FillRect(slider.RectTransform!);
            });

            BuildLabeledRow(ui, "Text input", () =>
            {
                var input = ui.TextInput("", "type here", null, false, new color(0.10f, 0.12f, 0.16f, 0.95f));
                input.MaxLength.Value = 64;
                FillRect(input.RectTransform!);
            });

            BuildRadioRow(ui);

            BuildLabeledRow(ui, "Progress meter", () =>
            {
                var meter = ui.ProgressMeter(0.65f, new color(0.14f, 0.16f, 0.20f, 0.95f),
                    new color(0.25f, 0.75f, 0.95f, 1f));
                FillRect(meter.RectTransform!);
            });

            var section = ui.CollapsibleSection("Collapsible section (click)", out var sectionContent, true,
                new color(0.052f, 0.060f, 0.074f, 0.92f));
            FillRect(section.RectTransform!);
            SetLayoutHeight(section.RectTransform!, 74f, 78f);
            ui.NestInto(sectionContent);
            var sectionBody = ui.Text("Body text that hides when the header is clicked.", 13f,
                new color(0.78f, 0.84f, 0.92f, 1f));
            sectionBody.WordWrap.Value = true;
            sectionBody.VerticalAlignment.Value = TextVerticalAlignment.Top;
            FillRect(sectionBody.RectTransform!);
            ui.NestOut();

            var listLabel = ui.Text("Scroll list", 14f, new color(0.80f, 0.86f, 0.94f, 1f));
            listLabel.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            FillRect(listLabel.RectTransform!);
            SetLayoutHeight(listLabel.RectTransform!, 22f, 24f);

            var scroll = ui.ScrollRect(out var scrollContent, new float2(1f, 1f), new color(0.045f, 0.060f, 0.080f, 0.90f));
            FillRect(scroll.RectTransform!);
            SetLayoutHeight(scroll.RectTransform!, 220f, 260f, 1f);

            var scrollUi = new UIBuilder(scrollContent.Slot);
            scrollUi.Font(font);
            var scrollLayout = scrollUi.VerticalLayout(4f, 6f);
            scrollLayout.ForceExpandHeight.Value = false;
            FillRect(scrollLayout.RectTransform!);
            for (int i = 1; i <= 30; i++)
            {
                var row = scrollUi.Text($"row {i:00}", 14f, new color(0.90f, 0.94f, 1f, 1f));
                row.VerticalAlignment.Value = TextVerticalAlignment.Middle;
                FillRect(row.RectTransform!);
                SetLayoutHeight(row.RectTransform!, 22f, 24f);
            }
            scrollUi.NestOut();

            ui.NestOut();
        });
    }

    private static void BuildLabeledRow(UIBuilder ui, string label, Action buildControl)
    {
        var row = ui.Panel(new color(0.052f, 0.060f, 0.074f, 0.92f));
        FillRect(row.RectTransform!);
        SetLayoutHeight(row.RectTransform!, 34f, 36f);
        ui.Nest();
        var splits = ui.SplitHorizontally(0.42f, 0.04f, 0.54f);
        ui.NestInto(splits[0]);
        var text = ui.Text(label, 14f, new color(0.94f, 0.96f, 1f, 1f));
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(text.RectTransform!);
        ui.NestOut();
        ui.NestInto(splits[2]);
        buildControl();
        ui.NestOut();
        ui.NestOut();
    }

    // Radios share exclusivity through the group NAME, not through a parent component, so three in a
    // row with the same group is the whole wiring.
    private static void BuildRadioRow(UIBuilder ui)
    {
        var row = ui.Panel(new color(0.052f, 0.060f, 0.074f, 0.92f));
        FillRect(row.RectTransform!);
        SetLayoutHeight(row.RectTransform!, 34f, 36f);
        ui.Nest();
        var splits = ui.SplitHorizontally(0.42f, 0.04f, 0.18f, 0.18f, 0.18f);
        ui.NestInto(splits[0]);
        var label = ui.Text("Radio group", 14f, new color(0.94f, 0.96f, 1f, 1f));
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        FillRect(label.RectTransform!);
        ui.NestOut();
        for (int i = 0; i < 3; i++)
        {
            ui.NestInto(splits[2 + i]);
            var radio = ui.Radio("scratch-showcase", i == 0, null, new color(0.78f, 0.82f, 0.88f, 1f));
            CenterRect(radio.RectTransform!, new float2(24f, 24f));
            ui.NestOut();
        }
        ui.NestOut();
    }

    private static void FillRect(RectTransform rect)
    {
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = float2.Zero;
        rect.OffsetMax.Value = float2.Zero;
    }

    private static void CenterRect(RectTransform rect, float2 size)
    {
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = size * -0.5f;
        rect.OffsetMax.Value = size * 0.5f;
    }

    private static void SetLayoutHeight(RectTransform rect, float minHeight, float preferredHeight, float flexibleHeight = 0f)
    {
        var element = rect.Slot.GetComponent<LayoutElement>() ?? rect.Slot.AttachComponent<LayoutElement>();
        element.MinHeight.Value = minHeight;
        element.PreferredHeight.Value = preferredHeight;
        element.FlexibleHeight.Value = flexibleHeight;
    }

    // PARTICLES
    //
    // One system per emitter shape, plus the two things an emitter cannot show on its own: a sub-emitter
    // chain and a rotation demo. Ten cells, a little over two metres apart, each on its own leg with its
    // name and what it is demonstrating on the sign.
    //
    // Two rules every system here is built around. An emitter COMPONENT retires the system's built-in disc
    // spray, and it only decides where a particle appears and which way it points - speed, lifetime, size
    // and colour are the initializers' job, so every system needs a lifetime and something that gives its
    // particles a velocity. And attaching any module that writes colour or size retires the system's own
    // start/end envelope, which is why the systems carrying a colour module also carry a size module: with
    // the envelope off, whatever nobody writes stays at the neutral default and a particle is born white
    // and a metre across. -xlinka
    private static void CreateParticleShowcase(World world)
    {
        var root = world.RootSlot.AddSlot("Particles");
        root.LocalPosition.Value = ParticlesOrigin;

        // Grown BACKWARDS from the original six-deep slab, not outwards: the front edge is where the
        // area's own label stands and where the walk in from spawn arrives, and everything behind it is
        // bare floor, so the third row costs nothing in plate spacing. -xlinka
        AreaPlate(world, "Particles", new float2(ParticlesOrigin.x, ParticlesOrigin.z - 1.8f),
            new float2(11f, 9.6f), new colorHDR(0.14f, 0.16f, 0.24f, 1f));
        Sign(root, "Particles\none system per emitter shape", new float3(0f, 3.4f, 2.6f), 0.26f);

        CreateSparks(root, new float2(-4.4f, 1.2f));
        CreateSmoke(root, new float2(-2.2f, 1.2f));
        CreateRain(root, new float2(0f, 1.2f));
        CreateRingBurst(root, new float2(2.2f, 1.2f));
        CreateFireflies(root, new float2(4.4f, 1.2f));
        CreateTornado(root, new float2(-4.4f, -1.2f));
        CreateCurtain(root, new float2(-2.2f, -1.2f));
        CreateMeshSpray(root, new float2(0f, -1.2f));
        CreateSubEmitterChain(root, new float2(2.2f, -1.2f));
        CreateRotationDemo(root, new float2(4.4f, -1.2f));

        // Back row: the strand, sheet and light tiers, which are the parts nothing in the front two rows
        // exercises at all. Every one of them is a RENDERER feature - the front rows all draw as plain
        // billboards - so this row is what tells you the hook side is alive. -xlinka
        CreateTrailBurst(root, new float2(-4.4f, -4f));
        CreateRibbonSpiral(root, new float2(-2.2f, -4f));
        CreateFlipbookSheet(root, new float2(0f, -4f));
        CreateEmberLights(root, new float2(2.2f, -4f));
        CreateTrailPendulum(root, new float2(4.4f, -4f));
    }

    // A firework: short-lived sparks thrown in every direction, each one dragging a trail that stays
    // where it was drawn. This is ParticleTrails, so the streak is a HISTORY - the sparks move and the
    // streaks stand still behind them.
    private static void CreateTrailBurst(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Trail Burst",
            "Firework (ParticleTrails)\nevery spark drags a streak that stays put", cell, 1.2f);
        var system = NewSystem(slot, 260, 0f);
        system.EmissionStrength.Value = 2.4f;

        var emitter = slot.AttachComponent<PointEmitter>();
        emitter.System.Target = system;
        emitter.Rate.Value = 45f;
        emitter.RandomDirectionWeight.Value = 1f;

        var speed = AddParticleModule<ParticleSpeedRangeInitializer>(slot, system);
        speed.MinSpeed.Value = 0.6f;
        speed.MaxSpeed.Value = 1.0f;

        // Short. The trail's own MaxPointAge decides how long the streak hangs around, so the spark
        // itself only has to live long enough to draw one.
        var life = AddParticleModule<ParticleLifetimeRangeInitializer>(slot, system);
        life.MinLifetime.Value = 0.35f;
        life.MaxLifetime.Value = 0.55f;

        var force = AddParticleModule<ParticleForce>(slot, system);
        force.Force.Value = new float3(0f, -4f, 0f);

        var tint = AddParticleModule<ParticleColorBySpeed>(slot, system);
        tint.MinSpeed.Value = 0.1f;
        tint.MaxSpeed.Value = 1.2f;
        tint.MinColor.Value = new colorHDR(0.85f, 0.15f, 0.05f, 1f);
        tint.MaxColor.Value = new colorHDR(1.00f, 0.90f, 0.55f, 1f);

        var size = AddParticleModule<ParticleUniformSizeInitializer>(slot, system);
        size.Size.Value = 0.03f;

        var trails = AddParticleModule<ParticleTrails>(slot, system);
        trails.MaxTrails.Value = 96;
        trails.MaxPointsPerTrail.Value = 20;
        trails.MinVertexDistance.Value = 0.015f;
        trails.MaxPointAge.Value = 0.3f;
        trails.WidthScale.Value = 1.4f;
        trails.Smoothing.Value = 4;
        trails.ColorInheritance.Value = SimModules.TrailInheritance.Continuous;
        trails.WidthInheritance.Value = SimModules.TrailInheritance.Birth;
        trails.Alignment.Value = SimStrands.StrandAlignment.CameraFacing;
        // Tapered to nothing at the far end, which is what makes a streak read as a streak instead of a
        // ribbon with a blunt end hanging in the air.
        AddCurveStop(trails.WidthTimes, trails.WidthValues, 0f, 1f);
        AddCurveStop(trails.WidthTimes, trails.WidthValues, 1f, 0f);
        AddStrandColorStop(trails.ColorTimes, trails.ColorValues, 0f, new colorHDR(1f, 1f, 1f, 1f));
        AddStrandColorStop(trails.ColorTimes, trails.ColorValues, 1f, new colorHDR(1f, 0.4f, 0.1f, 0f));
    }

    // A ribbon is NOT a trail: every point on it is a live particle, so the whole band moves. Threading
    // a stream emitted off a spinning arm gives the band a helix to follow, which is the cheapest way to
    // see that it is following the particles and not a recorded path.
    private static void CreateRibbonSpiral(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Ribbon Spiral",
            "Spiral (ParticleRibbons)\nlive particles threaded onto one moving band", cell, 0.9f);
        var system = NewSystem(slot, 220, 0f);
        system.EmissionStrength.Value = 1.8f;

        // The emitter orbits, the system does not. A slot cannot both sit off-centre and spin about the
        // centre, so the spin is one slot and the nozzle hanging off it is another.
        var spin = slot.AddSlot("Spin");
        var rotator = spin.AttachComponent<Rotator>();
        rotator.Axis.Value = float3.Up;
        rotator.Speed.Value = 260f;
        rotator.Rotation.DriveTarget(spin.LocalRotation);

        var nozzle = spin.AddSlot("Nozzle");
        nozzle.LocalPosition.Value = new float3(0.28f, 0f, 0f);

        var emitter = nozzle.AttachComponent<PointEmitter>();
        emitter.System.Target = system;
        emitter.Rate.Value = 70f;

        var speed = AddParticleModule<ParticleSpeedInitializer>(slot, system);
        speed.Speed.Value = 0.42f;

        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 1.4f;

        var gradient = AddParticleModule<ParticleColorGradient>(slot, system);
        AddGradientStop(gradient, 0f, new colorHDR(0.20f, 0.95f, 1.00f, 1f));
        AddGradientStop(gradient, 0.6f, new colorHDR(0.45f, 0.35f, 1.00f, 1f));
        AddGradientStop(gradient, 1f, new colorHDR(0.60f, 0.10f, 0.55f, 0f));

        var size = AddParticleModule<ParticleUniformSizeInitializer>(slot, system);
        size.Size.Value = 0.02f;

        var ribbons = AddParticleModule<ParticleRibbons>(slot, system);
        ribbons.MaxRibbons.Value = 4;
        ribbons.MaxRibbonLength.Value = 56;
        ribbons.Smoothing.Value = 5;
        ribbons.WidthScale.Value = 3.5f;
        ribbons.Alignment.Value = SimStrands.StrandAlignment.CameraFacing;
        AddCurveStop(ribbons.WidthTimes, ribbons.WidthValues, 0f, 1f);
        AddCurveStop(ribbons.WidthTimes, ribbons.WidthValues, 1f, 0.15f);
    }

    // The sheet demo. A checker whose cells line up exactly with a 4x4 grid, so every frame is a flat
    // block of one of two colours and the CROSSFADE between frames is the only thing you can see
    // happening - which is the point of the fractional frame. Step it instead and this stand strobes.
    private static void CreateFlipbookSheet(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Flipbook",
            "Sheet (ParticleFlipbook)\nsixteen cells, crossfaded, not stepped", cell, 1.3f);
        var system = NewSystem(slot, 90, 0f);
        system.EmissionStrength.Value = 0.9f;

        // 128 across at 32 a cell is exactly four by four, so a cell IS a frame. A stand-in for a real
        // sheet, and a better one than a photograph would be: you can name the frame you are looking at.
        var sheet = slot.AttachComponent<CheckerTextureProvider>();
        sheet.Width.Value = 128;
        sheet.Height.Value = 128;
        sheet.CellSize.Value = 32;
        sheet.ColorA.Value = new color(0.10f, 0.30f, 0.55f, 1f);
        sheet.ColorB.Value = new color(1.00f, 0.85f, 0.40f, 1f);
        system.Texture.Target = sheet;

        var emitter = slot.AttachComponent<BoxEmitter>();
        emitter.System.Target = system;
        emitter.Size.Value = new float3(0.5f, 0.05f, 0.5f);
        emitter.Rate.Value = 9f;

        var speed = AddParticleModule<ParticleSpeedInitializer>(slot, system);
        speed.Speed.Value = 0.14f;

        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 2.4f;

        var alpha = AddParticleModule<ParticleAlphaOverLifetime>(slot, system);
        AddCurveStop(alpha.Times, alpha.Values, 0f, 0f);
        AddCurveStop(alpha.Times, alpha.Values, 0.2f, 0.9f);
        AddCurveStop(alpha.Times, alpha.Values, 1f, 0f);

        var size = AddParticleModule<ParticleUniformSizeInitializer>(slot, system);
        size.Size.Value = 0.11f;

        var flipbook = AddParticleModule<ParticleFlipbook>(slot, system);
        flipbook.Columns.Value = 4;
        flipbook.Rows.Value = 4;
        flipbook.FrameCount.Value = 16;
        flipbook.Drive.Value = SimModules.FlipbookDrive.Rate;
        flipbook.LoopMode.Value = SimModules.FlipbookLoopMode.Loop;
        // Slow on purpose. At six a second the crossfade is doing all the work and you can watch it.
        flipbook.FramesPerSecond.Value = 6f;
        flipbook.RandomStartFrame.Value = true;
    }

    // Embers that are actually lighting the world. The simulation only NOMINATES candidates; the hook
    // holds the budget, keeps incumbents through near-ties and ramps promotions, so the thing to watch
    // here is that the lit embers do not blink as they trade places.
    private static void CreateEmberLights(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Ember Lights",
            "Embers (ParticleLights)\nsix of them are real dynamic lights", cell, 0.8f);
        var system = NewSystem(slot, 200, 0f);
        system.EmissionStrength.Value = 3.0f;

        var emitter = slot.AttachComponent<ConeEmitter>();
        emitter.System.Target = system;
        emitter.Radius.Value = 0.1f;
        emitter.Angle.Value = 16f;
        emitter.Rate.Value = 40f;

        var speed = AddParticleModule<ParticleSpeedRangeInitializer>(slot, system);
        speed.MinSpeed.Value = 0.9f;
        speed.MaxSpeed.Value = 1.3f;

        var life = AddParticleModule<ParticleLifetimeRangeInitializer>(slot, system);
        life.MinLifetime.Value = 0.7f;
        life.MaxLifetime.Value = 1.0f;

        var force = AddParticleModule<ParticleForce>(slot, system);
        force.Force.Value = new float3(0f, -2.4f, 0f);

        var gradient = AddParticleModule<ParticleColorGradient>(slot, system);
        AddGradientStop(gradient, 0f, new colorHDR(1.00f, 0.95f, 0.70f, 1f));
        AddGradientStop(gradient, 0.45f, new colorHDR(1.00f, 0.45f, 0.10f, 1f));
        AddGradientStop(gradient, 1f, new colorHDR(0.35f, 0.05f, 0.00f, 0f));

        var size = AddParticleModule<ParticleUniformSizeInitializer>(slot, system);
        size.Size.Value = 0.035f;

        var lights = AddParticleModule<ParticleLights>(slot, system);
        lights.MaxLights.Value = 6;
        lights.Selection.Value = SimModules.ParticleLightSelection.EveryNth;
        lights.Stride.Value = 5;
        lights.BaseColor.Value = new colorHDR(1.00f, 0.55f, 0.20f, 1f);
        lights.BaseIntensity.Value = 1.6f;
        lights.BaseRange.Value = 1.8f;
        // Size-scaled range would put these at four centimetres. The particle is a spark; the light it
        // throws is not the same size as the spark.
        lights.RangeFromSize.Value = false;
    }

    // The standalone renderer, with no particle system anywhere near it. A bob on an arc, a streak
    // behind it, recorded in the stand's space rather than in the bob's own - which is the whole reason
    // the streak stays in the air instead of swinging along with it.
    private static void CreateTrailPendulum(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Trail Pendulum",
            "Pendulum (TrailRenderer)\none slot, no particles, same ribbon builder", cell, 1.7f);

        var bob = slot.AddSlot("Bob");
        var mesh = bob.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 0.07f;
        var material = bob.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = new colorHDR(0.60f, 0.95f, 1.00f, 1f);
        var renderer = bob.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        // X sweeps side to side; Y runs at double the rate with a quarter turn of phase on it, so the
        // bob is highest at both ends of the swing and lowest through the middle. That is what makes it
        // read as an arc instead of a slider.
        //
        // The whole arc sits ABOVE the stand's own post. Hung below it the way a real pendulum would be,
        // the bob swings straight through the pole at the bottom of every stroke. -xlinka
        var swing = bob.AttachComponent<Oscillator3D>();
        swing.Min.Value = new float3(-0.75f, 0.05f, 0f);
        swing.Max.Value = new float3(0.75f, 0.30f, 0f);
        swing.Speed.Value = new float3(2.3f, 4.6f, 0f);
        swing.Phase.Value = new float3(0f, -MathF.PI * 0.5f, 0f);
        swing.Target.DriveTarget(bob.LocalPosition);

        var trail = bob.AttachComponent<TrailRenderer>();
        trail.Space.Target = slot;
        trail.Width.Value = 0.1f;
        trail.MinVertexDistance.Value = 0.02f;
        trail.MaxPointAge.Value = 0.7f;
        trail.MaxPoints.Value = 64;
        trail.Smoothing.Value = 5;
        trail.EmissionStrength.Value = 2.0f;
        trail.Alignment.Value = SimStrands.StrandAlignment.CameraFacing;
        trail.Color.Value = new colorHDR(0.55f, 0.90f, 1.00f, 1f);
        // The end-of-build sweep only reaches particle systems, and this is not one.
        trail.MaxViewDistance.Value = ParticleViewDistance;
        AddCurveStop(trail.WidthTimes, trail.WidthValues, 0f, 1f);
        AddCurveStop(trail.WidthTimes, trail.WidthValues, 1f, 0f);
        AddStrandColorStop(trail.ColorTimes, trail.ColorValues, 0f, new colorHDR(1f, 1f, 1f, 1f));
        AddStrandColorStop(trail.ColorTimes, trail.ColorValues, 0.4f, new colorHDR(0.5f, 0.7f, 1f, 0.7f));
        AddStrandColorStop(trail.ColorTimes, trail.ColorValues, 1f, new colorHDR(0.2f, 0.1f, 0.6f, 0f));
    }

    // A leg, a label out in front of it, and an empty slot at the top for the emitter to live on.
    private static Slot ParticleStand(Slot parent, string name, string caption, float2 cell, float height)
    {
        var stand = parent.AddSlot(name);
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);

        Post(stand, float2.Zero, MathF.Max(height - 0.15f, 0.06f), 0.05f);
        Sign(stand, caption, TowardSpawn(stand, new float3(0f, height + 0.95f, 0f), 0.35f), 0.075f);

        var slot = stand.AddSlot("System");
        slot.LocalPosition.Value = new float3(0f, height, 0f);
        return slot;
    }

    private static ParticleSystem NewSystem(Slot slot, int maxParticles, float gravity)
    {
        var system = slot.AttachComponent<ParticleSystem>();
        system.MaxParticles.Value = maxParticles;
        system.Gravity.Value = gravity;
        // A fixed step keeps the shape of an effect the same at any frame rate, which matters more here
        // than anywhere else: ten systems side by side make a stutter in one of them obvious.
        system.FixedTimeStep.Value = 1f / 90f;
        return system;
    }

    private static T AddParticleModule<T>(Slot slot, ParticleSystem system) where T : ParticleModuleBase, new()
    {
        var module = slot.AttachComponent<T>();
        module.System.Target = system;
        return module;
    }

    private static void CreateSparks(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Sparks", "Sparks (PointEmitter)\nspeed range, gravity force, colour by speed", cell, 1.1f);
        // The force module supplies the fall, so the system's own gravity stays out of it - otherwise the
        // sparks are pulled down twice and the number on the force module means nothing.
        var system = NewSystem(slot, 400, 0f);
        system.EmissionStrength.Value = 2.6f;

        var emitter = slot.AttachComponent<PointEmitter>();
        emitter.System.Target = system;
        emitter.Rate.Value = 160f;
        // Was 0.35 - with the lifetime range now actually applying, particles at the sideways end of
        // this spread had a full 1.4 s of unbraked horizontal travel to cash in. Tightened toward
        // straight up so the arc still kicks sideways without leaving the stand's cell.
        emitter.RandomDirectionWeight.Value = 0.2f;

        var speed = AddParticleModule<ParticleSpeedRangeInitializer>(slot, system);
        speed.MinSpeed.Value = 1.3f;
        speed.MaxSpeed.Value = 2.1f;

        var life = AddParticleModule<ParticleLifetimeRangeInitializer>(slot, system);
        life.MinLifetime.Value = 0.45f;
        life.MaxLifetime.Value = 0.75f;

        // Steeper than gravity would be on its own - short life, hard pull down, so the arc reads as a
        // quick throw-and-drop instead of a lazy hang in the air.
        var force = AddParticleModule<ParticleForce>(slot, system);
        force.Force.Value = new float3(0f, -11f, 0f);

        var tint = AddParticleModule<ParticleColorBySpeed>(slot, system);
        tint.MinSpeed.Value = 0.2f;
        tint.MaxSpeed.Value = 2.1f;
        tint.MinColor.Value = new colorHDR(0.65f, 0.10f, 0.02f, 1f);
        tint.MaxColor.Value = new colorHDR(1.00f, 0.92f, 0.60f, 1f);

        var size = AddParticleModule<ParticleSizeOverLifetime>(slot, system);
        size.StartSize.Value = new float3(0.05f, 0.05f, 0.05f);
        size.EndSize.Value = new float3(0.008f, 0.008f, 0.008f);
    }

    private static void CreateSmoke(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Smoke", "Smoke (SphereEmitter)\ndrag, alpha curve, size curve, spin", cell, 1.0f);
        var system = NewSystem(slot, 300, 0.35f);
        system.EmissionStrength.Value = 0.6f;

        var emitter = slot.AttachComponent<SphereEmitter>();
        emitter.System.Target = system;
        emitter.Radius.Value = 0.22f;
        emitter.Rate.Value = 45f;
        emitter.RandomDirectionWeight.Value = 0.4f;

        var speed = AddParticleModule<ParticleSpeedRangeInitializer>(slot, system);
        speed.MinSpeed.Value = 0.15f;
        speed.MaxSpeed.Value = 0.5f;

        var life = AddParticleModule<ParticleLifetimeRangeInitializer>(slot, system);
        life.MinLifetime.Value = 2.2f;
        life.MaxLifetime.Value = 3.4f;

        var drag = AddParticleModule<ParticleDrag>(slot, system);
        drag.Drag.Value = 0.55f;

        // The curves below own colour and size, so the envelope is off and a particle would otherwise be
        // born white. This initializer is what makes the smoke grey.
        var tint = AddParticleModule<ParticleColorInitializer>(slot, system);
        tint.Color.Value = new colorHDR(0.62f, 0.64f, 0.70f, 1f);

        var alpha = AddParticleModule<ParticleAlphaOverLifetime>(slot, system);
        AddCurveStop(alpha.Times, alpha.Values, 0f, 0f);
        AddCurveStop(alpha.Times, alpha.Values, 0.25f, 0.55f);
        AddCurveStop(alpha.Times, alpha.Values, 1f, 0f);

        var size = AddParticleModule<ParticleSizeCurve>(slot, system);
        AddCurveStop(size.Times, size.Values, 0f, 0.12f);
        AddCurveStop(size.Times, size.Values, 1f, 0.42f);

        var spin = AddParticleModule<ParticleSpin>(slot, system);
        spin.Force.Value = new float3(0f, 0f, 0.8f);
    }

    private static void CreateRain(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Rain", "Rain (BoxEmitter)\ndownward velocity, oriented by velocity", cell, 2.4f);
        // Footprint was already inside the cell (the box emitter bounds it); the cap just needed to
        // come down off 500 to stay modest like every other stand here.
        var system = NewSystem(slot, 380, -2f);
        system.EmissionStrength.Value = 0.4f;
        // Nothing in this system writes colour or size, so the envelope stays on and these four fields are
        // what a drop is tinted with; the streak shape comes from the size initializer below.
        system.StartColor.Value = new colorHDR(0.65f, 0.80f, 1.00f, 0.85f);
        system.EndColor.Value = new colorHDR(0.45f, 0.60f, 0.95f, 0.25f);
        system.StartSize.Value = 1f;
        system.EndSize.Value = 1f;

        var emitter = slot.AttachComponent<BoxEmitter>();
        emitter.System.Target = system;
        emitter.Size.Value = new float3(1.6f, 0.05f, 1.0f);
        emitter.Rate.Value = 220f;

        var velocity = AddParticleModule<ParticleVelocityInitializer>(slot, system);
        velocity.Velocity.Value = new float3(0f, -4.5f, 0f);

        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 0.9f;

        var size = AddParticleModule<ParticleSizeInitializer>(slot, system);
        size.Size.Value = new float3(0.012f, 0.16f, 0.012f);

        var orient = AddParticleModule<ParticleOrientByVelocity>(slot, system);
        orient.Up.Value = float3.Up;
        orient.MinimumVelocity.Value = 0.2f;
        orient.VelocityTransitionRange.Value = 0.4f;
    }

    private static void CreateRingBurst(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Ring Burst", "Ring burst (CircleEmitter)\nradial force pushing the rim out", cell, 1.0f);
        var system = NewSystem(slot, 320, -0.6f);
        system.StartColor.Value = new colorHDR(0.30f, 0.95f, 1.00f, 0.95f);
        system.EndColor.Value = new colorHDR(0.10f, 0.30f, 0.90f, 0f);
        system.StartSize.Value = 0.05f;
        system.EndSize.Value = 0.01f;

        var emitter = slot.AttachComponent<CircleEmitter>();
        emitter.System.Target = system;
        emitter.Radius.Value = 0.12f;
        emitter.FromShell.Value = true;
        emitter.Alignment.Value = SimEmitters.CircleEmitterAlignment.XZ;
        emitter.DirectionMode.Value = SimEmitters.CircleEmitterDirection.RadialUniform;
        emitter.Rate.Value = 90f;

        var speed = AddParticleModule<ParticleSpeedInitializer>(slot, system);
        speed.Speed.Value = 0.4f;

        // Was 1.4 - a Linear radial push GROWS with distance, so a particle riding it for a real 1.4 s
        // (instead of the pre-fix no-op lifetime) blows straight through the clamp and out past the
        // neighbouring stand. Cut down so the rim finishes fading before it gets there.
        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 0.85f;

        // The centre is this component's own slot, which is the system slot, so the rim is shoved out from
        // the middle of its own emitter. The distance clamps are not decoration: with no floor on the
        // distance, a particle crossing the centre takes an unbounded shove and leaves the world.
        var radial = AddParticleModule<ParticleRadialForce>(slot, system);
        radial.Force.Value = 1.6f;
        radial.Mode.Value = SimModules.RadialForceMode.Linear;
        radial.MinDistance.Value = 0.1f;
        radial.MaxDistance.Value = 1.0f;
    }

    private static void CreateFireflies(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Fireflies", "Fireflies (ConeVolumeEmitter)\nturbulence, attractor, colour gradient", cell, 0.9f);
        var system = NewSystem(slot, 220, 0f);
        system.EmissionStrength.Value = 3.2f;

        var emitter = slot.AttachComponent<ConeVolumeEmitter>();
        emitter.System.Target = system;
        emitter.BaseRadius.Value = 0.5f;
        emitter.Height.Value = 1.2f;
        emitter.DirectionMode.Value = SimEmitters.ConeEmitterDirection.RadialUniform;
        emitter.Rate.Value = 28f;
        emitter.RandomDirectionWeight.Value = 0.6f;

        var speed = AddParticleModule<ParticleSpeedRangeInitializer>(slot, system);
        speed.MinSpeed.Value = 0.05f;
        speed.MaxSpeed.Value = 0.25f;

        // Was 3-5 s. The turbulence field has no drag to bleed it off, so a fly living that long drifts
        // well past the lure before it dies - the horizontal spread stayed put on RandomDirectionWeight
        // and cone size, this was purely a "how long does it get to wander" problem.
        var life = AddParticleModule<ParticleLifetimeRangeInitializer>(slot, system);
        life.MinLifetime.Value = 1.5f;
        life.MaxLifetime.Value = 2.1f;

        var turbulence = AddParticleModule<ParticleTurbulence>(slot, system);
        turbulence.Strength.Value = 0.22f;
        turbulence.Frequency.Value = 1.8f;
        turbulence.ScrollSpeed.Value = 0.4f;

        // An attractor pulls toward its OWN slot, so it gets one of its own above the cone and the flies
        // gather up there instead of back at the emitter.
        var lure = slot.AddSlot("Lure");
        lure.LocalPosition.Value = new float3(0f, 1.1f, 0f);
        var attractor = AddParticleModule<ParticleAttractor>(lure, system);
        attractor.Strength.Value = 1.2f;
        attractor.Range.Value = 2f;

        var gradient = AddParticleModule<ParticleColorGradient>(slot, system);
        AddGradientStop(gradient, 0f, new colorHDR(0.10f, 0.08f, 0.02f, 0f));
        AddGradientStop(gradient, 0.35f, new colorHDR(1.00f, 0.85f, 0.35f, 1f));
        AddGradientStop(gradient, 1f, new colorHDR(0.35f, 0.20f, 0.02f, 0f));

        // Gradient on means envelope off, so the size has to come from somewhere.
        var size = AddParticleModule<ParticleUniformSizeInitializer>(slot, system);
        size.Size.Value = 0.04f;
    }

    private static void CreateTornado(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Tornado", "Tornado (CylinderEmitter)\nangular velocity, pulled back to birth point", cell, 0.6f);
        // Was 420 - a Linear origin tether is a spring (stable, unlike Ring Burst's push), but the orbit
        // it settles into still scales with speed and lifetime, both of which used to be no-ops. Capacity
        // comes down with them.
        var system = NewSystem(slot, 380, 0f);
        system.StartColor.Value = new colorHDR(0.75f, 0.70f, 0.55f, 0.7f);
        system.EndColor.Value = new colorHDR(0.45f, 0.40f, 0.32f, 0f);
        system.StartSize.Value = 0.045f;
        system.EndSize.Value = 0.02f;

        var emitter = slot.AttachComponent<CylinderEmitter>();
        emitter.System.Target = system;
        emitter.Radius.Value = 0.35f;
        emitter.Height.Value = 1.8f;
        emitter.FromShell.Value = true;
        emitter.ExcludeCaps.Value = true;
        emitter.DirectionMode.Value = SimEmitters.CylinderEmitterDirection.CircleUniform;
        emitter.Rate.Value = 100f;

        var speed = AddParticleModule<ParticleSpeedRangeInitializer>(slot, system);
        speed.MinSpeed.Value = 0.6f;
        speed.MaxSpeed.Value = 1.0f;

        var life = AddParticleModule<ParticleLifetimeRangeInitializer>(slot, system);
        life.MinLifetime.Value = 1.6f;
        life.MaxLifetime.Value = 2.2f;

        var spin = AddParticleModule<ParticleAngularVelocityRangeInitializer>(slot, system);
        spin.MinAngularVelocity.Value = 120f;
        spin.MaxAngularVelocity.Value = 360f;

        // Each particle is tethered to the spot it was born on rather than to a shared centre, so the wall
        // of the cylinder holds together as it swirls instead of collapsing onto the axis. It settles into
        // an orbit of radius roughly speed/sqrt(Force) around its birth point - a stiffer spring here
        // keeps that orbit tight against the shrunk emitter radius above.
        var origin = AddParticleModule<ParticleOriginForce>(slot, system);
        origin.Force.Value = 4.5f;
        origin.Mode.Value = SimModules.RadialForceMode.Linear;
        origin.MinDistance.Value = 0.05f;
        origin.MaxDistance.Value = 1.2f;
    }

    private static void CreateCurtain(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Curtain", "Curtain (LineEmitter)\ncolour list, uniform size over lifetime", cell, 2.2f);
        var system = NewSystem(slot, 380, -0.4f);

        var emitter = slot.AttachComponent<LineEmitter>();
        emitter.System.Target = system;
        emitter.Point0.Value = new float3(-0.8f, 0f, 0f);
        emitter.Point1.Value = new float3(0.8f, 0f, 0f);
        emitter.DirectionMode.Value = SimEmitters.LineEmitterDirection.Fixed;
        emitter.Direction0.Value = new float3(0f, -1f, 0f);
        emitter.Direction1.Value = new float3(0f, -1f, 0f);
        emitter.Rate.Value = 140f;

        var speed = AddParticleModule<ParticleSpeedInitializer>(slot, system);
        speed.Speed.Value = 0.6f;

        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 2f;

        var colors = AddParticleModule<ParticleColorListInitializer>(slot, system);
        colors.Colors.Add(new colorHDR(0.95f, 0.35f, 0.55f, 1f));
        colors.Colors.Add(new colorHDR(0.35f, 0.85f, 0.95f, 1f));
        colors.Colors.Add(new colorHDR(0.95f, 0.85f, 0.35f, 1f));
        colors.Colors.Add(new colorHDR(0.55f, 0.45f, 0.95f, 1f));

        var size = AddParticleModule<ParticleUniformSizeOverLifetime>(slot, system);
        size.StartSize.Value = 0.06f;
        size.EndSize.Value = 0f;
    }

    private static void CreateMeshSpray(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Mesh Spray", "Mesh spray (MeshEmitter)\noff a torus surface, coloured by direction", cell, 1.2f);
        var system = NewSystem(slot, 360, 0f);
        system.EmissionStrength.Value = 1.6f;

        // The surface the particles come off is worth seeing, so the torus is a rendered mesh rather than
        // an invisible source.
        var shape = slot.AddSlot("Torus");
        var mesh = shape.AttachComponent<TorusMesh>();
        mesh.MajorRadius.Value = 0.3f;
        mesh.MinorRadius.Value = 0.09f;
        mesh.MajorSegments.Value = 32;
        mesh.MinorSegments.Value = 14;
        var material = shape.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.30f, 0.32f, 0.38f, 1f);
        material.Metallic.Value = 0.6f;
        material.Smoothness.Value = 0.55f;
        var renderer = shape.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        var emitter = shape.AttachComponent<MeshEmitter>();
        emitter.System.Target = system;
        emitter.SourceMesh.Target = mesh;
        emitter.EmitFrom.Value = SimEmitters.MeshEmissionSource.Faces;
        emitter.DirectionMode.Value = SimEmitters.MeshEmitterDirection.TangentSpace;
        emitter.Direction.Value = float3.Forward;
        emitter.Rate.Value = 120f;

        // Was 0.7 speed / 1.6 s life - straight-line travel off the surface with no drag, so with the
        // lifetime now actually applied the spray was ballooning to nearly a metre past the torus. Cut
        // both so the shell sits close around the ring instead of swallowing the neighbour's cell.
        var speed = AddParticleModule<ParticleSpeedInitializer>(slot, system);
        speed.Speed.Value = 0.45f;

        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 1.0f;

        var tint = AddParticleModule<ParticleColorByDirection>(slot, system);
        tint.ReferenceDirection.Value = float3.Up;
        tint.AlignedColor.Value = new colorHDR(0.35f, 1.00f, 0.65f, 1f);
        tint.OrthogonalColor.Value = new colorHDR(0.20f, 0.45f, 0.95f, 1f);
        tint.OppositeColor.Value = new colorHDR(0.95f, 0.35f, 0.25f, 1f);

        var size = AddParticleModule<ParticleUniformSizeInitializer>(slot, system);
        size.Size.Value = 0.05f;

        // Orients at its own slot, and that slot IS the torus, so every particle keeps looking back at the
        // ring it came off.
        AddParticleModule<ParticleOrientAtPoint>(shape, system).Up.Value = float3.Up;
    }

    private static void CreateSubEmitterChain(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Sub Emitters", "Sub-emitters (birth, lifetime, death)\none system spawning into another", cell, 1.0f);

        // The child carries no emitter component of its own, and a system with no emitter falls back to
        // its built-in disc spray - so its rate and burst are zeroed. Everything it holds arrives from the
        // parent's three sub-emitters. -xlinka
        var childSlot = slot.AddSlot("Chain Sparks");
        // Was 600. The child has no lifetime module of its own - every arriving particle gets a flat 1 s
        // - so its capacity only ever needed to cover what the parent can hand it per second, not a
        // long-lived population.
        var child = NewSystem(childSlot, 380, -2.5f);
        child.EmissionRate.Value = 0f;
        child.BurstCount.Value = 0;
        child.EmissionStrength.Value = 2.4f;
        child.StartColor.Value = new colorHDR(1.00f, 0.75f, 0.30f, 1f);
        child.EndColor.Value = new colorHDR(0.90f, 0.20f, 0.10f, 0f);
        child.StartSize.Value = 0.03f;
        child.EndSize.Value = 0.006f;

        var system = NewSystem(slot, 120, -1.6f);
        system.StartColor.Value = new colorHDR(0.85f, 0.90f, 1.00f, 0.9f);
        system.EndColor.Value = new colorHDR(0.40f, 0.55f, 0.95f, 0f);
        system.StartSize.Value = 0.07f;
        system.EndSize.Value = 0.03f;

        var emitter = slot.AttachComponent<PointEmitter>();
        emitter.System.Target = system;
        emitter.Rate.Value = 8f;
        emitter.RandomDirectionWeight.Value = 0.15f;

        // Was 2.2-2.8 speed / 1.6 s life. The trail sub-emitter hands the child its exact CURRENT
        // velocity, so a faster, longer-lived parent was launching child sparks fast AND from further out
        // along its own now-real flight path - the two effects compounded. Both come down together.
        var speed = AddParticleModule<ParticleSpeedRangeInitializer>(slot, system);
        speed.MinSpeed.Value = 0.9f;
        speed.MaxSpeed.Value = 1.1f;

        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 0.7f;

        var birth = AddParticleModule<ParticleBirthSubEmitter>(slot, system);
        birth.TargetSystem.Target = child;
        birth.EmitMin.Value = 2;
        birth.EmitMax.Value = 4;
        birth.DirectionMode.Value = SimModules.SubEmissionDirectionMode.VelocityDirection;

        var trail = AddParticleModule<ParticleLifetimeSubEmitter>(slot, system);
        trail.TargetSystem.Target = child;
        trail.Rate.Value = 8f;
        trail.EmitMin.Value = 1;
        trail.EmitMax.Value = 2;
        trail.DirectionMode.Value = SimModules.SubEmissionDirectionMode.Velocity;

        // Burst size trimmed too, and switched from VelocityDirection (a hardcoded 1 m/s regardless of
        // how slow the parent is) to Velocity, so a death particle's speed is bounded by the parent's
        // actual - now much smaller - speed instead of a fixed unit vector that ignored every other
        // knob on this system.
        var death = AddParticleModule<ParticleDeathSubEmitter>(slot, system);
        death.TargetSystem.Target = child;
        death.EmitMin.Value = 4;
        death.EmitMax.Value = 7;
        death.DirectionMode.Value = SimModules.SubEmissionDirectionMode.Velocity;
        death.RandomDirectionWeight.Value = 1f;
    }

    private static void CreateRotationDemo(Slot parent, float2 cell)
    {
        var slot = ParticleStand(parent, "Rotation", "Rotation (Rotation3D initializers)\nrandom spin bled off by angular drag", cell, 1.0f);
        var system = NewSystem(slot, 200, -1f);
        system.StartColor.Value = new colorHDR(0.85f, 0.55f, 0.95f, 1f);
        system.EndColor.Value = new colorHDR(0.35f, 0.20f, 0.60f, 0f);
        system.StartSize.Value = 0.09f;
        system.EndSize.Value = 0.05f;

        var emitter = slot.AttachComponent<ConeEmitter>();
        emitter.System.Target = system;
        emitter.Radius.Value = 0.12f;
        emitter.Angle.Value = 22f;
        emitter.Rate.Value = 40f;

        // Was 1.6 speed / 2.5 s life - the cone's 22 degree flare turns into real horizontal ground
        // covered once particles get a real 2.5 s to fly it, not the pre-fix 1 s. Cut down so the tumble
        // stays framed near the stand.
        var speed = AddParticleModule<ParticleSpeedInitializer>(slot, system);
        speed.Speed.Value = 1.4f;

        var life = AddParticleModule<ParticleLifetimeInitializer>(slot, system);
        life.Lifetime.Value = 1.3f;

        var pose = AddParticleModule<ParticleRotation3DInitializer>(slot, system);
        pose.EulerAngles.Value = new float3(25f, 40f, 15f);

        var poseRange = AddParticleModule<ParticleRotation3DRangeInitializer>(slot, system);
        poseRange.MinEulerAngles.Value = new float3(-45f, -180f, -45f);
        poseRange.MaxEulerAngles.Value = new float3(45f, 180f, 45f);

        var spin = AddParticleModule<ParticleAngularVelocity3DRangeInitializer>(slot, system);
        spin.MinAngularVelocity.Value = new float3(-220f, -220f, -220f);
        spin.MaxAngularVelocity.Value = new float3(220f, 220f, 220f);

        var drag = AddParticleModule<ParticleAngularDrag>(slot, system);
        drag.Drag.Value = 0.6f;
    }

    private static void AddCurveStop(SyncFieldList<float> times, SyncFieldList<float> values, float time, float value)
    {
        times.Add(time);
        values.Add(value);
    }

    private static void AddGradientStop(ParticleColorGradient gradient, float time, colorHDR value)
    {
        gradient.Times.Add(time);
        gradient.Colors.Add(value);
    }

    // Same two-parallel-lists shape as the curve stops, for the colour ramps that run ALONG a strand
    // rather than across a particle's life.
    private static void AddStrandColorStop(SyncFieldList<float> times, SyncFieldList<colorHDR> colors, float time, colorHDR value)
    {
        times.Add(time);
        colors.Add(value);
    }

    // UI GALLERY
    //
    // Every dashboard screen and every import dialog, standing in the world instead of behind a key press.
    //
    // A screen cannot be hung on a bare canvas: WidgetScreen pulls its font off the Dashboard above it and
    // text with no font renders NOTHING, so each stand carries a real Dashboard with exactly one screen
    // registered. That is also why the frames look like the dash - because they are one. AddScreen does
    // the build, the register and the show; ShowScreen is called again here so the first render is forced
    // rather than waiting on the first hover.
    //
    // The screens all reach for the engine (the world manager, the input map, the local dashboard) through
    // null-conditional accessors, so a stand in a template world builds and draws whatever it can and
    // leaves the rest empty. Nothing here fakes a session for them to read.
    //
    // Eleven live dashboards on one plate is the most expensive corner of this world by a wide margin -
    // every one of them owns a canvas that lays out and tessellates. That is the price of showing them all
    // at once instead of one at a time behind a key press, and it is worth knowing before reading an FPS
    // number taken while standing here. -xlinka
    private static void CreateUiGallery(World world)
    {
        var root = world.RootSlot.AddSlot("UI Gallery");
        root.LocalPosition.Value = UiGalleryOrigin;

        AreaPlate(world, "UI gallery", new float2(UiGalleryOrigin.x, UiGalleryOrigin.z),
            new float2(12f, 8.5f), new colorHDR(0.18f, 0.20f, 0.30f, 1f));
        Sign(root, "UI gallery\nevery dashboard screen, every import dialog", new float3(4.6f, 3.2f, 0f), 0.26f);

        // Six columns two metres apart, three rows two and a half apart. Screens first, then the dialogs.
        int index = 0;
        AddScreenStand<HomeScreen>(world, root, "Home", GalleryCell(index++));
        AddScreenStand<WorldsScreen>(world, root, "Worlds", GalleryCell(index++));
        AddScreenStand<GroupsScreen>(world, root, "Groups", GalleryCell(index++));
        AddScreenStand<InventoryScreen>(world, root, "Inventory", GalleryCell(index++));
        AddScreenStand<SessionScreen>(world, root, "Session", GalleryCell(index++));
        AddScreenStand<SettingsScreen>(world, root, "Settings", GalleryCell(index++));
        AddScreenStand<FileBrowserScreen>(world, root, "Files", GalleryCell(index++));
        AddScreenStand<DebugScreen>(world, root, "Debug", GalleryCell(index++));
        AddScreenStand<ExitScreen>(world, root, "Exit", GalleryCell(index++));
        AddScreenStand<WidgetGridScreen>(world, root, "Widget grid", GalleryCell(index++));
        AddComingSoonStand(world, root, "Placeholder", GalleryCell(index++));

        AddDialogStand<ImageImportDialog>(world, root, "Image import", "demo/sample.png", GalleryCell(index++));
        AddDialogStand<ModelImportDialog>(world, root, "Model import", "demo/sample.glb", GalleryCell(index++));
        AddDialogStand<ShaderImportDialog>(world, root, "Shader import", "demo/sample.gdshader", GalleryCell(index++));
        AddDialogStand<VideoImportDialog>(world, root, "Video import", "demo/sample.mp4", GalleryCell(index++));
        AddDialogStand<UnsupportedImportDialog>(world, root, "Unsupported import", "demo/sample.wav", GalleryCell(index++)).ClassName = "Audio";
        AddFolderDialogStand(world, root, "Folder import", GalleryCell(index++));
        AddImportIndicatorStand(world, root, "Import indicator", GalleryCell(index++));
    }

    private static float2 GalleryCell(int index)
    {
        const int columns = 6;
        int column = index % columns;
        int row = index / columns;
        return new float2((column - (columns - 1) * 0.5f) * 2f, (row - 1) * 2.5f);
    }

    // Leg, nameplate out in front of it, and a slot at eye height turned to face spawn. The canvas scale
    // puts a 1180 px dashboard at 1.18 m across, which fits the two-metre cell with room to walk between.
    private static Slot GalleryStand(Slot areaRoot, string label, string caption, float2 cell, float scale)
    {
        var stand = areaRoot.AddSlot(label + " Stand");
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);

        Post(stand, float2.Zero, 0.95f, 0.05f);
        Sign(stand, caption, TowardSpawn(stand, new float3(0f, 0.62f, 0f), 0.4f), 0.062f);

        var host = stand.AddSlot(label);
        host.LocalPosition.Value = new float3(0f, 1.5f, 0f);
        host.LocalScale.Value = new float3(scale, scale, scale);
        FaceSpawn(host);
        CullPanelBeyond(host);
        return host;
    }

    private static void AddScreenStand<T>(World world, Slot areaRoot, string label, float2 cell)
        where T : DashboardScreen, new()
    {
        if (!ComponentExecutionPolicy.CanAttach(typeof(T), world))
        {
            AddPrivateUiStand(areaRoot, label, cell);
            return;
        }
        var host = GalleryStand(areaRoot, label, label + " screen\n(" + typeof(T).Name + ")", cell, 0.001f);
        var dash = NewGalleryDashboard(world, host, label);
        dash.OpenFilled(dash.AddScreen<T>(label));
    }

    // The plain screen with a hand-built body, which is exactly how the dashboard makes a tab for a
    // feature that has no backend yet.
    private static void AddComingSoonStand(World world, Slot areaRoot, string label, float2 cell)
    {
        if (!ComponentExecutionPolicy.CanAttach(typeof(DashboardScreen), world))
        {
            AddPrivateUiStand(areaRoot, label, cell);
            return;
        }
        var host = GalleryStand(areaRoot, label, "Placeholder tab\n(DashboardScreen, built by hand)", cell, 0.001f);
        var dash = NewGalleryDashboard(world, host, label);

        var screen = dash.AddScreen<DashboardScreen>("Demo (Soon)", new color(0.55f, 0.60f, 0.72f, 1f));
        var content = screen.ContentSlot;
        if (content == null)
            return;

        var builder = new UIBuilder(content);
        builder.Font(SharedFont(world)).TextColor(color.White);
        var layout = builder.VerticalLayout(10f, 28f);
        FillRect(layout.RectTransform!);

        builder.FontSize(24f).MinHeight(44f).PreferredHeight(48f).FlexibleHeight(0f);
        var heading = builder.Text("Demo - coming soon", 24f, new color(0.62f, 0.66f, 0.74f, 1f));
        heading.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        heading.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        builder.FontSize(16f).MinHeight(60f).PreferredHeight(90f).FlexibleHeight(0f);
        var body = builder.Text("A screen with no component of its own: a plain DashboardScreen with a body written into its content slot.",
            16f, new color(0.62f, 0.66f, 0.74f, 1f));
        body.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        body.VerticalAlignment.Value = TextVerticalAlignment.Top;

        builder.NestOut();
        dash.OpenFilled(screen);
    }

    private static Dashboard NewGalleryDashboard(World world, Slot host, string title)
    {
        var dash = host.AttachComponent<Dashboard>();
        dash.Font.Target = SharedFont(world);
        dash.Title.Value = title;
        return dash;
    }

    // A dialog builds its own panel on its first update, so all this stand does is put it somewhere and
    // hand it a path. The path points at nothing on purpose: pressing Import logs that the file is
    // missing (the async read fails and warns, or a null handler drops a labelled placeholder) and the
    // world carries on. Like any import dialog, it claims its creator on startup and is visible
    // to everyone. -xlinka
    private static T AddDialogStand<T>(World world, Slot areaRoot, string label, string samplePath, float2 cell)
        where T : ImportDialog, new()
    {
        var host = GalleryStand(areaRoot, label,
            label + "\ndemo dialog, sample path, Import will report the missing file", cell, 1f);
        var dialog = host.AttachComponent<T>();
        dialog.TargetWorld = world;
        dialog.Paths.Add(samplePath);
        return dialog;
    }

    private static void AddFolderDialogStand(World world, Slot areaRoot, string label, float2 cell)
    {
        var host = GalleryStand(areaRoot, label,
            label + "\ndemo dialog, sample path, Import will report the missing folder", cell, 1f);
        var dialog = host.AttachComponent<FolderImportDialog>();
        dialog.TargetWorld = world;
        dialog.Path = "demo/sample-folder";
    }

    private static void AddPrivateUiStand(Slot areaRoot, string label, float2 cell)
    {
        var host = GalleryStand(areaRoot, label, label, cell, 0.001f);
        var shell = host.AttachComponent<PanelShell>();
        shell.Title.Value = label;
        shell.Size.Value = new float2(760f, 260f);
        shell.Font.Target = SharedFont(areaRoot.World);
        shell.RebuildContent(ui => ui.Text("Open this in your dashboard.\nPersonal controls stay in your private userspace.", 26f, color.White));
    }

    // The indicator floats itself above whatever anchor it is given and follows the local user's head, so
    // the stand is an anchor slot and nothing else.
    private static void AddImportIndicatorStand(World world, Slot areaRoot, string label, float2 cell)
    {
        var host = GalleryStand(areaRoot, label,
            "Import progress readout\n(ModelImportIndicator, parked mid-stage)", cell, 1f);

        // It floats itself 1.9 m above whatever anchor it is handed, so the anchor goes on the floor of
        // the stand rather than at panel height. There is one indicator at a time by design - a real model
        // import replaces this one and takes the stand with it, which is the component behaving correctly
        // rather than the demo breaking. -xlinka
        var anchor = host.AddSlot("Anchor");
        anchor.LocalPosition.Value = new float3(0f, -1.5f, 0f);
        ModelImportIndicator.Show(world, anchor, "Importing sample.glb");
        // Parked on a counted stage so the stand shows what a real import looks like mid-flight
        // rather than an empty bar.
        ModelImportIndicator.Report(new Components.Import.ImportProgress(
            Components.Import.ImportStage.Textures, 0.45f, 5, 11));
    }

    // LIGHTS AND TEXT
    //
    // The three light types with their shadows and colours, the glow marker, and what world text can do.
    //
    // Rich text and word wrap are CANVAS text features - the world-space TextRenderer takes a string, a
    // size and an outline, and its line breaks are the ones you type. So the tag sample and the wrapped
    // paragraph are a Helio panel, and the sizes and outlines beside them are the world renderer. Putting
    // them side by side is the point: those are two different text paths. -xlinka
    private static void CreateLightsAndText(World world)
    {
        var root = world.RootSlot.AddSlot("Lights And Text");
        root.LocalPosition.Value = LightsOrigin;

        AreaPlate(world, "Lights and text", new float2(LightsOrigin.x, LightsOrigin.z),
            new float2(10f, 6f), new colorHDR(0.22f, 0.21f, 0.17f, 1f));
        Sign(root, "Lights and text", new float3(0f, 3.2f, -2.6f), 0.26f);

        CreateLampStand(root, "Point lamp", "Point light (Light)\nwarm, soft shadows, six metre range",
            new float2(-4f, -1.5f), LightType.Point, new color(1.00f, 0.72f, 0.42f, 1f), light =>
            {
                light.Intensity.Value = 3.2f;
                light.Range.Value = 6f;
                light.Shadows.Value = ShadowType.Soft;
                // A six-metre lamp with soft shadows, on a post, in an area you can only be in one of at
                // a time. Ten metres out it is doing nothing you can see and it still has a shadow map. -xlinka
                light.DistanceFadeBegin.Value = LampFadeBegin;
                light.DistanceFadeLength.Value = LampFadeLength;
            });

        CreateLampStand(root, "Spot lamp", "Spot light (Light)\ncold, hard shadows, 32 degree cone",
            new float2(-2f, -1.5f), LightType.Spot, new color(0.55f, 0.78f, 1.00f, 1f), light =>
            {
                light.Intensity.Value = 4f;
                light.Range.Value = 8f;
                light.SpotAngle.Value = 32f;
                light.Shadows.Value = ShadowType.Hard;
                light.DistanceFadeBegin.Value = LampFadeBegin;
                light.DistanceFadeLength.Value = LampFadeLength;
                // Aim it down the post at the floor, otherwise a spot on a pole lights the sky.
                light.Slot.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, -MathF.PI * 0.42f);
            });

        // A directional light has no position and no range: it lights the whole world, so a second one is
        // a change to every area at once. It stays in at a twelfth of the sun's strength, as a fill that
        // is visible on the props here and does not repaint the rest of the place. -xlinka
        CreateLampStand(root, "Directional fill", "Directional light (Light)\nworld-wide, so it is dialled right down",
            new float2(0f, -1.5f), LightType.Directional, new color(0.62f, 0.68f, 1.00f, 1f), light =>
            {
                light.Intensity.Value = 0.12f;
                light.Shadows.Value = ShadowType.None;
                light.Slot.LocalRotation.Value = floatQ.Euler(2.2f, -0.9f, 0f);
            });

        CreateGlowMarker(root, new float2(2f, -1.5f));
        CreateTextSizes(root, new float2(4f, -1.5f));
        CreateOutlineSamples(root, new float2(-4f, 1.5f));
        CreateCanvasTextPanel(world, root, new float2(0f, 1.5f));
        CreateWorldParagraph(root, new float2(3f, 1.5f));
    }

    // A lamp on a post with a bulb you can see, because a Light on its own is an invisible component and
    // "which one is the spot" is unanswerable without something glowing at the source.
    private static void CreateLampStand(Slot parent, string name, string caption, float2 cell, LightType type, color tint, Action<Light> configure)
    {
        var stand = parent.AddSlot(name);
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);
        Post(stand, float2.Zero, 1.5f, 0.05f);
        Sign(stand, caption, TowardSpawn(stand, new float3(0f, 2.15f, 0f), 0.4f), 0.07f);

        var slot = stand.AddSlot("Lamp");
        slot.LocalPosition.Value = new float3(0f, 1.7f, 0f);

        var bulb = slot.AddSlot("Bulb");
        var bulbMesh = bulb.AttachComponent<SphereMesh>();
        bulbMesh.Radius.Value = 0.07f;
        bulbMesh.Segments.Value = 18;
        bulbMesh.Rings.Value = 10;
        var bulbMaterial = bulb.AttachComponent<UnlitMaterial>();
        bulbMaterial.TintColor.Value = new colorHDR(tint.r, tint.g, tint.b, 1f);
        bulbMaterial.UseVertexColor.Value = false;
        var bulbRenderer = bulb.AttachComponent<MeshRenderer>();
        bulbRenderer.Mesh.Target = bulbMesh;
        bulbRenderer.Material.Target = bulbMaterial;

        var light = slot.AttachComponent<Light>();
        light.Type.Value = type;
        light.LightColor.Value = tint;
        configure(light);

        // Something for the lamp to actually light, or a shadow setting proves nothing.
        var target = stand.AddSlot("Lit Block");
        // Under the lamp on the spawn side, which is also where the spot is aimed: a Godot light points
        // down its own -Z, so the tilt above throws the cone down and toward the visitor.
        target.LocalPosition.Value = new float3(0f, 0.2f, -0.55f);
        var targetMesh = target.AttachComponent<BevelBoxMesh>();
        targetMesh.Size.Value = new float3(0.4f, 0.4f, 0.4f);
        targetMesh.Bevel.Value = 0.05f;
        targetMesh.BevelSegments.Value = 3;
        var targetMaterial = target.AttachComponent<PBS_Metallic>();
        targetMaterial.AlbedoColor.Value = new colorHDR(0.78f, 0.78f, 0.80f, 1f);
        targetMaterial.Metallic.Value = 0.05f;
        targetMaterial.Smoothness.Value = 0.35f;
        var targetRenderer = target.AttachComponent<MeshRenderer>();
        targetRenderer.Mesh.Target = targetMesh;
        targetRenderer.Material.Target = targetMaterial;
    }

    private static void CreateGlowMarker(Slot parent, float2 cell)
    {
        var stand = parent.AddSlot("Glow Marker");
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);
        Sign(stand, "Ground marker (GlowCircle)\nbuilds its own disc and ring", new float3(0f, 1.3f, 0f), 0.07f);

        var slot = stand.AddSlot("Glow");
        slot.LocalPosition.Value = new float3(0f, 0.02f, 0f);
        var glow = slot.AttachComponent<GlowCircle>();
        glow.Setup(0.6f, 0.7f, new colorHDR(0.30f, 0.85f, 1.00f, 0.85f), new colorHDR(0.20f, 0.55f, 1.00f, 0.55f));
    }

    private static void CreateTextSizes(Slot parent, float2 cell)
    {
        var stand = parent.AddSlot("Text Sizes");
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);
        Post(stand, float2.Zero, 1.0f, 0.05f);
        Sign(stand, "World text at three sizes (TextRenderer)", TowardSpawn(stand, new float3(0f, 2.3f, 0f), 0.4f), 0.07f);

        WorldTextSample(stand, "0.32 m", new float3(0f, 1.95f, 0f), 0.32f);
        WorldTextSample(stand, "0.16 m", new float3(0f, 1.6f, 0f), 0.16f);
        WorldTextSample(stand, "0.08 m", new float3(0f, 1.4f, 0f), 0.08f);
    }

    private static void CreateOutlineSamples(Slot parent, float2 cell)
    {
        var stand = parent.AddSlot("Text Outlines");
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);
        Post(stand, float2.Zero, 1.0f, 0.05f);
        Sign(stand, "Outline on and off (TextRenderer)\nthe outline is what keeps text readable over a bright wall",
            TowardSpawn(stand, new float3(0f, 2.3f, 0f), 0.4f), 0.07f);

        var outlined = WorldTextSample(stand, "outline on", new float3(0f, 1.85f, 0f), 0.14f);
        outlined.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 1f);
        outlined.OutlineThickness.Value = 1.6f;

        var plain = WorldTextSample(stand, "outline off", new float3(0f, 1.55f, 0f), 0.14f);
        plain.OutlineThickness.Value = 0f;
        plain.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0f);

        // A bright panel behind both, so "readable over what" is answered rather than asserted. Behind
        // means away from spawn, not local -Z: this area sits behind the spawn point, where local -Z is
        // the side the viewer stands on, and a raw offset put the wall in front of the words.
        var backdrop = stand.AddSlot("Backdrop");
        backdrop.LocalPosition.Value = TowardSpawn(stand, new float3(0f, 1.7f, 0f), -0.08f);
        var backdropMesh = backdrop.AttachComponent<QuadMesh>();
        backdropMesh.Size.Value = new float2(1.5f, 0.7f);
        backdropMesh.DualSided.Value = true;
        var backdropMaterial = backdrop.AttachComponent<UnlitMaterial>();
        backdropMaterial.TintColor.Value = new colorHDR(0.92f, 0.90f, 0.84f, 1f);
        backdropMaterial.UseVertexColor.Value = false;
        var backdropRenderer = backdrop.AttachComponent<MeshRenderer>();
        backdropRenderer.Mesh.Target = backdropMesh;
        backdropRenderer.Material.Target = backdropMaterial;
        FaceSpawn(backdrop);
    }

    // The same paragraph the canvas panel wraps for itself, next to it, in world text. Every break in it
    // is typed, because that is the only kind of break a TextRenderer has - it takes a string, a size, a
    // line spacing and an outline, and nothing in it measures a rect. Standing the two side by side is
    // how you tell which one you actually want. -xlinka
    private static void CreateWorldParagraph(Slot parent, float2 cell)
    {
        var stand = parent.AddSlot("World Paragraph");
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);
        Post(stand, float2.Zero, 1.0f, 0.05f);
        Sign(stand, "Hard-wrapped paragraph (TextRenderer)\nworld text has no wrapper, so the breaks are typed",
            TowardSpawn(stand, new float3(0f, 2.35f, 0f), 0.4f), 0.07f);

        var paragraph = WorldTextSample(stand,
            "World text takes a string, a size and an\n" +
            "outline. It does not measure a rect, so it\n" +
            "cannot decide where a line ends: every\n" +
            "break here was typed into the string.\n" +
            "Line spacing is the one knob that changes\n" +
            "how the block reads once it is written.",
            new float3(0f, 1.75f, 0f), 0.075f);
        paragraph.LineSpacing.Value = 1.15f;
        paragraph.HorizontalAlign.Value = TextHorizontalAlignment.Left;
    }

    private static TextRenderer WorldTextSample(Slot parent, string content, float3 localPosition, float size)
    {
        var slot = parent.AddSlot("Text " + content);
        slot.LocalPosition.Value = localPosition;
        FaceSpawn(slot);

        var text = slot.AttachComponent<TextRenderer>();
        text.Text.Value = content;
        text.Size.Value = size;
        text.Color.Value = new color(0.95f, 0.97f, 1f, 1f);
        text.Font.Target = SharedFont(parent.World);
        text.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0.9f);
        text.OutlineThickness.Value = 1.05f;
        return text;
    }

    // The canvas half: inline tags and a paragraph that wraps itself, neither of which the world-space
    // renderer does.
    private static void CreateCanvasTextPanel(World world, Slot parent, float2 cell)
    {
        var stand = parent.AddSlot("Canvas Text");
        stand.LocalPosition.Value = new float3(cell.x, 0f, cell.y);
        Post(stand, float2.Zero, 1.0f, 0.05f);
        Sign(stand, "Rich text and word wrap (Helio Text)\ntags and wrapping are canvas features, not world text",
            TowardSpawn(stand, new float3(0f, 2.35f, 0f), 0.4f), 0.07f);

        var host = stand.AddSlot("Panel");
        host.LocalPosition.Value = new float3(0f, 1.7f, 0f);
        host.LocalScale.Value = new float3(0.0016f, 0.0016f, 0.0016f);
        FaceSpawn(host);

        var font = SharedFont(world);

        var rect = host.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-320f, -190f);
        rect.OffsetMax.Value = new float2(320f, 190f);
        host.AttachComponent<Canvas>();

        var background = host.AttachComponent<Helio.UI.Image>();
        background.Tint.Value = new color(0.04f, 0.05f, 0.07f, 0.94f);

        var builder = new UIBuilder(host);
        builder.Font(font);
        var layout = builder.VerticalLayout(8f, 14f);
        layout.ForceExpandHeight.Value = false;
        FillRect(layout.RectTransform!);

        var tagged = builder.Text(
            "<b>bold</b> <i>italic</i> <u>underline</u> <s>strike</s>\n" +
            "<color=#ffb000>coloured</color> <size=26>bigger</size> <mark>marked</mark>\n" +
            "x<sup>2</sup> and H<sub>2</sub>O, <smallcaps>small caps</smallcaps>",
            18f, new color(0.92f, 0.96f, 1f, 1f));
        tagged.RichText.Value = true;
        tagged.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        tagged.VerticalAlignment.Value = TextVerticalAlignment.Top;
        FillRect(tagged.RectTransform!);
        SetLayoutHeight(tagged.RectTransform!, 110f, 120f);

        var paragraph = builder.Text(
            "This paragraph is one long line in the source and the layout is what breaks it. Word wrap " +
            "measures each word against the rect it was given and moves to the next line when the next " +
            "word would not fit, so the same string reflows when the panel is resized instead of keeping " +
            "the line breaks somebody typed once at some other width.",
            15f, new color(0.80f, 0.86f, 0.94f, 1f));
        paragraph.WordWrap.Value = true;
        paragraph.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        paragraph.VerticalAlignment.Value = TextVerticalAlignment.Top;
        FillRect(paragraph.RectTransform!);
        SetLayoutHeight(paragraph.RectTransform!, 180f, 200f, 1f);

        builder.NestOut();
    }

    // Local-space step toward spawn, for standing a label clear of the thing it names.
    private static float3 TowardSpawn(Slot parent, float3 localPosition, float distance)
    {
        var global = parent.LocalPointToGlobal(localPosition);
        var toSpawn = parent.GlobalDirectionToLocal(new float3(-global.x, 0f, -global.z));
        toSpawn.y = 0f;
        return toSpawn.LengthSquared > 1e-6f ? localPosition + toSpawn.Normalized * distance : localPosition;
    }
}
