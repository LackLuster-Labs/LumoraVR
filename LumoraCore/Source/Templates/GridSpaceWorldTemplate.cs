// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Templates;

internal sealed class GridSpaceWorldTemplate : WorldTemplateDefinition
{
    public GridSpaceWorldTemplate() : base("Grid") { }

    protected override void Build(World world)
    {
        var spawnSlot = world.RootSlot.AddSlot("SpawnArea");
        // The ground's top is y=0, the same surface its collider stops things on.
        spawnSlot.LocalPosition.Value = new float3(0f, 0.01f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();
        var spawnArea = spawnSlot.AttachComponent<CommonSpawnArea>();
        var spawnPoints = spawnSlot.AttachComponent<CirclePointGenerator>();
        spawnPoints.Radius.Value = 4f;
        spawnArea.SpawnPointGenerator.Target = spawnPoints;
        // Quiet marker in the floor's own palette: a low lip of a ring and a faint cool pool. The
        // disc and ring are additive, so alpha is the strength. -xlinka
        spawnSlot.AddSlot("Visual").AttachComponent<GlowCircle>()
            .Setup(4f, 0.08f,
                new colorHDR(0.45f, 0.52f, 0.68f, 0.10f),
                new colorHDR(0.42f, 0.52f, 0.72f, 0.42f));

        // Warm morning key light, 35 degrees up and a little behind spawn's right shoulder, so what you face at
        // spawn is lit. It sat 10 degrees up dead ahead before: everything in view was backlit and a white ball
        // in front of spawn read the same with the sun on and off. The slot's -Z is the way the light travels
        // and the sky draws its sun from the same rotation, so the two cannot disagree. -xlinka
        var sunRotation = floatQ.Euler(0.698f, -0.611f, 0f);
        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = sunRotation;
        var dirLight = lightSlot.AttachComponent<Light>();
        dirLight.Type.Value = LightType.Directional;
        dirLight.LightColor.Value = new color(1.00f, 0.78f, 0.55f, 1f);
        // Tuned under the stray bootstrap sun (see LocalHomeWorldTemplate); carries its share now.
        dirLight.Intensity.Value = 1.9f;
        dirLight.Shadows.Value = ShadowType.Soft;

        // Morning sky: deep cool dawn-blue overhead, a wide warm peach/orange band at the horizon, the sun
        // where the directional light comes from. - xlinka
        var skySlot = world.RootSlot.AddSlot("GradientSkybox");
        var skybox = skySlot.AttachComponent<GradientSkybox>();
        skybox.TopColor.Value = new color(0.14f, 0.18f, 0.40f, 1f);
        skybox.HorizonColor.Value = new color(1.00f, 0.60f, 0.40f, 1f);
        skybox.BottomColor.Value = new color(0.95f, 0.55f, 0.42f, 1f);
        skybox.SunColor.Value = new color(1.00f, 0.78f, 0.52f, 1f);
        skybox.SunDirection.Value = sunRotation * float3.Forward;
        skybox.SunSize.Value = 0.045f;
        skybox.SunIntensity.Value = 2.6f;
        skybox.SunGlowPower.Value = 48f;
        skybox.AmbientEnergy.Value = 0.70f;

        // The box is centred on its slot, so the slot goes down half the thickness and the mesh and its
        // collider share one top at y=0. Shifting only the collider would leave the drawn floor 5 cm above the
        // surface everything stands on, and every dropped object would look sunk into it. -xlinka
        const float groundThickness = 0.1f;
        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, -groundThickness * 0.5f, 0f);
        groundSlot.Tag.Value = "floor";

        // 300 m so the far edge sits past the haze from anywhere near spawn; the grid is world-space
        // so the mesh size and UV scale do not touch its cell size. -xlinka
        var groundMesh = groundSlot.AttachComponent<BoxMesh>();
        groundMesh.Size.Value = new float3(300f, groundThickness, 300f);
        groundMesh.UVScale.Value = new float3(300f, 1f, 300f);

        // Cool slate floor under a warm sky. The floor lights itself (ambient is off in its shader),
        // so these are the colours you get; the sun only adds warmth and shadow on top. HorizonColor
        // is this sky's colour just under its horizon line (0.81 horizon + 0.19 bottom), so the far
        // floor dissolves into the sky instead of meeting it at a seam. -xlinka
        var groundMaterial = groundSlot.AttachComponent<GridSpaceGroundMaterial>();
        groundMaterial.BaseNearColor.Value = new colorHDR(0.110f, 0.120f, 0.145f, 1f);
        groundMaterial.BaseFarColor.Value = new colorHDR(0.090f, 0.100f, 0.125f, 1f);
        groundMaterial.MinorLineColor.Value = new colorHDR(0.200f, 0.215f, 0.250f, 1f);
        groundMaterial.MajorLineColor.Value = new colorHDR(0.330f, 0.370f, 0.460f, 1f);
        groundMaterial.MinorScale.Value = 1f;
        groundMaterial.MajorScale.Value = 5f;
        groundMaterial.MinorLineWidth.Value = 1.0f;
        groundMaterial.MajorLineWidth.Value = 1.5f;
        groundMaterial.LightResponse.Value = 1.6f;
        groundMaterial.SheenColor.Value = new colorHDR(0.55f, 0.62f, 0.75f, 1f);
        groundMaterial.SheenStrength.Value = 0.06f;
        groundMaterial.HorizonColor.Value = new colorHDR(0.97f, 0.58f, 0.40f, 1f);
        groundMaterial.HorizonStart.Value = 14f;
        groundMaterial.HorizonEnd.Value = 110f;

        var groundRenderer = groundSlot.AttachComponent<MeshRenderer>();
        groundRenderer.Mesh.Target = groundMesh;
        groundRenderer.Material.Target = groundMaterial;
        groundRenderer.ShadowCastMode.Value = ShadowCastMode.Off;

        var groundCollider = groundSlot.AttachComponent<BoxCollider>();
        groundCollider.CharacterCollider.Value = true;
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Size.Value = groundMesh.Size.Value;

        // Grabbable preview sphere. A satin dielectric with the PBS shader's own fresnel, so it shows
        // the sky and the sun the way a material ball should, instead of wearing the floor's planar
        // grid with a seam. -xlinka
        var orbSlot = world.RootSlot.AddSlot("MaterialOrb");
        orbSlot.LocalPosition.Value = new float3(0.95f, 1.25f, -1.05f);
        orbSlot.AttachComponent<Grabbable>();

        var orbMesh = orbSlot.AttachComponent<SphereMesh>();
        orbMesh.Radius.Value = 0.17f;
        orbMesh.Segments.Value = 32;
        orbMesh.Rings.Value = 20;

        var orbMaterial = orbSlot.AttachComponent<PBS_Metallic>();
        orbMaterial.AlbedoColor.Value = new colorHDR(0.66f, 0.70f, 0.76f, 1f);
        orbMaterial.Metallic.Value = 0f;
        orbMaterial.Smoothness.Value = 0.6f;

        var orbRenderer = orbSlot.AttachComponent<MeshRenderer>();
        orbRenderer.Mesh.Target = orbMesh;
        orbRenderer.Material.Target = orbMaterial;

        // Stand-height glass beside the spawn circle, nothing drawn over it.
        Mirror.Spawn(world.RootSlot, new float3(0f, 1.65f, 4.7f),
            float3.Backward, new float2(2.6f, 2.2f));
        var probeSlot = world.RootSlot.AddSlot("Spawn reflections");
        probeSlot.LocalPosition.Value = new float3(0f, 1.5f, 0f);
        var probe = probeSlot.AttachComponent<ReflectionProbe>();
        probe.Size.Value = new float3(20f, 8f, 20f);
        probe.MaxDistance.Value = 40f;
        // Captured once and again whenever loading settles, not every frame: Always re-rendered the whole
        // scene six times a frame, avatars and all, for a soft reflection on a matte orb. -xlinka
        probe.UpdateMode.Value = ProbeUpdateMode.Once;
    }
}
