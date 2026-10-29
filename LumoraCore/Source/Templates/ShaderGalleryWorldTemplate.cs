// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Utility;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Core.Scripting;

namespace Lumora.Core.Templates;

// A gallery of shaders made in the node editor, under a sky that is one too. A living planet hangs over
// the middle of a round plaza with its moon going round it and its graph standing open behind it; the
// other exhibits stand round the ring, each beside the orb that holds its graph.
//
// Only the planet's graph is open when the world is made. An open graph keeps a card and a compiled
// preview per node, and nineteen open at once is a thousand previews compiling at load; closed, a graph is
// packed in place with its slot on, draws nothing, its material still follows it, and the Loom tool opens it
// over its orb. -xlinka
//
// Compass from spawn, which looks north across the plaza at the planet:
//   left (west) arc     energy shield, lava lake, hologram, ocean, galaxy, rose window
//   right (east) arc    gas giant, dissolve, portal, marble, soap film, campfire
// The lit exhibits stand on the east arc, facing the low western sun; what glows by itself stands on the
// west arc with the sun behind it.
//   behind spawn        the sky's own orb
internal sealed class ShaderGalleryWorldTemplate : WorldTemplateDefinition
{
    private const string FontPath = "res://Assets/Fonts/FiraCode/FiraCode-SemiBold.ttf";

    private static readonly float3 Centre = new(0f, 0f, -14f);
    private const float RingRadius = 14f;

    private const float PlanetRadius = 2.5f;
    private const float PlanetHeight = 5f;
    private const float AtmosphereRatio = 1.045f;
    private const float MoonOrbit = 5.6f;
    private const float PlinthHeight = 0.95f;
    private const float SignViewDistance = 40f;

    // Twilight: the sun low in the west-south-west, off spawn's left, so from spawn the planet is half day
    // and half night with its cities lit along the right.
    private static readonly floatQ SunRotation =
        floatQ.AxisAngleRad(float3.Up, Radians(-72f)) * floatQ.AxisAngleRad(float3.Right, Radians(-14f));

    public ShaderGalleryWorldTemplate() : base("Shaders") { }

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;

    // The plaza first, then the sky, the planet and each bay a frame apart.
    protected override IEnumerable<Action> Steps(World world)
    {
        // Where the light comes from, for every graph that lights something itself.
        var toSun = -(SunRotation * float3.Backward);
        Shared shared = null!;

        yield return () =>
        {
            shared = new Shared(world);
            CreateSpawn(world, shared);
            CreateLight(world, toSun);
            CreateGround(world);
        };
        yield return () => CreateSky(world, shared, toSun);
        yield return () => CreatePlanet(world, shared, toSun);

        yield return () => Shield(new Exhibit(world, shared, "Energy Shield", -54f));
        yield return () => LavaLake(new Exhibit(world, shared, "Lava Lake", -75.2f));
        yield return () => Hologram(new Exhibit(world, shared, "Hologram", -96.4f));
        yield return () => Ocean(new Exhibit(world, shared, "Ocean", -117.6f));
        yield return () => Galaxy(new Exhibit(world, shared, "Spiral Galaxy", -138.8f));
        yield return () => RoseWindow(new Exhibit(world, shared, "Rose Window", -160f));
        yield return () => GasGiant(new Exhibit(world, shared, "Gas Giant", 54f));
        yield return () => Dissolve(new Exhibit(world, shared, "Dissolve", 75.2f));
        yield return () => Portal(new Exhibit(world, shared, "Portal", 96.4f));
        yield return () => Marble(new Exhibit(world, shared, "Marble", 117.6f));
        yield return () => SoapFilm(new Exhibit(world, shared, "Soap Film", 138.8f));
        yield return () => Campfire(new Exhibit(world, shared, "Campfire", 160f));
    }

    // ---- the place ----

    private static void CreateSpawn(World world, Shared shared)
    {
        var spawn = world.RootSlot.AddSlot("SpawnArea");
        spawn.Tag.Value = "spawn";
        spawn.AttachComponent<SimpleUserSpawn>();
        var area = spawn.AttachComponent<CommonSpawnArea>();
        var points = spawn.AttachComponent<CirclePointGenerator>();
        points.Radius.Value = 1.2f;
        area.SpawnPointGenerator.Target = points;
        spawn.AddSlot("Visual").AttachComponent<GlowCircle>()
            .Setup(1.5f, 0.05f, new colorHDR(0.35f, 0.45f, 0.75f, 0.08f), new colorHDR(0.45f, 0.6f, 1f, 0.4f));

        // Off to the left, clear of the view across the plaza to the planet.
        Sign(spawn, shared, "Shader Gallery", new float3(-2.3f, 1.95f, -1.4f), 0.16f);
        Sign(spawn, shared, "every surface here is a shader graph\nLoom tool on an orb: Edit shader graph",
            new float3(-2.3f, 1.68f, -1.4f), 0.065f);
    }

    private static void CreateLight(World world, float3 toSun)
    {
        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = SunRotation;
        var sun = lightSlot.AttachComponent<Light>();
        sun.Type.Value = LightType.Directional;
        sun.LightColor.Value = new color(1f, 0.72f, 0.5f, 1f);
        sun.Intensity.Value = 1.7f;
        sun.Shadows.Value = ShadowType.Soft;
        sun.ShadowMaxDistance.Value = 45f;
        sun.ShadowSplits.Value = ShadowSplitMode.Two;

        // The dome hides this sky; it is here for the light it lends, matched to the dome's colours.
        var skybox = world.RootSlot.AddSlot("GradientSkybox").AttachComponent<GradientSkybox>();
        skybox.TopColor.Value = new color(0.03f, 0.04f, 0.09f, 1f);
        skybox.HorizonColor.Value = new color(0.2f, 0.18f, 0.3f, 1f);
        skybox.BottomColor.Value = new color(0.06f, 0.06f, 0.08f, 1f);
        skybox.SunColor.Value = new color(1f, 0.6f, 0.35f, 1f);
        skybox.SunDirection.Value = toSun;
        skybox.SunSize.Value = 0.02f;
        skybox.SunIntensity.Value = 0.8f;
        skybox.SunGlowPower.Value = 16f;
        skybox.AmbientEnergy.Value = 0.6f;
    }

    // Walkable top at y=0, both halves agreeing on it.
    private static void CreateGround(World world)
    {
        const float thickness = 0.1f;
        var ground = world.RootSlot.AddSlot("Ground");
        ground.LocalPosition.Value = new float3(0f, -thickness * 0.5f, 0f);
        ground.Tag.Value = "floor";
        var mesh = ground.AttachComponent<BoxMesh>();
        mesh.Size.Value = new float3(300f, thickness, 300f);
        mesh.UVScale.Value = new float3(300f, 1f, 300f);

        // Near black so everything that glows reads, the far floor fading into the dome's horizon.
        var material = ground.AttachComponent<GridSpaceGroundMaterial>();
        material.BaseNearColor.Value = new colorHDR(0.035f, 0.04f, 0.055f, 1f);
        material.BaseFarColor.Value = new colorHDR(0.03f, 0.035f, 0.05f, 1f);
        material.MinorLineColor.Value = new colorHDR(0.07f, 0.08f, 0.11f, 1f);
        material.MajorLineColor.Value = new colorHDR(0.15f, 0.17f, 0.26f, 1f);
        material.MinorScale.Value = 1f;
        material.MajorScale.Value = 5f;
        material.LightResponse.Value = 1.3f;
        material.SheenColor.Value = new colorHDR(0.45f, 0.5f, 0.7f, 1f);
        material.SheenStrength.Value = 0.05f;
        material.HorizonColor.Value = new colorHDR(0.11f, 0.12f, 0.22f, 1f);
        material.HorizonStart.Value = 20f;
        material.HorizonEnd.Value = 120f;

        var renderer = ground.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        var collider = ground.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Type.Value = ColliderType.Static;
        collider.Size.Value = mesh.Size.Value;
    }

    // The sky is a dome turned a quarter round its north-south axis: its UV poles lie on the east and west
    // horizon, where the haze hides how a sphere's UV pinches, and its seam runs under the ground.
    private static void CreateSky(World world, Shared shared, float3 toSun)
    {
        var exhibit = new Exhibit(world, shared, "Night Sky", 180f, RingRadius + 4.5f);
        var sky = exhibit.Material("Night Sky", new float3(0f, 0f, 1.2f), g => ShaderGalleryGraphs.Sky(g, toSun));

        var dome = world.RootSlot.AddSlot("Sky Dome");
        dome.LocalPosition.Value = Centre;
        dome.LocalRotation.Value = floatQ.AxisAngleRad(new float3(0f, 0f, 1f), Radians(-90f));
        var mesh = dome.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 140f;
        mesh.Segments.Value = 96;
        mesh.Rings.Value = 48;
        Render(dome, mesh, sky, shadows: false);
    }

    // ---- the living planet ----

    private static void CreatePlanet(World world, Shared shared, float3 toSun)
    {
        var exhibit = new Exhibit(world, shared, "Living Planet", Centre, floatQ.Identity);
        var root = exhibit.Root;
        root.AddSlot("Pad").AttachComponent<GlowCircle>()
            .Setup(3.4f, 0.06f, new colorHDR(0.25f, 0.45f, 0.9f, 0.05f), new colorHDR(0.35f, 0.6f, 1f, 0.45f));

        var atmosphere = exhibit.Material("Atmosphere", new float3(-0.95f, 0f, 4.2f), g => ShaderGalleryGraphs.Atmosphere(g, toSun, AtmosphereRatio));
        var surface = exhibit.Material("Living Planet", new float3(0f, 0f, 4.2f), g => ShaderGalleryGraphs.Planet(g, toSun), open: true);
        var moon = exhibit.Material("Moon", new float3(0.95f, 0f, 4.2f), ShaderGalleryGraphs.Moon);

        // Unturned, so its own space is the world's and its Sun Direction holds.
        var planet = Sphere(root, "Planet", new float3(0f, PlanetHeight, 0f), PlanetRadius, 160, 80, surface);
        var shell = Sphere(planet, "Atmosphere", float3.Zero, PlanetRadius * AtmosphereRatio, 128, 64, atmosphere);
        shell.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;

        var orbit = planet.AddSlot("Moon Orbit");
        orbit.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(14f));
        orbit.AttachComponent<Rotator>().Speed.Value = 5f;
        Sphere(orbit, "Moon", new float3(MoonOrbit, 0f, 0f), 0.55f, 64, 32, moon);

        // The open graph stands behind the planet, facing spawn across the plaza.
        exhibit.StandOpenGraphs(-6.6f);
    }

    // ---- round the ring, west ----

    private static void Shield(Exhibit exhibit)
    {
        const float radius = 1.4f;
        var shield = exhibit.Material("Energy Shield", new float3(2.0f, 0f, 2.3f), g => ShaderGalleryGraphs.Shield(g, radius));
        var root = exhibit.Root;
        var dome = Sphere(root, "Shield", float3.Zero, radius, 96, 48, shield);
        dome.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;

        // What it guards.
        Cylinder(root, "Pedestal", new float3(0f, 0.25f, 0f), 0.13f, 0.5f, exhibit.Shared.Iron);
        var crystal = root.AddSlot("Crystal");
        crystal.LocalPosition.Value = new float3(0f, 0.78f, 0f);
        var ico = crystal.AttachComponent<IcoSphereMesh>();
        ico.Radius.Value = 0.22f;
        ico.Subdivisions.Value = 0;
        ico.FlatShading.Value = true;
        Render(crystal, ico, exhibit.Shared.Crystal);
        crystal.AttachComponent<Rotator>().Speed.Value = 30f;
        Lamp(root, new float3(0f, 1.0f, 0f), new color(0.3f, 0.6f, 1f, 1f), 0.9f, 4.5f);
    }

    private static void LavaLake(Exhibit exhibit)
    {
        var lava = exhibit.Material("Lava Lake", new float3(2.0f, 0f, 2.3f), ShaderGalleryGraphs.Lava);
        Pool(exhibit, "Lava", 3.0f, 96, lava);
        Lamp(exhibit.Root, new float3(0f, 1.1f, 0f), new color(1f, 0.42f, 0.14f, 1f), 1.6f, 5.5f);
    }

    private static void Hologram(Exhibit exhibit)
    {
        const float beamHeight = 1.0f;
        const float lensTop = 0.245f;
        var beam = exhibit.Material("Projector Beam", new float3(-0.9f, 0f, 1.7f), g => ShaderGalleryGraphs.Beam(g, beamHeight));
        var globe = exhibit.Material("Hologram", new float3(0.9f, 0f, 1.7f), ShaderGalleryGraphs.Hologram);

        var root = exhibit.Root;
        var projector = Cylinder(root, "Projector", new float3(0f, lensTop * 0.5f, 0f), 0.32f, lensTop - 0.005f, exhibit.Shared.Iron);
        SolidCylinder(projector, 0.32f, lensTop);
        var lens = Disc(root, "Lens", new float3(0f, lensTop, 0f), 0.24f, exhibit.Shared.Lens);
        lens.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(90f));

        var cone = root.AddSlot("Beam");
        cone.LocalPosition.Value = new float3(0f, lensTop + beamHeight * 0.5f, 0f);
        var coneMesh = cone.AttachComponent<ConeMesh>();
        coneMesh.RadiusBase.Value = 0.2f;
        coneMesh.RadiusTop.Value = 0.5f;
        coneMesh.Height.Value = beamHeight;
        coneMesh.Segments.Value = 48;
        Render(cone, coneMesh, beam, shadows: false);

        var sphere = Sphere(root, "Globe", new float3(0f, lensTop + beamHeight + 0.5f, 0f), 0.55f, 64, 32, globe);
        sphere.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;
        sphere.AttachComponent<Rotator>().Speed.Value = 18f;
        Lamp(root, sphere.LocalPosition.Value, new color(0.35f, 0.8f, 1f, 1f), 0.8f, 3.5f);
    }

    private static void Ocean(Exhibit exhibit)
    {
        var ocean = exhibit.Material("Ocean", new float3(2.0f, 0f, 2.4f), ShaderGalleryGraphs.Ocean);
        Pool(exhibit, "Water", 3.2f, 128, ocean);
    }

    private static void Galaxy(Exhibit exhibit)
    {
        var galaxy = exhibit.Material("Spiral Galaxy", new float3(1.6f, 0f, 1.9f), ShaderGalleryGraphs.Galaxy);
        // Nearly flat overhead, a little tipped toward the plaza: walk under it and it is face on.
        var holder = exhibit.Root.AddSlot("Spiral Galaxy");
        holder.LocalPosition.Value = new float3(0f, 3.0f, -0.5f);
        holder.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(80f));
        var disc = Disc(holder, "Disc", float3.Zero, 1.7f, galaxy);
        disc.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;
        var spin = disc.AttachComponent<Rotator>();
        spin.Axis.Value = new float3(0f, 0f, 1f);
        spin.Speed.Value = 4f;
    }

    private static void RoseWindow(Exhibit exhibit)
    {
        var glass = exhibit.Material("Rose Window", new float3(1.7f, 0f, 1.5f), ShaderGalleryGraphs.RoseWindow);
        var root = exhibit.Root;
        var window = Disc(root, "Window", new float3(0f, 2.15f, -1f), 1.25f, glass);
        window.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;
        var tracery = Torus(root, "Tracery", window.LocalPosition.Value, 1.28f, 0.07f, exhibit.Shared.Stone);
        tracery.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(90f));
        var sill = Box(root, "Sill", new float3(0f, 0.42f, -1f), new float3(2.9f, 0.84f, 0.45f), exhibit.Shared.Stone);
        SolidBox(sill, new float3(2.9f, 0.84f, 0.45f));
    }

    // ---- round the ring, east ----

    private static void GasGiant(Exhibit exhibit)
    {
        var giant = exhibit.Material("Gas Giant", new float3(-0.8f, 0f, 1.9f), ShaderGalleryGraphs.GasGiant);
        var rings = exhibit.Material("Rings", new float3(0.8f, 0f, 1.9f), g => ShaderGalleryGraphs.Rings(g, 0.63));

        var tilt = exhibit.Root.AddSlot("Gas Giant");
        tilt.LocalPosition.Value = new float3(0f, 2.75f, -0.6f);
        // Tipped toward the plaza: from eye height a ring plane nearly level with the eye is a line.
        tilt.LocalRotation.Value = floatQ.AxisAngleRad(new float3(0f, 0f, 1f), Radians(14f)) * floatQ.AxisAngleRad(float3.Right, Radians(36f));
        var body = Sphere(tilt, "Planet", float3.Zero, 1.05f, 128, 64, giant);
        body.AttachComponent<Rotator>().Speed.Value = 9f;
        var disc = Disc(tilt, "Rings", float3.Zero, 2.1f, rings);
        disc.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(90f));
        disc.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    private static void Dissolve(Exhibit exhibit)
    {
        var dissolve = exhibit.Material("Dissolve", new float3(1.6f, 0f, 1.7f), ShaderGalleryGraphs.Dissolve);
        var holder = exhibit.Root.AddSlot("Dissolve");
        holder.LocalPosition.Value = new float3(0f, 1.75f, -0.3f);
        holder.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(70f));
        var ring = Torus(holder, "Torus", float3.Zero, 0.55f, 0.2f, dissolve);
        ring.AttachComponent<Rotator>().Speed.Value = 22f;
        exhibit.Root.AddSlot("Pad").AttachComponent<GlowCircle>()
            .Setup(0.9f, 0.04f, new colorHDR(1f, 0.45f, 0.1f, 0.05f), new colorHDR(1f, 0.55f, 0.2f, 0.35f));
    }

    private static void Portal(Exhibit exhibit)
    {
        var vortex = exhibit.Material("Portal", new float3(-1.75f, 0f, 1.5f), ShaderGalleryGraphs.Portal);
        var runes = exhibit.Material("Rune Ring", new float3(1.75f, 0f, 1.5f), ShaderGalleryGraphs.RuneRing);
        var gate = exhibit.Root.AddSlot("Portal");
        gate.LocalPosition.Value = new float3(0f, 1.62f, -0.6f);
        var disc = Disc(gate, "Vortex", float3.Zero, 1.15f, vortex);
        disc.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;
        var ring = Torus(gate, "Ring", float3.Zero, 1.23f, 0.1f, runes);
        ring.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(90f));
        var step = Box(exhibit.Root, "Step", new float3(0f, 0.12f, -0.6f), new float3(2.2f, 0.24f, 0.7f), exhibit.Shared.Stone);
        SolidBox(step, new float3(2.2f, 0.24f, 0.7f));
        Lamp(exhibit.Root, new float3(0f, 1.62f, 0.2f), new color(0.65f, 0.4f, 1f, 1f), 1.1f, 5f);
    }

    private static void Marble(Exhibit exhibit)
    {
        var marble = exhibit.Material("Marble", new float3(2.0f, 0f, 1.9f), ShaderGalleryGraphs.Marble);
        var root = exhibit.Root;
        Cylinder(root, "Pedestal", new float3(-0.8f, 0.5f, -0.4f), 0.3f, 1f, exhibit.Shared.Plinth);
        var sphere = Sphere(root, "Sphere", new float3(-0.8f, 1.55f, -0.4f), 0.5f, 96, 48, marble);
        sphere.AttachComponent<Rotator>().Speed.Value = 8f;
        Cylinder(root, "Pedestal", new float3(0.85f, 0.5f, -0.4f), 0.3f, 1f, exhibit.Shared.Plinth);
        var holder = root.AddSlot("Cube");
        holder.LocalPosition.Value = new float3(0.85f, 1.62f, -0.4f);
        holder.AttachComponent<Rotator>().Speed.Value = 10f;
        var cube = Box(holder, "Cube", float3.Zero, new float3(0.62f, 0.62f, 0.62f), marble);
        cube.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(35.26f)) * floatQ.AxisAngleRad(new float3(0f, 0f, 1f), Radians(45f));
    }

    private static void SoapFilm(Exhibit exhibit)
    {
        var film = exhibit.Material("Soap Film", new float3(1.7f, 0f, 1.9f), ShaderGalleryGraphs.Bubble);
        var bubbles = new (float3 At, float Radius)[]
        {
            (new float3(-0.9f, 1.45f, 0.2f), 0.32f),
            (new float3(0.35f, 2.15f, -0.3f), 0.42f),
            (new float3(0.95f, 1.2f, 0.6f), 0.22f),
            (new float3(-0.25f, 2.95f, -0.9f), 0.28f),
            (new float3(-1.2f, 2.45f, -0.6f), 0.18f),
            (new float3(0.1f, 1.05f, -0.2f), 0.14f),
        };
        int index = 0;
        foreach (var (at, radius) in bubbles)
        {
            var bubble = Sphere(exhibit.Root, "Bubble", at, radius, 48, 24, film);
            bubble.GetComponent<MeshRenderer>()!.ShadowCastMode.Value = ShadowCastMode.Off;
            var spin = bubble.AttachComponent<Rotator>();
            spin.Speed.Value = 6f + 3f * index++;
        }
    }

    private static void Campfire(Exhibit exhibit)
    {
        var fire = exhibit.Material("Campfire", new float3(1.6f, 0f, 1.7f), ShaderGalleryGraphs.Flame);
        var camp = exhibit.Root.AddSlot("Campfire");
        for (int i = 0; i < 9; i++)
        {
            float a = i * MathF.PI * 2f / 9f;
            var stone = camp.AddSlot("Stone");
            stone.LocalPosition.Value = new float3(MathF.Cos(a) * 0.55f, 0.07f, MathF.Sin(a) * 0.55f);
            stone.LocalScale.Value = new float3(1.2f, 0.75f, 1f);
            var ico = stone.AttachComponent<IcoSphereMesh>();
            ico.Radius.Value = 0.12f;
            ico.Subdivisions.Value = 1;
            ico.FlatShading.Value = true;
            Render(stone, ico, exhibit.Shared.Stone);
        }
        for (int i = 0; i < 3; i++)
        {
            var log = Cylinder(camp, "Log", new float3(0f, 0.09f, 0f), 0.065f, 0.85f, exhibit.Shared.Wood);
            log.LocalRotation.Value = floatQ.AxisAngleRad(float3.Up, Radians(i * 60f)) * floatQ.AxisAngleRad(float3.Right, Radians(80f));
        }
        for (int i = 0; i < 3; i++)
        {
            var card = camp.AddSlot("Flame");
            card.LocalPosition.Value = new float3(0f, 0.66f, 0f);
            card.LocalRotation.Value = floatQ.AxisAngleRad(float3.Up, Radians(i * 60f));
            var quad = card.AttachComponent<QuadMesh>();
            quad.Size.Value = new float2(0.95f, 1.3f);
            Render(card, quad, fire, shadows: false);
        }
        Lamp(camp, new float3(0f, 0.75f, 0f), new color(1f, 0.55f, 0.25f, 1f), 2.2f, 7f);
    }

    // ---- shared pieces ----

    // Materials and the font every exhibit draws on, one of each in the world.
    private sealed class Shared
    {
        public readonly FontProvider Font;
        public readonly PBS_Metallic Plinth;
        public readonly PBS_Metallic Stone;
        public readonly PBS_Metallic Iron;
        public readonly PBS_Metallic Wood;
        public readonly PBS_Metallic Lens;
        public readonly PBS_Metallic Crystal;

        public Shared(World world)
        {
            var slot = world.RootSlot.AddSlot("Shared Assets");
            Font = slot.AttachComponent<FontProvider>();
            Font.URL.Value = new Uri(FontPath);
            Font.FallbackURLs.Add(new Uri(FontPath));
            Plinth = Pbs(slot, new colorHDR(0.11f, 0.12f, 0.15f, 1f), 0.6f, 0.62f);
            Stone = Pbs(slot, new colorHDR(0.36f, 0.35f, 0.33f, 1f), 0f, 0.22f);
            Iron = Pbs(slot, new colorHDR(0.14f, 0.14f, 0.16f, 1f), 0.8f, 0.55f);
            Wood = Pbs(slot, new colorHDR(0.24f, 0.14f, 0.08f, 1f), 0f, 0.18f);
            Lens = Pbs(slot, new colorHDR(0.05f, 0.1f, 0.12f, 1f), 0f, 0.8f, new colorHDR(0.3f, 0.85f, 1f, 1f));
            Crystal = Pbs(slot, new colorHDR(0.5f, 0.75f, 1f, 1f), 0.2f, 0.9f, new colorHDR(0.25f, 0.55f, 1f, 1f));
        }

        private static PBS_Metallic Pbs(Slot slot, colorHDR albedo, float metallic, float smoothness, colorHDR? emissive = null)
        {
            var material = slot.AttachComponent<PBS_Metallic>();
            material.AlbedoColor.Value = albedo;
            material.Metallic.Value = metallic;
            material.Smoothness.Value = smoothness;
            if (emissive is { } glow)
                material.EmissiveColor.Value = glow;
            return material;
        }
    }

    // One stop on the ring. Its root faces the middle, so everything in it is placed in the stop's own
    // terms: +Z toward the middle, +X to the right as the middle sees it.
    private sealed class Exhibit
    {
        public readonly Slot Root;
        public readonly Shared Shared;
        private readonly List<(ShaderGraphSource Source, ShaderGraphBuilder.Bounds Bounds, Slot Orb, string Title, int Nodes)> _open = new();

        public Exhibit(World world, Shared shared, string name, float degrees, float radius = RingRadius)
            : this(world, shared, name, RingPoint(degrees, radius), FacingMiddle(RingPoint(degrees, radius)))
        {
        }

        public Exhibit(World world, Shared shared, string name, float3 at, floatQ rotation)
        {
            Shared = shared;
            Root = world.RootSlot.AddSlot(name);
            Root.LocalPosition.Value = at;
            Root.LocalRotation.Value = rotation;
        }

        private static float3 RingPoint(float degrees, float radius)
        {
            float a = Radians(degrees);
            return Centre + new float3(MathF.Sin(a), 0f, -MathF.Cos(a)) * radius;
        }

        private static floatQ FacingMiddle(float3 at)
        {
            var toward = Centre - at;
            return floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toward.x, toward.z));
        }

        // A material orb on a plinth, its graph built by the recipe and laid out so that opening it puts the
        // Material Output over the orb. Its name and size are on the plinth.
        public CustomShaderMaterial Material(string title, float3 plinth, Action<ShaderGraphBuilder> recipe, bool open = false)
        {
            var post = Cylinder(Root, "Plinth", new float3(plinth.x, PlinthHeight * 0.5f, plinth.z), 0.16f, PlinthHeight, Shared.Plinth);
            SolidCylinder(post, 0.16f, PlinthHeight);

            var orb = Root.AddSlot(title);
            orb.LocalPosition.Value = new float3(plinth.x, PlinthHeight + ShaderGraphMaterials.OrbRadius + 0.01f, plinth.z);
            var source = ShaderGraphMaterials.BuildOrb(orb, open);
            orb.Name.Value = title;
            var graph = new ShaderGraphBuilder(source);
            recipe(graph);
            foreach (var problem in graph.Problems)
                Logger.Warn($"Shader gallery: {title}: {problem}");
            var bounds = graph.Layout();
            if (open)
                _open.Add((source, bounds, orb, title, graph.NodeCount));

            Sign(Root, Shared, $"{title}\n{graph.NodeCount} nodes", new float3(plinth.x, PlinthHeight + 0.42f, plinth.z), 0.06f);
            return source.Material.Target!;
        }

        // The open graphs side by side along a line behind the stop, the first on the left, each named above.
        public void StandOpenGraphs(float back)
        {
            const float floor = 0.45f;
            const float gap = 0.5f;
            float total = -gap;
            foreach (var graph in _open)
                total += graph.Bounds.Width + gap;
            float x = -total * 0.5f;
            foreach (var (source, bounds, orb, title, nodes) in _open)
            {
                var space = source.Graph.Target!.Slot;
                var at = new float3(x - bounds.Left, floor - bounds.Bottom, back);
                space.LocalPosition.Value = at - orb.LocalPosition.Value;
                space.LocalRotation.Value = floatQ.Identity;
                Sign(Root, Shared, $"{title}\n{nodes} nodes, open", new float3(x + bounds.Width * 0.5f, floor + bounds.Height + 0.3f, back), 0.14f);
                x += bounds.Width + gap;
            }
        }
    }

    private static TextRenderer Sign(Slot parent, Shared shared, string text, float3 at, float size)
    {
        var slot = parent.AddSlot("Sign");
        slot.LocalPosition.Value = at;
        var label = slot.AttachComponent<TextRenderer>();
        label.Text.Value = text;
        label.Size.Value = size;
        label.Color.Value = new color(0.93f, 0.96f, 1f, 1f);
        label.Font.Target = shared.Font;
        label.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0.9f);
        label.OutlineThickness.Value = 1.05f;
        label.MaxViewDistance.Value = SignViewDistance;
        slot.AttachComponent<FaceLocalUser>();
        return label;
    }

    private static void Render(Slot slot, Component mesh, IAssetProvider<MaterialAsset> material, bool shadows = true)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = shadows ? ShadowCastMode.On : ShadowCastMode.Off;
    }

    private static Slot Sphere(Slot parent, string name, float3 at, float radius, int segments, int rings, IAssetProvider<MaterialAsset> material)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = at;
        var mesh = slot.AttachComponent<SphereMesh>();
        mesh.Radius.Value = radius;
        mesh.Segments.Value = segments;
        mesh.Rings.Value = rings;
        Render(slot, mesh, material);
        return slot;
    }

    // A flat round disc, facing along its slot's Z.
    private static Slot Disc(Slot parent, string name, float3 at, float radius, IAssetProvider<MaterialAsset> material)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = at;
        var mesh = slot.AttachComponent<CircleMesh>();
        mesh.Radius.Value = radius;
        mesh.Segments.Value = 128;
        Render(slot, mesh, material);
        return slot;
    }

    // Lying in its slot's XZ plane, like a ring on a table.
    private static Slot Torus(Slot parent, string name, float3 at, float major, float minor, IAssetProvider<MaterialAsset> material)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = at;
        var mesh = slot.AttachComponent<TorusMesh>();
        mesh.MajorRadius.Value = major;
        mesh.MinorRadius.Value = minor;
        mesh.MajorSegments.Value = 128;
        mesh.MinorSegments.Value = 32;
        Render(slot, mesh, material);
        return slot;
    }

    private static Slot Cylinder(Slot parent, string name, float3 at, float radius, float height, IAssetProvider<MaterialAsset> material)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = at;
        var mesh = slot.AttachComponent<CylinderMesh>();
        mesh.Radius.Value = radius;
        mesh.Height.Value = height;
        mesh.Segments.Value = 32;
        Render(slot, mesh, material);
        return slot;
    }

    private static Slot Box(Slot parent, string name, float3 at, float3 size, IAssetProvider<MaterialAsset> material)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = at;
        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = size;
        Render(slot, mesh, material);
        return slot;
    }

    // A square pool sunk into a stone kerb, its surface a finely cut grid lying flat for a shader to move.
    private static void Pool(Exhibit exhibit, string name, float size, int segments, IAssetProvider<MaterialAsset> material)
    {
        var surface = exhibit.Root.AddSlot(name);
        surface.LocalPosition.Value = new float3(0f, 0.1f, 0f);
        surface.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, Radians(90f));
        var grid = surface.AttachComponent<GridMesh>();
        grid.Size.Value = new float2(size, size);
        grid.SegmentsX.Value = segments;
        grid.SegmentsY.Value = segments;
        Render(surface, grid, material, shadows: false);

        const float kerb = 0.22f;
        const float height = 0.2f;
        float half = size * 0.5f + kerb * 0.5f;
        float length = size + kerb * 2f;
        foreach (var (at, extent) in new[]
        {
            (new float3(0f, height * 0.5f, -half), new float3(length, height, kerb)),
            (new float3(0f, height * 0.5f, half), new float3(length, height, kerb)),
            (new float3(-half, height * 0.5f, 0f), new float3(kerb, height, size)),
            (new float3(half, height * 0.5f, 0f), new float3(kerb, height, size)),
        })
        {
            var stone = Box(exhibit.Root, "Kerb", at, extent, exhibit.Shared.Stone);
            SolidBox(stone, extent);
        }
    }

    private static void SolidBox(Slot slot, float3 size)
    {
        var collider = slot.AttachComponent<BoxCollider>();
        collider.CharacterCollider.Value = true;
        collider.Size.Value = size;
        collider.Type.Value = ColliderType.Static;
    }

    private static void SolidCylinder(Slot slot, float radius, float height)
    {
        var collider = slot.AttachComponent<CylinderCollider>();
        collider.CharacterCollider.Value = true;
        collider.Radius.Value = radius;
        collider.Height.Value = height;
        collider.Type.Value = ColliderType.Static;
    }

    private static void Lamp(Slot parent, float3 at, color tint, float intensity, float range)
    {
        var slot = parent.AddSlot("Lamp");
        slot.LocalPosition.Value = at;
        var light = slot.AttachComponent<Light>();
        light.Type.Value = LightType.Point;
        light.LightColor.Value = tint;
        light.Intensity.Value = intensity;
        light.Range.Value = range;
        light.Shadows.Value = ShadowType.None;
    }
}
