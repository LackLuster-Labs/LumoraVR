// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Templates;

internal sealed class LocalHomeWorldTemplate : WorldTemplateDefinition
{
    public LocalHomeWorldTemplate() : base("LocalHome") { }

    protected override void Build(World world)
    {
        var spawnSlot = world.RootSlot.AddSlot("SpawnArea");
        spawnSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();

        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = floatQ.Euler(0.785f, -0.785f, 0f);
        var dirLight = lightSlot.AttachComponent<Light>();
        dirLight.Type.Value = LightType.Directional;
        dirLight.LightColor.Value = new color(1f, 0.96f, 0.84f, 1f);
        dirLight.Intensity.Value = 1.2f;
        dirLight.Shadows.Value = ShadowType.Soft;

        var skySlot = world.RootSlot.AddSlot("GradientSkybox");
        var skybox = skySlot.AttachComponent<GradientSkybox>();
        skybox.TopColor.Value = new color(0.070f, 0.060f, 0.150f, 1f);
        skybox.HorizonColor.Value = new color(0.46f, 0.38f, 0.58f, 1f);
        skybox.BottomColor.Value = new color(0.040f, 0.032f, 0.065f, 1f);
        skybox.SunColor.Value = new color(1.0f, 0.72f, 0.54f, 1f);
        skybox.SunDirection.Value = new float3(-0.38f, 0.58f, -0.72f);
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

        var risingBallsMesh = risingBallsSlot.AttachComponent<BoxMesh>();
        risingBallsMesh.Size.Value = new float3(risingDiskRadius * 2f, risingVolumeHeight, risingDiskRadius * 2f);

        var risingBallsMaterial = risingBallsSlot.AttachComponent<LocalHomeRisingMaterial>();
        risingBallsMaterial.TintA.Value = groundMaterial.TintA.Value;
        risingBallsMaterial.TintB.Value = groundMaterial.TintB.Value;
        risingBallsMaterial.BlobRadius.Value = groundMaterial.BlobRadius.Value;
        risingBallsMaterial.BlobSmoothness.Value = groundMaterial.BlobSmoothness.Value;
        risingBallsMaterial.BlobCount.Value = groundMaterial.BlobCount.Value;
        risingBallsMaterial.RiseSpeed.Value = groundMaterial.RiseSpeed.Value;
        risingBallsMaterial.VolumeExtents.Value = new float2(risingDiskRadius, risingDiskRadius);
        risingBallsMaterial.VolumeHeight.Value = risingVolumeHeight;
        risingBallsMaterial.VolumeOffset.Value = new float3(0f, -risingVolumeHeight * 0.5f - risingVolumeLift, 0f);
        risingBallsMaterial.RimStrength.Value = groundMaterial.RimStrength.Value;
        risingBallsMaterial.RimFalloff.Value = groundMaterial.RimFalloff.Value;
        risingBallsMaterial.FresnelPower.Value = groundMaterial.FresnelPower.Value;
        risingBallsMaterial.AlphaScale.Value = 0.92f;
        risingBallsMaterial.EmissionStrength.Value = 1.05f;
        risingBallsMaterial.TimeScale.Value = groundMaterial.TimeScale.Value;
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
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Radius.Value = groundMesh.Radius.Value;
        groundCollider.Height.Value = groundMesh.Height.Value;
        groundCollider.Offset.Value = float3.Zero;

        var ambientLightSlot = world.RootSlot.AddSlot("AmbientLight");
        ambientLightSlot.LocalPosition.Value = new float3(0f, 5f, 0f);
        var ambientLight = ambientLightSlot.AttachComponent<Light>();
        ambientLight.Type.Value = LightType.Point;
        ambientLight.LightColor.Value = new color(0.4f, 0.45f, 0.55f, 1f);
        ambientLight.Intensity.Value = 0.4f;
        ambientLight.Range.Value = 100f;
        ambientLight.Shadows.Value = ShadowType.None;

        var clipboardSlot = world.RootSlot.AddSlot("ClipboardImporter");
        clipboardSlot.AttachComponent<ClipboardImporter>();

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
}
