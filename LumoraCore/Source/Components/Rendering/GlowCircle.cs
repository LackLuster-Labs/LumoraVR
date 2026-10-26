// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Glowing ground circle: an additive soft disc on the floor plus an open ring cylinder whose glow
// fades with height. The classic spawn-point marker. Builds its visuals as child slots on attach and
// re-applies Radius/Height/Color to them on change.
[ComponentCategory("Rendering")]
public class GlowCircle : Component, IPrimaryColorSource
{
    // Keep both visuals clear of the floor instead of competing with its depth.
    private const float SurfaceClearance = 0.01f;

    public readonly Sync<float> Radius;
    public readonly Sync<float> Height;
    // tint of the ground disc
    public readonly Sync<colorHDR> Color;
    // tint of the vertical ring
    public readonly Sync<colorHDR> RingColor;

    private readonly SyncRef<QuadMesh> _discMesh;
    private readonly SyncRef<CylinderMesh> _ringMesh;
    private readonly SyncRef<Slot> _ringSlot;
    private readonly SyncRef<UnlitMaterial> _discMaterial;
    private readonly SyncRef<UnlitMaterial> _ringMaterial;

    public GlowCircle()
    {
        Radius = new Sync<float>(this, 0.5f);
        Height = new Sync<float>(this, 0.5f);
        Color = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 1f));
        RingColor = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 1f));
        _discMesh = new SyncRef<QuadMesh>(this);
        _ringMesh = new SyncRef<CylinderMesh>(this);
        _ringSlot = new SyncRef<Slot>(this);
        _discMaterial = new SyncRef<UnlitMaterial>(this);
        _ringMaterial = new SyncRef<UnlitMaterial>(this);
    }

    public void Setup(float radius, float height, colorHDR discColor, colorHDR? ringColor = null)
    {
        Radius.Value = radius;
        Height.Value = height;
        Color.Value = discColor;
        RingColor.Value = ringColor ?? discColor;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Radius.OnChanged += _ => Apply();
        Height.OnChanged += _ => Apply();
        Color.OnChanged += _ => Apply();
        RingColor.OnChanged += _ => Apply();
    }

    public override void OnAttach()
    {
        base.OnAttach();
        if (_ringMesh.Target != null)
            return; // loaded/duplicated: visuals already exist

        // Soft disc on the ground.
        var discSlot = Slot.AddSlot("Disc");
        var discTexture = discSlot.AttachComponent<GradientStripTexture>();
        discTexture.Orientation.Value = GradientStripTexture.StripOrientation.Radial;
        discTexture.Exp.Value = 2f;
        discTexture.Size.Value = 64;

        var discMesh = discSlot.AttachComponent<QuadMesh>();
        // Lay the quad flat (normal up). floatQ.Euler is RADIANS, so axis-angle is clearer here.
        discMesh.Rotation.Value = floatQ.AxisAngleRad(float3.Right, -System.MathF.PI * 0.5f);
        discMesh.DualSided.Value = true;

        var discMaterial = discSlot.AttachComponent<UnlitMaterial>();
        discMaterial.BlendMode.Value = BlendMode.Additive;
        discMaterial.Texture.Target = discTexture;

        var discRenderer = discSlot.AttachComponent<MeshRenderer>();
        discRenderer.Mesh.Target = discMesh;
        discRenderer.Material.Target = discMaterial;

        // Open ring, glow fading upward.
        var ringSlot = Slot.AddSlot("Ring");
        var ringTexture = ringSlot.AttachComponent<GradientStripTexture>();
        ringTexture.Orientation.Value = GradientStripTexture.StripOrientation.Vertical;
        ringTexture.Exp.Value = 1.5f;

        var ringMesh = ringSlot.AttachComponent<CylinderMesh>();
        ringMesh.Caps.Value = false;
        ringMesh.Segments.Value = 32;

        var ringMaterial = ringSlot.AttachComponent<UnlitMaterial>();
        ringMaterial.BlendMode.Value = BlendMode.Additive;
        ringMaterial.Culling.Value = Culling.None;
        ringMaterial.Texture.Target = ringTexture;

        var ringRenderer = ringSlot.AttachComponent<MeshRenderer>();
        ringRenderer.Mesh.Target = ringMesh;
        ringRenderer.Material.Target = ringMaterial;

        _discMesh.Target = discMesh;
        _ringMesh.Target = ringMesh;
        _ringSlot.Target = ringSlot;
        _discMaterial.Target = discMaterial;
        _ringMaterial.Target = ringMaterial;

        Apply();
    }

    public override void OnStart()
    {
        base.OnStart();
        // Loaded and duplicated markers already have children; update those too.
        Apply();
    }

    // A marker that arrives with its disc and ring already made, as an imported one does: the parts this built
    // on attach go, and the ones it came with are taken up one at a time as they are found.
    internal void DropBuiltParts()
    {
        if (_discMesh.Target?.Slot is { } disc && !ReferenceEquals(disc, Slot) && disc.Parent == Slot)
            disc.Destroy();
        if (_ringSlot.Target is { } ring && !ReferenceEquals(ring, Slot) && ring.Parent == Slot)
            ring.Destroy();
        _discMesh.Target = null!;
        _ringMesh.Target = null!;
        _ringSlot.Target = null!;
        _discMaterial.Target = null!;
        _ringMaterial.Target = null!;
    }

    internal enum Part { Disc, Ring, RingSlot, DiscMaterial, RingMaterial }

    internal bool Adopt(Part part, IWorldElement found)
    {
        var owner = found is Networking.Sync.SyncElement { Parent: { } parent } && found is not Component and not Lumora.Core.Slot ? parent : found;
        bool taken = part switch
        {
            Part.Disc when owner is QuadMesh quad => (_discMesh.Target = quad) != null,
            Part.Ring when owner is CylinderMesh cylinder => (_ringMesh.Target = cylinder) != null,
            Part.RingSlot when owner is Lumora.Core.Slot slot => (_ringSlot.Target = slot) != null,
            Part.DiscMaterial when owner is UnlitMaterial material => (_discMaterial.Target = material) != null,
            Part.RingMaterial when owner is UnlitMaterial material => (_ringMaterial.Target = material) != null,
            _ => false,
        };
        if (taken)
            Apply();
        return taken;
    }

    private void Apply()
    {
        float radius = Radius.Value;
        float height = Height.Value;
        var tint = Color.Value;

        if (_discMesh.Target is { } disc)
        {
            if (!ReferenceEquals(disc.Slot, Slot))
                disc.Slot.LocalPosition.Value = new float3(0f, SurfaceClearance, 0f);
            disc.Size.Value = new float2(radius * 2.5f, radius * 2.5f); // soft edge extends past the ring
        }
        if (_ringMesh.Target is { } ring)
        {
            ring.Radius.Value = radius;
            ring.Height.Value = height;
        }
        if (_ringSlot.Target is { } ringSlot)
            ringSlot.LocalPosition.Value = new float3(0f, height * 0.5f + SurfaceClearance, 0f);
        if (_discMaterial.Target is { } discMat)
            discMat.TintColor.Value = tint;
        if (_ringMaterial.Target is { } ringMat)
            ringMat.TintColor.Value = RingColor.Value;
    }

    // The disc is the marker; the ring is trim that usually rides the same colour anyway.
    public bool TryGetPrimaryColor(out colorHDR color)
    {
        color = Color.Value;
        return true;
    }
}
