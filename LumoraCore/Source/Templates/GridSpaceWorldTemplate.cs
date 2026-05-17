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
        spawnSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();

        // Warm low-angle key light. Rotation derived so the slot's local -Z
        // (Godot DirectionalLight photon direction) is exactly opposite the
        // skybox's sun_direction, i.e. photons travel from the visible sun
        // out into the scene. floatQ.Euler args are (yaw, pitch, roll). - xlinka
        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = floatQ.Euler(-2.55f, -0.181f, 0f);
        var dirLight = lightSlot.AttachComponent<Light>();
        dirLight.Type.Value = LightType.Directional;
        dirLight.LightColor.Value = new color(1.00f, 0.78f, 0.55f, 1f);
        dirLight.Intensity.Value = 1.4f;
        dirLight.Shadows.Value = ShadowType.Soft;

        // Morning-sunrise sky: deep cool dawn-blue overhead, a wide warm
        // peach/orange band at the horizon, low sun positioned where the
        // directional light is coming from. - xlinka
        var skySlot = world.RootSlot.AddSlot("GradientSkybox");
        var skybox = skySlot.AttachComponent<GradientSkybox>();
        skybox.TopColor.Value = new color(0.14f, 0.18f, 0.40f, 1f);
        skybox.HorizonColor.Value = new color(1.00f, 0.60f, 0.40f, 1f);
        skybox.BottomColor.Value = new color(0.95f, 0.55f, 0.42f, 1f);
        skybox.SunColor.Value = new color(1.00f, 0.78f, 0.52f, 1f);
        skybox.SunDirection.Value = new float3(-0.55f, 0.18f, -0.82f);
        skybox.SunSize.Value = 0.045f;
        skybox.SunIntensity.Value = 2.6f;
        skybox.SunGlowPower.Value = 48f;
        skybox.AmbientEnergy.Value = 0.70f;

        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        groundSlot.Tag.Value = "floor";

        var groundMesh = groundSlot.AttachComponent<BoxMesh>();
        groundMesh.Size.Value = new float3(100f, 0.1f, 100f);
        groundMesh.UVScale.Value = new float3(100f, 1f, 100f);

        var groundMaterial = groundSlot.AttachComponent<GridSpaceGroundMaterial>();
        groundMaterial.BlendMode.Value = BlendMode.Opaque;
        groundMaterial.Culling.Value = Culling.Back;

        var groundRenderer = groundSlot.AttachComponent<MeshRenderer>();
        groundRenderer.Mesh.Target = groundMesh;
        groundRenderer.Material.Target = groundMaterial;
        groundRenderer.ShadowCastMode.Value = ShadowCastMode.Off;

        var groundCollider = groundSlot.AttachComponent<BoxCollider>();
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Size.Value = groundMesh.Size.Value;
        groundCollider.Offset.Value = new float3(0f, -groundMesh.Size.Value.y * 0.5f, 0f);

        var orbSlot = world.RootSlot.AddSlot("GridMaterialOrb");
        orbSlot.LocalPosition.Value = new float3(0.95f, 1.25f, -1.05f);
        orbSlot.AttachComponent<Grabbable>();

        var orbMesh = orbSlot.AttachComponent<SphereMesh>();
        orbMesh.Radius.Value = 0.17f;
        orbMesh.Segments.Value = 28;
        orbMesh.Rings.Value = 16;
        orbMesh.UVScale.Value = new float2(6f, 3f);

        var orbRenderer = orbSlot.AttachComponent<MeshRenderer>();
        orbRenderer.Mesh.Target = orbMesh;
        orbRenderer.Material.Target = groundMaterial;
    }
}
