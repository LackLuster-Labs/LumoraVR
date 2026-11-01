// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Godot.Hooks;

namespace Lumora.Fluids.Hooks;

// Draws the stain grids of a world's systems: Godot decals on Forward+, quads laid on the surface in one
// MultiMesh on the Mobile renderer (FluidStain), all under the world's own node, so a background world shows none.
// The ledger mirror hands the stains over when the grid changes (BeginStains, AddStain, CommitStains); a decal
// node is kept and reused, never made per frame. -xlinka
[ImplementableHook(typeof(FluidStainPool))]
public sealed class FluidStainPoolHook : ComponentHook<FluidStainPool>
{
    private static readonly Dictionary<World, FluidStainPoolHook> _byWorld = new();
    private static Texture2D? _stainTexture;

    private readonly List<Decal> _decals = new();
    private readonly float[] _quads = new float[FluidStainPool.MaxStains * FluidResiduePass.InstanceFloats];
    private Rid _quadMesh;
    private Rid _multimesh;
    private Rid _instance;
    private ShaderMaterial? _material;
    private bool _mobile;
    private int _count;
    private World? _world;

    public int Count => _count;

    public override void Initialize()
    {
        base.Initialize();
        FluidGodotStart.EnsureStarted();
        _world = Owner.World;
        if (_world != null)
            _byWorld[_world] = this;
        _mobile = FluidPlatform.IsMobileRenderer;
    }

    public static FluidStainPoolHook? For(World world) => _byWorld.TryGetValue(world, out var hook) ? hook : null;

    // A soft round stain with a ragged edge, made once.
    private static Texture2D StainTexture()
    {
        if (_stainTexture != null)
            return _stainTexture;
        const int Size = 64;
        var image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float dx = (x + 0.5f) / Size * 2f - 1f, dy = (y + 0.5f) / Size * 2f - 1f;
            float angle = MathF.Atan2(dy, dx);
            float edge = 0.8f + 0.08f * MathF.Sin(angle * 5f) + 0.05f * MathF.Sin(angle * 9f + 1.3f);
            float r = MathF.Sqrt(dx * dx + dy * dy) / edge;
            float alpha = Math.Clamp((1f - r) / 0.35f, 0f, 1f);
            image.SetPixel(x, y, new Color(1f, 1f, 1f, alpha * alpha));
        }
        _stainTexture = ImageTexture.CreateFromImage(image);
        return _stainTexture;
    }

    private void EnsureMobile()
    {
        if (_multimesh.IsValid)
            return;
        var scenario = attachedNode.GetWorld3D()?.Scenario ?? default;
        if (!scenario.IsValid)
            return;
        var quad = new QuadMesh { Size = Vector2.One };
        _quadMesh = quad.GetRid();
        _keepQuad = quad;
        _multimesh = RenderingServer.MultimeshCreate();
        RenderingServer.MultimeshSetMesh(_multimesh, _quadMesh);
        RenderingServer.MultimeshAllocateData(_multimesh, FluidStainPool.MaxStains, RenderingServer.MultimeshTransformFormat.Transform3D, true, true, false);
        RenderingServer.MultimeshSetVisibleInstances(_multimesh, 0);
        _instance = RenderingServer.InstanceCreate2(_multimesh, scenario);
        RenderingServer.InstanceSetLayerMask(_instance, FluidRenderLayers.Public(Owner.World));
        RenderingServer.InstanceGeometrySetCastShadowsSetting(_instance, RenderingServer.ShadowCastingSetting.Off);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Modules/Fluids/Shaders/FluidStain.gdshader") };
        RenderingServer.InstanceGeometrySetMaterialOverride(_instance, _material.GetRid());
    }

    private QuadMesh? _keepQuad;

    public override void ApplyChanges()
    {
        if (attachedNode == null)
            return;
        if (_mobile)
            EnsureMobile();
        if (_instance.IsValid)
            RenderingServer.InstanceSetVisible(_instance, Owner.Enabled);
        foreach (var decal in _decals)
            decal.Visible = Owner.Enabled && decal.Visible;
    }

    public void BeginStains() => _count = 0;

    // One stain cell: where it sits, the surface normal, its width, its tint, and how much is left (0..1).
    public void AddStain(Vector3 centre, Vector3 normal, float size, Color tint, float amount)
    {
        if (_count >= FluidStainPool.MaxStains || attachedNode == null)
            return;
        var n = normal.LengthSquared() > 1e-8f ? normal.Normalized() : Vector3.Up;
        var t1 = Math.Abs(n.Y) < 0.99f ? Vector3.Up.Cross(n).Normalized() : Vector3.Right.Cross(n).Normalized();
        var t2 = n.Cross(t1);
        if (_mobile)
        {
            EnsureMobile();
            // the quad faces +Z: Z onto the normal, lifted a millimetre off the surface
            var basis = new Basis(t1 * size, t2 * size, n);
            var at = centre + n * 0.001f;
            var span = _quads.AsSpan(_count * FluidResiduePass.InstanceFloats, FluidResiduePass.InstanceFloats);
            span[0] = basis.X.X; span[1] = basis.Y.X; span[2] = basis.Z.X; span[3] = at.X;
            span[4] = basis.X.Y; span[5] = basis.Y.Y; span[6] = basis.Z.Y; span[7] = at.Y;
            span[8] = basis.X.Z; span[9] = basis.Y.Z; span[10] = basis.Z.Z; span[11] = at.Z;
            span[12] = tint.R; span[13] = tint.G; span[14] = tint.B; span[15] = tint.A;
            float seed = (MathF.Abs(centre.X * 12.9898f + centre.Z * 78.233f) % 1f);
            span[16] = Math.Clamp(amount, 0f, 1f); span[17] = seed; span[18] = 1f - seed; span[19] = 0f;
        }
        else
        {
            Decal decal;
            if (_count < _decals.Count)
                decal = _decals[_count];
            else
            {
                decal = new Decal { TextureAlbedo = StainTexture(), CullMask = FluidRenderLayers.Public(Owner.World), UpperFade = 0.2f, LowerFade = 0.2f };
                attachedNode.AddChild(decal);
                _decals.Add(decal);
            }
            // a decal projects along its own -Y: Y onto the normal
            decal.GlobalTransform = new Transform3D(new Basis(t1, n, -t2), centre);
            decal.Size = new Vector3(size, 0.06f, size);
            decal.Modulate = new Color(tint.R, tint.G, tint.B, tint.A * Math.Clamp(amount, 0f, 1f));
            decal.Visible = Owner.Enabled;
        }
        _count++;
    }

    public void CommitStains()
    {
        if (_mobile)
        {
            if (!_multimesh.IsValid)
                return;
            _quads.AsSpan(_count * FluidResiduePass.InstanceFloats).Clear();
            RenderingServer.MultimeshSetBuffer(_multimesh, _quads);
            RenderingServer.MultimeshSetVisibleInstances(_multimesh, _count);
            return;
        }
        for (int i = _count; i < _decals.Count; i++)
            _decals[i].Visible = false;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_world != null && _byWorld.TryGetValue(_world, out var hook) && ReferenceEquals(hook, this))
            _byWorld.Remove(_world);
        foreach (var decal in _decals)
        {
            if (GodotObject.IsInstanceValid(decal))
                decal.QueueFree();
        }
        _decals.Clear();
        if (_instance.IsValid)
            RenderingServer.FreeRid(_instance);
        if (_multimesh.IsValid)
            RenderingServer.FreeRid(_multimesh);
        _instance = _multimesh = default;
        _keepQuad = null;
        _material?.Dispose();
        _material = null;
        base.Destroy(destroyingWorld);
    }
}
