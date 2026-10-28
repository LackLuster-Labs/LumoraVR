// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Godot.Hooks.Particles;
using Lumora.Simulation.Particles;
using LumoraParticleSystem = Lumora.Core.Components.ParticleSystem;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Renders a ParticleSystem's particles as one MultiMesh draw call. The simulation lives in LumoraSimulation
// and is driven by the engine component (emitters/modules are engine components too); this hook only pulls
// the finished position/size/rotation/color buffers each frame and writes the instance buffer in a single
// upload - per-instance Set* calls cost a marshalling round-trip each, which at particle counts IS the
// frame budget. -xlinka
//
// The higher tiers hang off the same pull: trail and ribbon strands become ribbons of quads under
// StrandSurface, a flipbook's fractional frame rides the instance buffer's custom data into the shader,
// and nominated light candidates go through ParticleLightArbiter before any of them become a real
// OmniLight3D. Each of those is skipped entirely when the module that feeds it is not attached.
[ImplementableHook(typeof(LumoraParticleSystem))]
public sealed partial class ParticleSystemHook : ComponentHook<LumoraParticleSystem>, ILodRangeTarget
{
    private const string ShaderPath = "res://Shaders/EngineParticle.gdshader";

    // One variant per blend mode; they differ by a single render_mode line over a shared body. Opaque and
    // cutout have no particle meaning of their own and fall in with alpha, which is what a sprite with a
    // hard-edged texture wants anyway.
    private static string ShaderPathFor(Lumora.Core.Assets.BlendMode mode) => mode switch
    {
        Lumora.Core.Assets.BlendMode.Additive => "res://Shaders/EngineParticle.gdshader",
        Lumora.Core.Assets.BlendMode.Multiply => "res://Shaders/EngineParticleMultiply.gdshader",
        _ => "res://Shaders/EngineParticleAlpha.gdshader",
    };

    private Lumora.Core.Assets.BlendMode _appliedBlendMode = (Lumora.Core.Assets.BlendMode)(-1);

    private void ApplyBlendMode()
    {
        var mode = Owner.BlendMode.Value;
        if (mode == _appliedBlendMode || _material == null)
            return;
        _appliedBlendMode = mode;

        string path = ShaderPathFor(mode);
        if (!ResourceLoader.Exists(path))
        {
            LumoraLogger.Warn($"ParticleSystemHook: particle shader not found at {path}");
            return;
        }

        var shader = GD.Load<Shader>(path);
        if (shader != null && _material.Shader != shader)
            _material.Shader = shader;   // uniform values persist across the swap
    }
    private const int InstanceStride = 16; // 12 transform + 4 color
    private const int SheetStride = 20;    // ...+ 4 custom data, carrying the flipbook frame

    // Hard ceiling on real dynamic lights ONE system may promote, on top of whatever its ParticleLights
    // asks for. A promoted candidate is a real light with a real per-light cost and the component's
    // MaxLights is authored blind - by someone who cannot see how many other systems are in frame. This
    // is the number that stops a world full of ember fountains from being a lighting bill. -xlinka
    private const int MaxPromotedLights = 12;

    private ParticleProcessNode _processNode = null!;
    private MultiMeshInstance3D _instance = null!;
    private MultiMesh _multiMesh = null!;
    private SphereMesh _particleMesh = null!;
    private QuadMesh _sheetMesh = null!;
    private ShaderMaterial _material = null!;
    private float[] _buffer = System.Array.Empty<float>();
    private int _capacity;
    private int _lastRenderVersion = -1;
    private bool _useSheet;

    private int _sheetColumns = -1;
    private int _sheetRows = -1;
    private int _sheetFrames = -1;
    private Texture2D? _sheetTexture;
    private bool _sheetBillboard;

    // The authored per-particle mesh currently on the MultiMesh, or null while using the built-ins.
    private Mesh? _authoredMesh;
    private bool _authoredMeshLogged;

    // Same resolution MeshRendererHookBase does: a MeshProvider hands over the ArrayMesh its asset hook
    // already built, a procedural mesh component hands over its own. Null while an asset is still
    // decoding, which is not an error - this re-runs every pass.
    private int _meshWaitPasses;
    private bool _meshWaitLogged;

    // Says which link of the chain is missing, once, when an assigned mesh has failed to arrive for long
    // enough that it is not simply still decoding. Provider -> asset -> hook -> ArrayMesh is four hops and
    // a null at any of them looks identical from here: the sphere just keeps drawing. -xlinka
    private void ReportMeshWait()
    {
        if (_meshWaitLogged || Owner.ParticleMeshAsset.Target == null)
            return;
        if (++_meshWaitPasses < 240)
            return;

        _meshWaitLogged = true;
        var provider = Owner.ParticleMeshAsset.Target;
        var asset = Owner.ParticleMeshAsset.Asset;
        LumoraLogger.Warn(
            $"ParticleSystemHook '{Owner.Slot?.SlotName.Value}': mesh assigned but never arrived - "
            + $"provider={provider.GetType().Name} refCount={provider.AssetReferenceCount} "
            + $"available={provider.IsAssetAvailable} asset={(asset == null ? "null" : asset.LoadState.ToString())} "
            + $"hook={(asset?.Hook?.GetType().Name ?? "null")}");
    }

    private Mesh? ResolveParticleMesh()
    {
        // An imported mesh first: it is the reference that actually drives a decode, so if one is set it
        // is the one the author meant.
        if (Owner.ParticleMeshAsset.Asset?.Hook is MeshAssetHook assetHook && assetHook.IsValid)
            return assetHook.GodotMesh;

        // Otherwise a procedural mesh component, which builds itself and hands over the ArrayMesh its own
        // hook made - the same resolution MeshRendererHookBase does.
        if (Owner.ParticleMesh.Target is Lumora.Core.Components.Meshes.ProceduralMesh procedural
            && !procedural.IsDestroyed
            && procedural.Hook is MeshHook meshHook)
        {
            WatchMeshSource(meshHook);
            return meshHook.GodotMesh;
        }

        WatchMeshSource(null);
        return null;
    }

    // Identical procedural meshes share one Godot mesh and a mesh that changes can move to another; the
    // particle mesh follows it on the next apply.
    private MeshHook? _meshSource;

    private void WatchMeshSource(MeshHook? source)
    {
        if (ReferenceEquals(_meshSource, source))
            return;
        if (_meshSource != null)
            _meshSource.MeshReplaced -= OnMeshSourceReplaced;
        _meshSource = source;
        if (source != null)
            source.MeshReplaced += OnMeshSourceReplaced;
    }

    private void OnMeshSourceReplaced()
    {
        if (Owner is { IsDestroyed: false })
            Owner.MarkChangeDirty();
    }

    // The authored material, when the system names one. Kept separate from the built-in shader so a system
    // with no material of its own is untouched.
    private Material? _authoredMaterial;

    private void ApplyAuthoredMaterial()
    {
        if (_instance == null || !GodotObject.IsInstanceValid(_instance))
            return;

        var authored = Owner.Material.Asset?.GodotMaterial as Material;
        if (ReferenceEquals(authored, _authoredMaterial))
            return;

        _authoredMaterial = authored;
        _instance.MaterialOverride = authored ?? _material;
    }

    private StrandSurface? _trails;
    private StrandSurface? _ribbons;

    private ParticleLightArbiter? _lights;
    private OmniLight3D[] _lightNodes = System.Array.Empty<OmniLight3D>();

    public static IHook<LumoraParticleSystem> Constructor() => new ParticleSystemHook();

    public override void Initialize()
    {
        base.Initialize();

        _processNode = new ParticleProcessNode(this) { Name = "ParticleSystem" };
        attachedNode.AddChild(_processNode);

        _particleMesh = new SphereMesh
        {
            Radius = 1.0f,
            Height = 2.0f,
            RadialSegments = 8,
            Rings = 4
        };

        _material = new ShaderMaterial();
        if (ResourceLoader.Exists(ShaderPath))
            _material.Shader = GD.Load<Shader>(ShaderPath);
        else
            LumoraLogger.Warn($"ParticleSystemHook: Particle shader not found at {ShaderPath}");

        _multiMesh = new MultiMesh { Mesh = _particleMesh };
        _instance = new MultiMeshInstance3D
        {
            Name = "ParticleMultiMesh",
            Multimesh = _multiMesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _processNode.AddChild(_instance);

        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        if (_multiMesh == null || _instance == null)
            return;

        var renderQueue = System.Math.Clamp(Owner.RenderQueue.Value, -100, 10000);
        _material.RenderPriority = System.Math.Clamp(renderQueue, -128, 127);
        _instance.SortingOffset = renderQueue;
        // The COMPONENT being enabled is not the same question as the object being on.
        //
        // A disabled slot anywhere up the chain turns everything under it off, and an imported avatar
        // leans on that: a swap mesh like a wet-mode variant ships with its root slot Active=false and the
        // whole subtree - renderers, emitters, particle systems - is simply not meant to be there. Reading
        // only the component's own Enabled renders content the file explicitly turned off. -xlinka
        // RenderParticles off is a ribbons-only system: it simulates and emits, and draws no sprite.
        _instance.Visible = Owner.RenderParticles.Value && Owner.Enabled.Value && (Owner.Slot?.IsActive ?? false);
        ApplyBlendMode();
        ApplyAuthoredMaterial();
        _material.SetShaderParameter("emission_strength", Owner.EmissionStrength.Value);
        ApplyBounds();
        ApplyLodRange();
        ApplyLightFade();
    }

    // A LOD group or a distance cull over this slot gets to speak here like it does for any mesh, and the
    // system's own MaxViewDistance is the other half. The engine side stops STEPPING the sim past the same
    // distance (ParticleSystem.IsBeyondViewDistance) - this only stops the drawing, and the two have to be
    // measured against the same number or a system fades back in holding a frozen buffer. -xlinka
    private LodVisibilityRange _lodRange = LodVisibilityRange.Unbounded;

    public void SetLodVisibilityRange(in LodVisibilityRange range)
    {
        if (_lodRange.Equals(range))
            return;
        _lodRange = range;
        ApplyLodRange();
    }

    private void ApplyLodRange()
    {
        if (Owner == null || _instance == null || !GodotObject.IsInstanceValid(_instance))
            return;

        var range = EffectiveRange();
        range.ApplyTo(_instance);
        _trails?.ApplyLodRange(in range);
        _ribbons?.ApplyLodRange(in range);
    }

    private LodVisibilityRange EffectiveRange()
    {
        float distance = System.Math.Max(0f, Owner.MaxViewDistance.Value);
        var own = LodVisibilityRange.To(
            distance,
            distance > 0f ? LumoraParticleSystem.ViewDistanceFadeMargin : 0f);
        return LodVisibilityRange.Tightest(in _lodRange, in own);
    }

    private void ApplyBounds()
    {
        // Generous custom AABB: particles are sim'd engine-side in slot-local space, so the extents plus
        // travel headroom keeps Godot from frustum-culling a system whose node origin is off screen.
        var extents = Owner.EmitterExtents.Value;
        float height = System.Math.Max(Owner.InitialSpeed.Value * Owner.Lifetime.Value * 1.8f, 1f);
        float pad = System.Math.Max(System.Math.Max(extents.x, extents.z), 1f);
        _instance.CustomAabb = new Aabb(
            new Vector3(-pad, -height, -pad),
            new Vector3(pad * 2f, height * 2f, pad * 2f));
    }

    public override void Destroy(bool destroyingWorld)
    {
        WatchMeshSource(null);
        if (!destroyingWorld)
        {
            _trails?.Dispose();
            _ribbons?.Dispose();
            if (_processNode != null && GodotObject.IsInstanceValid(_processNode))
                _processNode.QueueFree();
        }
        _trails = null;
        _ribbons = null;
        _lights = null;
        _lightNodes = System.Array.Empty<OmniLight3D>();
        base.Destroy(destroyingWorld);
    }

    internal void PullRender(float delta)
    {
        if (Owner == null || Owner.IsDestroyed || _multiMesh == null)
            return;

        // Not the same question as "is the node visible": Godot's visibility range hides the mesh, but a
        // promoted light carries no range of its own and the simulation stops publishing out here, so
        // both of those have to be told separately.
        bool visible = Owner.RenderParticles.Value && Owner.Enabled.Value && (Owner.Slot?.IsActive ?? false) && !Owner.IsBeyondViewDistance();

        PullInstances();
        PullStrands(visible);
        PullLights(delta, visible);
    }

    private void PullInstances()
    {
        bool wantSheet = Owner.HasFlipbook;
        if (wantSheet != _useSheet)
        {
            // Custom data changes the instance stride, so the whole buffer and the MultiMesh allocation
            // have to be rebuilt rather than resized.
            _useSheet = wantSheet;
            _capacity = 0;
            _lastRenderVersion = -1;
        }
        ApplySheet(wantSheet);

        if (Owner.RenderVersion == _lastRenderVersion)
            return;
        _lastRenderVersion = Owner.RenderVersion;

        var positions = Owner.RenderPositions;
        var sizes = Owner.RenderSizes;
        var colors = Owner.RenderColors;
        int count = Owner.ParticleCount;
        if (positions == null || count <= 0)
        {
            _multiMesh.VisibleInstanceCount = 0;
            return;
        }

        int stride = _useSheet ? SheetStride : InstanceStride;
        if (_capacity < count)
        {
            _capacity = count;
            _multiMesh.InstanceCount = 0;
            _multiMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
            _multiMesh.UseColors = true;
            _multiMesh.UseCustomData = _useSheet;
            _multiMesh.InstanceCount = _capacity;
            _buffer = new float[_capacity * stride];
        }

        // Two loops rather than one with a branch inside: the rotation path costs a quaternion-to-basis
        // per particle and most systems never spin anything, so the sim tells us whether it is even
        // integrating rotation and the common case stays a plain diagonal write. -xlinka
        if (Owner.HasRotations)
            WriteRotatedInstances(positions, sizes, colors, Owner.RenderRotations, count, stride);
        else
            WriteAxisAlignedInstances(positions, sizes, colors, count, stride);

        if (_useSheet)
            WriteSheetFrames(count, stride);

        for (int i = count; i < _capacity; i++)
            System.Array.Clear(_buffer, i * stride, stride);

        _multiMesh.Buffer = _buffer;
        _multiMesh.VisibleInstanceCount = count;
    }

    private void WriteAxisAlignedInstances(float3[] positions, float3[] sizes, colorHDR[] colors, int count, int stride)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * stride;
            var p = positions[i];
            var s = sizes[i];
            // Row-major 3x4: per-axis scale on the diagonal, position in the 4th column.
            _buffer[o + 0] = s.x; _buffer[o + 1] = 0f; _buffer[o + 2] = 0f; _buffer[o + 3] = p.x;
            _buffer[o + 4] = 0f; _buffer[o + 5] = s.y; _buffer[o + 6] = 0f; _buffer[o + 7] = p.y;
            _buffer[o + 8] = 0f; _buffer[o + 9] = 0f; _buffer[o + 10] = s.z; _buffer[o + 11] = p.z;
            var c = colors[i];
            _buffer[o + 12] = c.r;
            _buffer[o + 13] = c.g;
            _buffer[o + 14] = c.b;
            _buffer[o + 15] = c.a;
        }
    }

    private void WriteRotatedInstances(float3[] positions, float3[] sizes, colorHDR[] colors, floatQ[] rotations, int count, int stride)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * stride;
            var p = positions[i];
            var s = sizes[i];
            var q = rotations[i];

            // Basis columns straight from the quaternion, each scaled by its own axis size.
            float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z;
            float xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
            float wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;

            float m00 = (1f - 2f * (yy + zz)) * s.x;
            float m10 = (2f * (xy + wz)) * s.x;
            float m20 = (2f * (xz - wy)) * s.x;
            float m01 = (2f * (xy - wz)) * s.y;
            float m11 = (1f - 2f * (xx + zz)) * s.y;
            float m21 = (2f * (yz + wx)) * s.y;
            float m02 = (2f * (xz + wy)) * s.z;
            float m12 = (2f * (yz - wx)) * s.z;
            float m22 = (1f - 2f * (xx + yy)) * s.z;

            _buffer[o + 0] = m00; _buffer[o + 1] = m01; _buffer[o + 2] = m02; _buffer[o + 3] = p.x;
            _buffer[o + 4] = m10; _buffer[o + 5] = m11; _buffer[o + 6] = m12; _buffer[o + 7] = p.y;
            _buffer[o + 8] = m20; _buffer[o + 9] = m21; _buffer[o + 10] = m22; _buffer[o + 11] = p.z;
            var c = colors[i];
            _buffer[o + 12] = c.r;
            _buffer[o + 13] = c.g;
            _buffer[o + 14] = c.b;
            _buffer[o + 15] = c.a;
        }
    }

    private void WriteSheetFrames(int count, int stride)
    {
        var frames = Owner.RenderFrames;
        int frameCount = Owner.FlipbookFrameCount;
        int available = frames?.Length ?? 0;

        for (int i = 0; i < count; i++)
        {
            int o = i * stride;
            float frame = i < available ? frames![i] : 0f;
            _buffer[o + 16] = FlipbookAtlas.Pack(frame, frameCount);
            _buffer[o + 17] = 0f;
            _buffer[o + 18] = 0f;
            _buffer[o + 19] = 0f;
        }
    }

    private void ApplySheet(bool wantSheet)
    {
        int columns = wantSheet ? Owner.FlipbookColumns : 1;
        int rows = wantSheet ? Owner.FlipbookRows : 1;
        int frames = wantSheet ? Owner.FlipbookFrameCount : 1;

        Texture2D? texture = null;
        if (Owner.Texture.Target?.Asset?.Hook is IGodotTexture hook && hook.IsValid)
            texture = hook.GodotTexture2D;

        // An authored per-particle MESH beats everything below it.
        //
        // A style can draw an arbitrary mesh per particle, and most effects on a real avatar do
        // exactly that - splashes, drips, debris. There is nowhere for that to land in a
        // billboard-or-sphere renderer, so all of them came out as balls. When one is set, hand it
        // straight to the MultiMesh; the sprite path below is what everything else still uses. -xlinka
        var authoredMesh = ResolveParticleMesh();
        if (authoredMesh == null)
            ReportMeshWait();
        if (authoredMesh != null)
        {
            if (!ReferenceEquals(_authoredMesh, authoredMesh))
            {
                _authoredMesh = authoredMesh;
                _multiMesh.Mesh = authoredMesh;
                _sheetTexture = null;   // force the uniforms below to re-push for the new mesh

                // Said once, when the mesh actually ARRIVES. Wiring it up and it decoding are two
                // different events, and the gap between them is where this silently fell back to a sphere
                // last time: the reference was set, the asset never loaded, and nothing said so. -xlinka
                if (!_authoredMeshLogged)
                {
                    _authoredMeshLogged = true;
                    // The AABB and a live particle size together, because the scale question can only be
                    // answered by the pair. The built-in meshes are 2 units across by convention and the
                    // instance transform multiplies that by the particle size directly; an authored mesh
                    // carries whatever size its artist gave it, so the same size value lands somewhere
                    // else entirely. -xlinka
                    var aabb = authoredMesh.GetAabb();
                    var sizes = Owner.RenderSizes;
                    string firstSize = sizes != null && sizes.Length > 0
                        ? $"{sizes[0].x:F3},{sizes[0].y:F3},{sizes[0].z:F3}"
                        : "none yet";

                    // The colour the SIM produced, and whether the material is set up to read it. White
                    // particles are either a sim that was never given a colour or a material that ignores
                    // the one it gets, and those are opposite fixes. -xlinka
                    var cols = Owner.RenderColors;
                    string firstColor = cols != null && cols.Length > 0
                        ? $"{cols[0].r:F2},{cols[0].g:F2},{cols[0].b:F2},{cols[0].a:F2}"
                        : "none yet";
                    string readsColor = "n/a";
                    if (Owner.Material.Asset?.GodotMaterial is ShaderMaterial shaderMat)
                    {
                        var flag = shaderMat.GetShaderParameter("use_vertex_color");
                        readsColor = flag.VariantType == Variant.Type.Nil ? "no such uniform" : flag.ToString();
                    }
                    LumoraLogger.Debug($"ParticleSystemHook '{Owner.Slot?.SlotName.Value}': drawing authored mesh "
                                   + $"({authoredMesh.GetSurfaceCount()} surface(s)) "
                                   + $"aabb={aabb.Size.X:F3}x{aabb.Size.Y:F3}x{aabb.Size.Z:F3} "
                                   + $"particleSize=({firstSize}) slotScale={Owner.Slot?.GlobalScale.x:F3} "
                                   + $"slotActive={Owner.Slot?.IsActive} visible={_instance?.Visible} "
                                   + $"color=({firstColor}) useVertexColor={readsColor} material="
                                   + $"{(Owner.Material.Asset?.GodotMaterial != null ? "authored" : "engine shader")}");
                }
            }
        }
        else if (_authoredMesh != null)
        {
            _authoredMesh = null;
            _sheetTexture = null;
        }

        // ANY texture gets the quad, not just a flipbook.
        //
        // The sphere is what a particle falls back to when there is nothing else to draw, and for a long
        // time it was the only thing here - which meant every textured system wrapped its sprite around a
        // low-poly ball and every imported effect arrived as a cloud of beads. A sprite is a flat thing
        // facing the viewer; that is what the texture was drawn for. Systems with NO texture keep the
        // sphere exactly as before, so nothing that never had one changes.
        //
        // Rotation still wins over billboarding when the author asked for it, because a system running an
        // orientation module has already said what it wants the particle to face. -xlinka
        bool useQuad = wantSheet || texture != null;
        bool billboard = useQuad && !Owner.HasRotations;

        if (columns == _sheetColumns && rows == _sheetRows && frames == _sheetFrames
            && ReferenceEquals(texture, _sheetTexture) && billboard == _sheetBillboard)
            return;

        _sheetColumns = columns;
        _sheetRows = rows;
        _sheetFrames = frames;
        _sheetTexture = texture;
        _sheetBillboard = billboard;

        if (_authoredMesh != null)
        {
            _multiMesh.Mesh = _authoredMesh;
        }
        else if (useQuad)
        {
            _sheetMesh ??= new QuadMesh { Size = new Vector2(2f, 2f) };
            _multiMesh.Mesh = _sheetMesh;
        }
        else
        {
            _multiMesh.Mesh = _particleMesh;
        }

        _material.SetShaderParameter("flipbook_texture", texture!);
        _material.SetShaderParameter("flipbook_sheet",
            new Vector4(columns, rows, frames, texture != null ? 1f : 0f));
        _material.SetShaderParameter("sheet_billboard", billboard);
    }

    private void PullStrands(bool visible)
    {
        var trails = Owner.TrailStrands;
        var ribbons = Owner.RibbonStrands;
        if (trails == null && ribbons == null && _trails == null && _ribbons == null)
            return;

        var view = ViewPosition();
        var trailsLook = Owner.TrailsLook;
        var ribbonsLook = Owner.RibbonsLook;
        UpdateStrand(ref _trails, trails, "ParticleTrails", visible, in view,
            trailsLook?.Material.Asset?.GodotMaterial as Material, trailsLook?.GenerateLightingData.Value ?? false);
        UpdateStrand(ref _ribbons, ribbons, "ParticleRibbons", visible, in view,
            ribbonsLook?.Material.Asset?.GodotMaterial as Material, ribbonsLook?.GenerateLightingData.Value ?? false);
    }

    private void UpdateStrand(ref StrandSurface? surface, ParticleStrandOutput? output, string name, bool visible, in float3 view,
        Material? authored, bool lighting)
    {
        if (output == null)
        {
            surface?.Dispose();
            surface = null;
            return;
        }

        if (surface == null)
        {
            surface = new StrandSurface(_processNode, name);
            var range = EffectiveRange();
            surface.ApplyLodRange(in range);
        }

        surface.ApplyMaterial(Owner.EmissionStrength.Value, Owner.RenderQueue.Value, Owner.StrandTexture.Target);
        surface.ApplyAuthored(authored, lighting);
        surface.Visible = visible;
        if (!visible)
            return;

        var parameters = StrandGeometryParams.From(output);
        surface.Rebuild(
            output.Positions.AsSpan(0, output.PointCount),
            output.Colors.AsSpan(0, output.PointCount),
            output.Widths.AsSpan(0, output.PointCount),
            output.Strands.AsSpan(0, output.StrandCount),
            output.StrandCount,
            in parameters,
            in view,
            output.Version);
    }

    // The rendering camera's position in the system's own space, which is the space the strands are
    // published in. Falls back to the slot origin when nothing is drawing yet (a headless host, a node
    // not yet in the tree): a camera-facing strand still needs SOME axis to twist around and a
    // degenerate one is worse than an arbitrary one.
    private float3 ViewPosition()
    {
        if (_processNode == null || !GodotObject.IsInstanceValid(_processNode) || !_processNode.IsInsideTree())
            return float3.Zero;

        var camera = _processNode.GetViewport()?.GetCamera3D();
        if (camera == null || !GodotObject.IsInstanceValid(camera))
            return float3.Zero;

        var local = _processNode.GlobalTransform.AffineInverse() * camera.GlobalPosition;
        return new float3(local.X, local.Y, local.Z);
    }

    private void PullLights(float delta, bool visible)
    {
        int budget = System.Math.Min(Owner.MaxLightCandidates, MaxPromotedLights);
        if (budget <= 0 && _lights == null)
            return;

        _lights ??= new ParticleLightArbiter();
        _lights.Update(Owner.LightCandidates, Owner.LightCandidateCount, budget, delta, visible);
        ApplyLights();
    }

    private void ApplyLights()
    {
        var arbiter = _lights;
        if (arbiter == null || _processNode == null || !GodotObject.IsInstanceValid(_processNode))
            return;

        if (_lightNodes.Length < arbiter.Capacity)
            System.Array.Resize(ref _lightNodes, arbiter.Capacity);

        for (int i = 0; i < arbiter.Capacity; i++)
        {
            var node = _lightNodes[i];
            if (!arbiter.IsLit(i))
            {
                if (node != null && GodotObject.IsInstanceValid(node))
                    node.Visible = false;
                continue;
            }

            if (node == null || !GodotObject.IsInstanceValid(node))
            {
                node = new OmniLight3D
                {
                    Name = $"ParticleLight{i}",
                    // Shadow passes are what a particle light actually costs, and a spark casting a
                    // shadow map is not what anyone attached this for. Off unless someone comes back and
                    // makes it a real option with a real budget behind it. -xlinka
                    ShadowEnabled = false,
                };
                ApplyLightFade(node);
                _processNode.AddChild(node);
                _lightNodes[i] = node;
            }

            ref var light = ref arbiter.Lights[i];
            node.Position = new Vector3(light.Position.x, light.Position.y, light.Position.z);
            node.LightColor = new Color(light.Color.r, light.Color.g, light.Color.b, 1f);
            node.LightEnergy = light.Intensity * light.Ramp;
            node.OmniRange = System.Math.Max(0.01f, light.Range);
            node.Visible = true;
        }
    }

    private void ApplyLightFade()
    {
        for (int i = 0; i < _lightNodes.Length; i++)
        {
            var node = _lightNodes[i];
            if (node != null && GodotObject.IsInstanceValid(node))
                ApplyLightFade(node);
        }
    }

    // A light is not a GeometryInstance3D, so the visibility range the mesh uses does not exist on it.
    // Distance fade is the equivalent knob, and the arbiter puts them all out past the same cut anyway.
    private void ApplyLightFade(Light3D node)
    {
        float distance = System.Math.Max(0f, Owner.MaxViewDistance.Value);
        if (distance <= 0f)
        {
            node.DistanceFadeEnabled = false;
            return;
        }

        node.DistanceFadeEnabled = true;
        node.DistanceFadeBegin = System.Math.Max(0f, distance - LumoraParticleSystem.ViewDistanceFadeMargin);
        node.DistanceFadeLength = LumoraParticleSystem.ViewDistanceFadeMargin;
    }

    // Node3D, NOT a plain Node: Godot only propagates visibility down through Node3D ancestors, so a plain
    // Node between WorldRoot and the MultiMeshInstance severs the chain - backgrounding a world (WorldRoot
    // Visible=false) then failed to hide the particles while ProcessMode still froze them, leaving frozen
    // particles bleeding into the next world. Kept at identity; particle transforms live in the buffer. -xlinka
    private sealed partial class ParticleProcessNode : Node3D
    {
        private readonly ParticleSystemHook _hook;
        public ParticleProcessNode(ParticleSystemHook hook) => _hook = hook;
        public ParticleProcessNode() => _hook = null!;

        public override void _Process(double delta)
        {
            _hook?.PullRender((float)delta);
        }
    }
}
