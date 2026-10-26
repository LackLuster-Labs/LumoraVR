// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

[ComponentCategory("Rendering")]
public sealed class Mirror : CameraPortal
{
    public const int MinimumResolution = 128;
    public const int MaximumResolution = 8192;

    public readonly Sync<float2> Size;
    public readonly Sync<int> Resolution;
    public readonly SyncRef<QuadMesh> Surface;
    public readonly SyncRef<ReflectionMaterial> Material;
    public readonly SyncRef<RenderTextureProvider> Texture;

    public Mirror()
    {
        Size = new Sync<float2>(this, new float2(2.6f, 2.2f));
        Resolution = new Sync<int>(this, 4096);
        Surface = new SyncRef<QuadMesh>(this);
        Material = new SyncRef<ReflectionMaterial>(this);
        Texture = new SyncRef<RenderTextureProvider>(this);
    }

    public override void OnAttach()
    {
        base.OnAttach();
        // OnAttach runs only for a newly authored component. Saved references restore as-is.
        var glass = Slot.AddSlot("Mirror surface");
        glass.AttachComponent<RenderLayerOverride>().Layer.Value = RenderLayerOverride.MirrorLayer;
        Surface.Target = glass.AttachComponent<QuadMesh>();
        Texture.Target = glass.AttachComponent<RenderTextureProvider>();
        Material.Target = glass.AttachComponent<ReflectionMaterial>();
        Material.Target.ReflectionTexture.Target = Texture.Target;
        Material.Target.SurfaceUVSampling.Value = true;
        Material.Target.MirrorSampling.Value = false;
        Renderer.Target = glass.AttachComponent<MeshRenderer>();
        Renderer.Target.Mesh.Target = Surface.Target;
        Renderer.Target.Material.Target = Material.Target;
        Renderer.Target.ShadowCastMode.Value = ShadowCastMode.Off;
        ReflectionTexture.Target = Texture.Target;
        PlaneOffset.Value = 0f;
        OverrideFarClip.Value = 40f;
        SyncSurface();
    }

    // A mirror on its own: the glass, a collider the size of it and a grab handle, and nothing drawn over the
    // glass. What Create New and the world templates put down. -xlinka
    public static Mirror Spawn(Slot parent, float3 position, float3 facing, float2 size)
    {
        var root = parent.AddSlot("Mirror");
        root.GlobalPosition = position;
        facing.y = 0f;
        if (facing.LengthSquared < 1e-6f) facing = float3.Backward;
        facing = facing.Normalized;
        root.GlobalRotation = floatQ.AxisAngleRad(float3.Up, System.MathF.Atan2(-facing.x, -facing.z));
        if (float3.Dot(root.Backward, facing) < 0f)
            root.GlobalRotation = root.GlobalRotation * floatQ.AxisAngleRad(float3.Up, System.MathF.PI);
        return SetUp(root, size);
    }

    public static Mirror SetUp(Slot slot, float2? size = null)
    {
        var mirror = slot.AttachComponent<Mirror>();
        if (size is { } wanted)
            mirror.Size.Value = wanted;
        slot.GetOrAttachComponent<BoxCollider>();
        slot.GetOrAttachComponent<Grabbable>().Scalable.Value = true;
        mirror.SyncSurface();
        return mirror;
    }

    public override void OnChanges()
    {
        SyncSurface();
        base.OnChanges();
    }

    private void SyncSurface()
    {
        var size = new float2(System.Math.Clamp(Size.Value.x, 0.05f, 100f),
            System.Math.Clamp(Size.Value.y, 0.05f, 100f));
        SurfaceSize.Value = size;
        if (Surface.Target != null) Surface.Target.Size.Value = size;
        if (Slot?.GetComponent<BoxCollider>() is { } collider)
        {
            var box = new float3(size.x, size.y, 0.02f);
            if (collider.Size.Value != box) collider.Size.Value = box;
        }
        if (Texture.Target != null)
        {
            int resolution = System.Math.Clamp(Resolution.Value, MinimumResolution, MaximumResolution);
            Texture.Target.Size.Value = new int2(resolution, resolution);
        }
    }
}
