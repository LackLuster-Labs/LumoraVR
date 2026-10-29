// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// A spot a camera can be sent to: the camera glides there, lens on the anchor, looking where it looks, at its
// width of view, and stays until another mode is picked. A laser press on it sends yours there. Its marker is
// drawn for eyes only, so no camera ever films one. In use while a camera sits on it; one camera to an
// anchor. Routes string anchors together (CameraRoute) and never hold one. -xlinka
[ComponentCategory("Interaction/Tools")]
public class CameraAnchor : Component
{
    // Where the lens goes; nothing means this slot.
    public readonly SyncRef<Slot> Root = new();
    public readonly Sync<float> FieldOfView = new(60f);
    // Seconds a route's glide rests here; nothing means it flows straight through.
    public readonly Sync<float> Hold = new();

    private static readonly ConditionalWeakTable<World, List<CameraAnchor>> Registry = new();

    // Above the grabbable beside it, so a press is the anchor's; a grab still finds the grabbable.
    private const int RayPriority = 10;

    private Slot? _marker;
    private RayTarget? _ray;
    private Action<float3>? _pressed;

    public Slot CameraRoot => Root.Target is { IsDestroyed: false } root ? root : Slot;

    public static IReadOnlyList<CameraAnchor> InWorld(World? world)
        => world != null && Registry.TryGetValue(world, out var list) ? list : Array.Empty<CameraAnchor>();

    // The camera sitting on it, if any.
    public PhotoCamera? User
    {
        get
        {
            foreach (var camera in PhotoCamera.InWorld(World))
            {
                if (!camera.IsDestroyed && ReferenceEquals(camera.Anchor.Target, this))
                    return camera;
            }
            return null;
        }
    }

    public bool InUse => User != null;

    // Sends the local user's camera here.
    public void Activate() => CameraDirector.Local?.UseAnchor(this);

    // Where a camera's lens is now, at its width of view.
    public static CameraAnchor? Spawn(World world, float3 position, floatQ rotation, float fieldOfView)
    {
        if (world.RootSlot == null)
            return null;
        using var own = world.EnterSpawnAllocation();
        var slot = world.RootSlot.AddSlot("Camera Anchor");
        slot.GlobalPosition = position;
        slot.GlobalRotation = rotation;
        var anchor = slot.AttachComponent<CameraAnchor>();
        anchor.FieldOfView.Value = fieldOfView;
        return anchor;
    }

    public override void OnAttach()
    {
        base.OnAttach();
        var collider = Slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(0.1f, 0.07f, 0.09f);
        collider.CharacterCollider.Value = false;
        Slot.AttachComponent<Grabbable>();
        AttachRay();
    }

    public override void OnStart()
    {
        base.OnStart();
        if (World != null)
        {
            var list = Registry.GetOrCreateValue(World);
            if (!list.Contains(this))
                list.Add(this);
        }
        BuildMarker();

        // The target is saved with the anchor and what a press does is not, so it is hooked up every session.
        _ray = Slot.GetComponent<RayTarget>() ?? (World?.IsAuthority == true ? AttachRay() : null);
        if (_ray != null)
        {
            _pressed = _ => Activate();
            _ray.Activated += _pressed;
        }
    }

    public override void OnDestroy()
    {
        if (_ray != null && _pressed != null)
            _ray.Activated -= _pressed;
        _ray = null;
        _pressed = null;
        if (World != null && Registry.TryGetValue(World, out var list))
            list.Remove(this);
        if (_marker is { IsDestroyed: false })
            _marker.Destroy();
        base.OnDestroy();
    }

    private RayTarget AttachRay()
    {
        var ray = Slot.AttachComponent<RayTarget>();
        ray.InteractionPriority.Value = RayPriority;
        return ray;
    }

    private void BuildMarker()
    {
        if (_marker is { IsDestroyed: false })
            return;
        _marker = Slot.AddLocalSlot("Marker");
        var layer = _marker.AttachComponent<RenderLayerOverride>();
        layer.Layer.Value = RenderLayerOverride.EyesOnlyLayer;

        var body = _marker.AddLocalSlot("Body");
        body.LocalPosition.Value = new float3(0f, 0f, 0.03f);
        var box = body.AttachComponent<BoxMesh>();
        box.Size.Value = new float3(0.08f, 0.05f, 0.05f);
        Paint(body, box, new colorHDR(0.95f, 0.7f, 0.1f, 0.55f));

        var lens = _marker.AddLocalSlot("Lens");
        lens.LocalPosition.Value = new float3(0f, 0f, -0.005f);
        lens.LocalRotation.Value = floatQ.AxisAngle(float3.Right, MathF.PI * 0.5f);
        var cone = lens.AttachComponent<CylinderMesh>();
        cone.Radius.Value = 0.018f;
        cone.Height.Value = 0.02f;
        Paint(lens, cone, new colorHDR(0.95f, 0.7f, 0.1f, 0.55f));
    }

    private static void Paint(Slot slot, ProceduralMesh mesh, colorHDR tint)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var material = slot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = tint;
        material.BlendMode.Value = BlendMode.Alpha;
        renderer.Material.Target = material;
    }
}
