// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Templates;

internal sealed class LocalHomeWorldTemplate : WorldTemplateDefinition
{
    public LocalHomeWorldTemplate() : base("LocalHome") { }

    protected override void Build(World world)
    {
        var spawnSlot = world.RootSlot.AddSlot("SpawnArea");
        spawnSlot.LocalPosition.Value = new float3(0f, 0.01f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();
        var spawnArea = spawnSlot.AttachComponent<CommonSpawnArea>();
        var spawnPoints = spawnSlot.AttachComponent<CirclePointGenerator>();
        spawnPoints.Radius.Value = 2f;
        spawnArea.SpawnPointGenerator.Target = spawnPoints;
        spawnSlot.AddSlot("Visual").AttachComponent<GlowCircle>()
            .Setup(2f, 0.15f,
                new colorHDR(0.85f, 0.83f, 0.78f, 0.3f), // faint white pool (alpha scales additive strength)
                new colorHDR(1.0f, 0.72f, 0.28f, 1f));   // gold ring

        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = SunRotation;
        var dirLight = lightSlot.AttachComponent<Light>();
        dirLight.Type.Value = LightType.Directional;
        dirLight.LightColor.Value = new color(1f, 0.96f, 0.84f, 1f);
        // Tuned while Bootstrap.tscn's own half-energy sun was still shining into every world; that
        // one is off now, so the world's sun carries what it used to add. -xlinka
        dirLight.Intensity.Value = 1.7f;
        dirLight.Shadows.Value = ShadowType.Soft;

        var skySlot = world.RootSlot.AddSlot("GradientSkybox");
        var skybox = skySlot.AttachComponent<GradientSkybox>();
        skybox.TopColor.Value = new color(0.070f, 0.060f, 0.150f, 1f);
        skybox.HorizonColor.Value = new color(0.46f, 0.38f, 0.58f, 1f);
        skybox.BottomColor.Value = new color(0.040f, 0.032f, 0.065f, 1f);
        skybox.SunColor.Value = new color(1.0f, 0.72f, 0.54f, 1f);
        skybox.SunDirection.Value = SkySun;
        skybox.SunSize.Value = 0.034f;
        skybox.SunIntensity.Value = 2.4f;
        skybox.AmbientEnergy.Value = 0.48f;

        const float groundRadius = 25f;
        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        groundSlot.Tag.Value = "floor";

        var groundMesh = groundSlot.AttachComponent<CylinderMesh>();
        groundMesh.Radius.Value = groundRadius;
        float groundHeight = 0.04f;
        groundMesh.Height.Value = groundHeight;
        groundMesh.Segments.Value = 64;
        groundMesh.UVScale.Value = new float2(25f, 25f);

        // Sink the cylinder so its top face lands exactly at y=0 (otherwise spawned
        // users sink half-height into the floor). - xlinka
        groundSlot.LocalPosition.Value = new float3(0f, -groundHeight * 0.5f, 0f);

        var groundMaterial = groundSlot.AttachComponent<MetaballMaterial>();
        groundMaterial.BlendMode.Value = BlendMode.Transparent;
        groundMaterial.Culling.Value = Culling.Back;
        groundMaterial.TintA.Value = new colorHDR(0.90f, 1.00f, 0.94f, 1f);
        groundMaterial.TintB.Value = new colorHDR(1.00f, 0.58f, 0.88f, 1f);
        groundMaterial.WaterDeepColor.Value = new colorHDR(0.040f, 0.030f, 0.060f, 1f);
        groundMaterial.WaterSurfaceColor.Value = new colorHDR(0.10f, 0.085f, 0.13f, 1f);
        groundMaterial.RippleColor.Value = new colorHDR(0.72f, 1.00f, 0.92f, 1f);
        groundMaterial.BlobRadius.Value = 0.12f;
        groundMaterial.BlobSmoothness.Value = 0.075f;
        groundMaterial.BlobCount.Value = 72;
        groundMaterial.RiseSpeed.Value = 0.34f;
        // Match risingDiskRadius below so the ripple shader places its centers at
        // the same xz as the rising orbs. Off-by-half-a-meter is enough to make
        // ripples drift visibly from the bubbles. - xlinka
        groundMaterial.VolumeExtents.Value = new float2(24.0f, 24.0f);
        groundMaterial.VolumeHeight.Value = 3.4f;
        groundMaterial.VolumeOffset.Value = new float3(0f, groundHeight * 0.5f, 0f);
        groundMaterial.RimStrength.Value = 1.55f;
        groundMaterial.RimFalloff.Value = 2.8f;
        groundMaterial.FresnelPower.Value = 3.0f;
        groundMaterial.EmissionStrength.Value = 0.82f;
        groundMaterial.WaveStrength.Value = 0.28f;
        groundMaterial.RippleStrength.Value = 0.62f;
        groundMaterial.RippleRadius.Value = 0.58f;
        groundMaterial.RippleWidth.Value = 0.032f;
        groundMaterial.LineStrength.Value = 0.34f;
        groundMaterial.RenderQueue.Value = 10;

        var groundRenderer = groundSlot.AttachComponent<MeshRenderer>();
        groundRenderer.Mesh.Target = groundMesh;
        groundRenderer.Material.Target = groundMaterial;
        groundRenderer.ShadowCastMode.Value = ShadowCastMode.Off;

        // Decoupled from groundMaterial.VolumeHeight: that one positions the floor's
        // ripple-emitter blobs (invisible), this one is how high the visible orbs
        // actually float. Bumping VolumeHeight here without touching the ground
        // shader keeps the floor ripple cadence the same. - xlinka
        const float risingVolumeHeight = 7.0f;
        const float risingVolumeLift = 0.035f;
        // Stay inside groundRadius so polar-distributed blob centers never punch
        // past the cylinder rim. - xlinka
        const float risingDiskRadius = 24.0f;

        var risingBallsSlot = groundSlot.AddSlot("RisingBalls");
        risingBallsSlot.LocalPosition.Value = new float3(0f, groundHeight * 0.5f + risingVolumeHeight * 0.5f + risingVolumeLift, 0f);

        // Real geometry, one surface: a sphere, a neck and a floor ring per bubble, placed by the
        // material's vertex shader. The volume here has to match the material's or the culling box
        // is wrong. -xlinka
        var risingVolumeOffset = new float3(0f, -risingVolumeHeight * 0.5f - risingVolumeLift, 0f);
        var risingBallsMesh = risingBallsSlot.AttachComponent<BubbleFieldMesh>();
        risingBallsMesh.Count.Value = groundMaterial.BlobCount.Value;
        risingBallsMesh.VolumeExtents.Value = new float2(risingDiskRadius, risingDiskRadius);
        risingBallsMesh.VolumeHeight.Value = risingVolumeHeight;
        risingBallsMesh.VolumeOffset.Value = risingVolumeOffset;

        var risingBallsMaterial = risingBallsSlot.AttachComponent<LocalHomeRisingMaterial>();
        risingBallsMaterial.TintA.Value = groundMaterial.TintA.Value;
        risingBallsMaterial.TintB.Value = groundMaterial.TintB.Value;
        risingBallsMaterial.BlobRadius.Value = groundMaterial.BlobRadius.Value;
        risingBallsMaterial.BlobSmoothness.Value = groundMaterial.BlobSmoothness.Value;
        risingBallsMaterial.BlobCount.Value = groundMaterial.BlobCount.Value;
        risingBallsMaterial.RiseSpeed.Value = groundMaterial.RiseSpeed.Value;
        risingBallsMaterial.VolumeExtents.Value = new float2(risingDiskRadius, risingDiskRadius);
        risingBallsMaterial.VolumeHeight.Value = risingVolumeHeight;
        risingBallsMaterial.VolumeOffset.Value = risingVolumeOffset;
        risingBallsMaterial.RimStrength.Value = groundMaterial.RimStrength.Value;
        risingBallsMaterial.RimFalloff.Value = groundMaterial.RimFalloff.Value;
        risingBallsMaterial.FresnelPower.Value = groundMaterial.FresnelPower.Value;
        risingBallsMaterial.AlphaScale.Value = 0.92f;
        risingBallsMaterial.EmissionStrength.Value = 1.05f;
        risingBallsMaterial.TimeScale.Value = groundMaterial.TimeScale.Value;
        risingBallsMaterial.RippleColor.Value = groundMaterial.RippleColor.Value;
        risingBallsMaterial.RippleStrength.Value = groundMaterial.RippleStrength.Value;
        risingBallsMaterial.RippleRadius.Value = groundMaterial.RippleRadius.Value;
        risingBallsMaterial.RippleWidth.Value = groundMaterial.RippleWidth.Value;
        risingBallsMaterial.RenderQueue.Value = 35;

        var risingBallsRenderer = risingBallsSlot.AttachComponent<MeshRenderer>();
        risingBallsRenderer.Mesh.Target = risingBallsMesh;
        risingBallsRenderer.Material.Target = risingBallsMaterial;
        risingBallsRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
        risingBallsRenderer.SortingOrder.Value = 8;

        var dropletParticlesSlot = groundSlot.AddSlot("DropletParticles");
        dropletParticlesSlot.LocalPosition.Value = new float3(0f, groundHeight * 0.5f + 0.01f, 0f);
        var dropletParticles = dropletParticlesSlot.AttachComponent<ParticleSystem>();
        dropletParticles.MaxParticles.Value = 420;
        dropletParticles.EmissionRate.Value = 20f;
        dropletParticles.BurstCount.Value = 7;
        dropletParticles.BurstInterval.Value = 0.38f;
        dropletParticles.EmitterExtents.Value = new float3(24.2f, 0f, 24.2f);
        dropletParticles.SpawnHeight.Value = 0.035f;
        dropletParticles.Lifetime.Value = 0.74f;
        dropletParticles.LifetimeVariance.Value = 0.20f;
        dropletParticles.StartSize.Value = 0.070f;
        dropletParticles.EndSize.Value = 0.014f;
        dropletParticles.InitialSpeed.Value = 1.35f;
        dropletParticles.SpeedVariance.Value = 0.48f;
        dropletParticles.Spread.Value = 0.30f;
        dropletParticles.Gravity.Value = -1.18f;
        dropletParticles.StartColor.Value = new colorHDR(0.90f, 1.00f, 0.94f, 0.90f);
        dropletParticles.EndColor.Value = new colorHDR(1.00f, 0.58f, 0.88f, 0.0f);
        dropletParticles.EmissionStrength.Value = 1.65f;
        dropletParticles.RenderQueue.Value = 60;
        dropletParticles.Seed.Value = 1771;

        var groundCollider = groundSlot.AttachComponent<CylinderCollider>();
        groundCollider.CharacterCollider.Value = true;
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Radius.Value = groundMesh.Radius.Value;
        groundCollider.Height.Value = groundMesh.Height.Value;
        groundCollider.Offset.Value = float3.Zero;

        var fillSlot = world.RootSlot.AddSlot(FillLightName);
        ShapeFill(fillSlot, fillSlot.AttachComponent<Light>());

        // Catch-all under the world. Bigger than ground so a user yeeted off-edge
        // still triggers respawn instead of falling forever. - xlinka
        var respawnSlot = world.RootSlot.AddSlot("RespawnPlane");
        var respawnPlane = respawnSlot.AttachComponent<RespawnPlane>();
        respawnPlane.Size.Value = new float2(100f, 100f);
        respawnPlane.Height.Value = -20f;
        respawnPlane.UseBounds.Value = false;
        respawnPlane.ShowVisual.Value = false;
        respawnPlane.ShowDebug.Value = false;
        respawnPlane.UserRespawnPosition.Value = new float3(0f, 1f, 0f);
    }

    private const string FillLightName = "FillLight";

    // 45 degrees up behind spawn's right shoulder. The sky drew its sun ahead and to the left, a different side
    // of the sky from where the light came; it now takes it from this rotation. -xlinka
    private static readonly floatQ SunRotation = floatQ.Euler(0.785f, -0.785f, 0f);
    private static float3 SkySun => SunRotation * float3.Forward;
    private static readonly float3 RetiredSkySun = new float3(-0.38f, 0.58f, -0.72f);

    // Fill from the side the sun is not on, low and to the front left: a directional light, cool and faint,
    // with no shadow. It was a point light five metres up with a hundred metre reach, which lit the middle of
    // the home brighter than its edges and had every pixel in reach light it. -xlinka
    private static void ShapeFill(Slot slot, Light light)
    {
        slot.LocalPosition.Value = new float3(0f, 10f, 0f);
        slot.LocalRotation.Value = floatQ.Euler(-2.404f, -0.467f, 0f);
        light.Type.Value = LightType.Directional;
        light.LightColor.Value = new color(0.4f, 0.45f, 0.55f, 1f);
        light.Intensity.Value = 0.3f;
        light.Shadows.Value = ShadowType.None;
    }

    // What older builds of this template put in the home and it no longer does, put right in a saved home as
    // it loads: the Helio test panel goes, the old point fill becomes the directional one, and the sky's sun
    // moves to where the light comes from. Each is matched by everything the template gave it, so nothing a
    // person made or changed is touched. -xlinka
    internal static void RemoveRetired(World world)
    {
        foreach (var slot in new List<Slot>(world.RootSlot.Children))
        {
            if (slot.Name.Value == "HelioTestPanel"
                && slot.GetComponent<PanelShell>()?.Title.Value?.StartsWith("Helio Validation", StringComparison.Ordinal) == true)
                slot.Destroy();
        }

        var oldFill = world.RootSlot.FindChild("AmbientLight", recursive: false);
        var light = oldFill?.GetComponent<Light>();
        if (oldFill != null && light != null && light.Type.Value == LightType.Point && light.Range.Value == 100f
            && light.Intensity.Value == 0.4f && light.Shadows.Value == ShadowType.None
            && oldFill.LocalPosition.Value.Equals(new float3(0f, 5f, 0f)))
        {
            oldFill.Name.Value = FillLightName;
            ShapeFill(oldFill, light);
        }

        var sky = world.RootSlot.FindChild("GradientSkybox", recursive: false)?.GetComponent<GradientSkybox>();
        if (sky != null && sky.SunDirection.Value.Equals(RetiredSkySun))
            sky.SunDirection.Value = SkySun;
    }
}
