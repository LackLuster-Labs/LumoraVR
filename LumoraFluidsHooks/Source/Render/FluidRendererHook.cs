// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Godot.Helpers;
using Lumora.Godot.Hooks;

namespace Lumora.Fluids.Hooks;

// Draws a FluidRenderer: the module's own mesh (FluidSurfaceBuffers: bulk, tubes, spray) on a MeshInstance3D under
// the slot's node, so it follows the slot like any renderer, with every buffer written on the GPU by the surface
// pass. The device records each renderer of a world (InWorld, then SetBounds and Record) right after that world's
// solver frame, from FluidSurfaceSource.FromSolver with the renderer's own system and frame. On a machine that does
// not simulate it pulls its system's ledger cells itself and draws them as blobs (FluidViewOnlyBlobs).
//
// Material: the renderer's own, any provider; with none, the FluidMaterial beside the renderer or its system.
// Strands and spray always wear the edge material, built here from that FluidMaterial's values. Godot never sees
// the compute writes, so the bounds are set by hand; while the surface moves under a shadow-casting light the
// custom AABB is set again at the moving-shadow rate, which is what makes a spot or omni light render its shadow
// again (measured: stale without it, within 7 mm with it).
//
// Near the eyes: while the local head is within the cut radius of the fluid's bounds, the mesh moves onto the
// local head band (every view but the eyes) and a twin of it on the eyes-only band draws in the Near variants,
// which discard what is within 15 cm of the eye drawing it. Mirrors and cameras keep the whole fluid, and the rest
// of the time nothing in the bulk discards, so it keeps early depth. -xlinka
[ImplementableHook(typeof(FluidRenderer))]
public sealed class FluidRendererHook : ComponentHook<FluidRenderer>, ILodRangeTarget
{
    // m
    public const float NearEyeRadius = 0.15f;
    // s between looks at a view-only renderer's ledger
    public const double LedgerCheckInterval = 0.25;

    private static readonly Dictionary<World, List<FluidRendererHook>> _byWorld = new();
    private static readonly List<FluidRendererHook> _viewOnly = new();
    private static bool _viewOnlyTicking;
    private static bool _watchingPath;
    private static FluidGpuPath _drawPath = FluidGpuPath.Pending;

    private MeshInstance3D? _node;
    // the eyes-only twin: a raw instance of the same mesh, owned here and freed before the mesh
    private Rid _nearInstance;
    private bool _nearVisible;
    private Transform3D _nearTransform;
    private FluidSurfaceBuffers? _surfaces;
    private FluidSurfacePass? _pass;
    private FluidViewOnlyBlobs? _blobs;
    private FluidAirBubbles? _air;
    private bool _airCreationFailed;
    private bool _lastAirEnabled;
    private float _lastAirAmount = float.NaN;
    private ShaderMaterial? _edge;
    private ShaderMaterial? _nearEdge;
    private ShaderMaterial? _nearBulk;
    private ShaderMaterial? _blobMaterial;
    private global::Godot.Material? _bulk;
    private FluidMaterial? _edgeSource;
    private FluidGpuTier _tier;
    private LodVisibilityRange _lod = LodVisibilityRange.Unbounded;
    private Aabb _bounds = new(new Vector3(-1, -1, -1), new Vector3(2, 2, 2));
    private double _boundsSetAt = double.NegativeInfinity;
    private int _edgeRefresh;
    private uint _layers = RenderHelper.PUBLIC_LAYER;
    private bool _near;
    private readonly uint[] _hidden = new uint[8];
    private World? _world;
    private ulong _cellsHash;
    private double _cellsCheckedAt = double.NegativeInfinity;
    private bool _materialAsked;
    private int _automaticTriangles;
    private int _automaticFields;
    private int _automaticTubeVertices;
    private uint[]? _surfaceCounts;
    private bool _readingCounts;
    private bool _destroying;
    private int _countsFrame;
    private bool _capacityFailed;
    private bool _retryCapacity;

    public FluidSurfaceBuffers? Surfaces => _surfaces;

    public FluidSurfacePass? Pass => _pass;

    // The view-only path's blobs, written from the system's ledger cells.
    public FluidViewOnlyBlobs? ViewOnlyBlobs => _blobs;

    // Whether the eyes draw the near-cut twin right now.
    public bool NearEye => _near;

    public MeshInstance3D? SurfaceNode => _node;

    // Whether the eyes-only twin is drawn, and the bands it is on.
    public bool NearEyeVisible => _nearVisible;

    public uint NearEyeLayers => (uint)RenderHelper.EYES_ONLY_LAYER;

    public Aabb Bounds => _bounds;

    public override void Initialize()
    {
        base.Initialize();
        FluidGodotStart.EnsureStarted();
        if (!_watchingPath)
        {
            _watchingPath = true;
            _drawPath = FluidGpu.DrawPath;
            RenderingServer.FramePreDraw += WatchDrawPath;
        }
        _world = Owner.World;
        if (_world != null)
        {
            if (!_byWorld.TryGetValue(_world, out var list))
                _byWorld[_world] = list = new List<FluidRendererHook>();
            list.Add(this);
        }
    }

    // Every renderer of a world from one source, as is (a seeded scene, a test). The device records each
    // renderer itself, with its own system index, frame and bounds.
    public static int RecordWorld(World world, in FluidSurfaceSource source)
    {
        if (!_byWorld.TryGetValue(world, out var list))
            return 0;
        int dispatches = 0;
        foreach (var hook in list)
            dispatches += hook.Record(source);
        return dispatches;
    }

    public static IReadOnlyList<FluidRendererHook> InWorld(World world)
        => _byWorld.TryGetValue(world, out var list) ? list : Array.Empty<FluidRendererHook>();

    private static FluidGpuTier CurrentTier() => FluidGpu.Current?.Tier ?? FluidGpuTier.None;

    // The spacing the particles really have on this machine's tier, as the device sets it before every record; the
    // system's own Spacing field is 0 unless set by hand.
    private float Spacing()
    {
        var system = Owner.System.Target;
        var tier = _world != null ? FluidSpace.Find(_world)?.Tier ?? CurrentTier() : CurrentTier();
        float spacing = system != null && !system.IsDestroyed ? system.SpacingOn(tier) : 0f;
        return spacing > 0f && float.IsFinite(spacing) ? spacing : FluidSystem.DefaultSpacing;
    }

    // s of trail behind a fast particle: this world's frame, at least a 90 Hz one, so at any frame rate a stream
    // laid down a packet a frame closes up (FluidSurfacePass.TrailTime).
    public const float TrailMinTime = 1f / 90f;
    public const float TrailMaxTime = 1f / 30f;

    private FluidSurfaceCapacity CapacityFor(FluidGpuTier tier)
    {
        var c = FluidSurfaceCapacity.For(tier);
        int budget = Owner.TriangleBudget.Value > 0 ? Owner.TriangleBudget.Value : Math.Max(c.BulkTriangles, _automaticTriangles);
        int sides = Owner.StrandSides.Value > 0 ? Owner.StrandSides.Value : c.TubeSides;
        float cell = Owner.CellScale.Value > 0f ? Owner.CellScale.Value : c.CellScale;
        int fields = Owner.SurfaceWorkLimit.Value > 0 ? Owner.SurfaceWorkLimit.Value : Math.Max(c.FieldSlots, _automaticFields);
        // These bounds keep array byte counts representable. They do not depend on frame timing. -xlinka
        budget = Math.Clamp(budget, 64, int.MaxValue / 64);
        fields = Math.Clamp(fields, 1, int.MaxValue / (FluidSurfacePass.Cells * 16));
        sides = Math.Clamp(sides, 3, 64);
        int tubes = Math.Clamp(Math.Max(c.TubeVertices, _automaticTubeVertices), 1024, TubeVertexLimit());
        return new FluidSurfaceCapacity(budget, cell, sides, c.Strands, c.RadiusFloor, c.Spray, c.Smoothing, fields, c.TubeSegments, tubes);
    }

    // The tail clears one quad per thread; use the actual dispatch and index-buffer address limits. -xlinka
    private static int TubeVertexLimit()
    {
        var rd = RenderingServer.GetRenderingDevice();
        ulong groups = rd?.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupCountX) ?? 65535;
        long threads = (long)Math.Min(groups, (ulong)int.MaxValue) * FluidLayout.GroupSize;
        return (int)Math.Min(threads, FluidSurfaceCapacity.MaxTubeVertices);
    }

    private void Ensure()
    {
        var path = FluidGpu.DrawPath;
        if (path == FluidGpuPath.ViewOnly && _surfaces != null)
            DropSurfaces();
        else if (path == FluidGpuPath.Gpu && _blobs != null)
            DropBlobs();
        if (path == FluidGpuPath.Gpu && _surfaces == null)
        {
            var rd = RenderingServer.GetRenderingDevice();
            var kernels = rd != null ? FluidSurfaceKernels.For(rd, out string error) : null;
            if (rd == null || kernels == null)
                return;
            _tier = CurrentTier();
            var capacity = CapacityFor(_tier);
            _surfaces = new FluidSurfaceBuffers(capacity);
            if (!_surfaces.LayoutOk)
                Lumora.Core.Logging.Logger.Warn($"Fluids: surface layout differs from FluidMeshLayout: {_surfaces.LayoutProblems}");
            _pass = new FluidSurfacePass(rd, kernels, _surfaces, capacity);
            _node = new MeshInstance3D { Name = "FluidSurface" };
            attachedNode.AddChild(_node);
            _node.SetBase(_surfaces.Mesh);
            _node.CustomAabb = _bounds;
            var scenario = attachedNode.GetWorld3D()?.Scenario ?? default;
            if (scenario.IsValid)
            {
                _nearInstance = RenderingServer.InstanceCreate2(_surfaces.Mesh, scenario);
                RenderingServer.InstanceSetLayerMask(_nearInstance, (uint)RenderHelper.EYES_ONLY_LAYER);
                RenderingServer.InstanceSetCustomAabb(_nearInstance, _bounds);
                RenderingServer.InstanceSetVisible(_nearInstance, false);
            }
        }
        else if (path == FluidGpuPath.ViewOnly && _blobs == null)
        {
            var scenario = attachedNode.GetWorld3D()?.Scenario ?? default;
            if (!scenario.IsValid)
                return;
            _blobs = new FluidViewOnlyBlobs(2048, 0, scenario, FluidRenderLayers.For(Owner.Slot, Owner.World), 0);
            _cellsHash = 0;
            _cellsCheckedAt = double.NegativeInfinity;
            _viewOnly.Add(this);
            if (!_viewOnlyTicking)
            {
                _viewOnlyTicking = true;
                RenderingServer.FramePreDraw += ViewOnlyTick;
            }
        }
    }

    public override void ApplyChanges()
    {
        if (attachedNode == null)
            return;
        Ensure();
        if (_lastAirEnabled != Owner.AirBubbles.Value || !_lastAirAmount.Equals(Owner.AirAmount.Value))
        {
            _lastAirEnabled = Owner.AirBubbles.Value;
            _lastAirAmount = Owner.AirAmount.Value;
            _airCreationFailed = false;
            _air?.Retry();
        }
        _air?.ApplyEnabled(Owner.Enabled && Owner.AirBubbles.Value && float.IsFinite(Owner.AirAmount.Value) && Owner.AirAmount.Value > 0f);
        _retryCapacity |= _capacityFailed;
        _capacityFailed = false;
        EnsureCapacity();
        _layers = FluidRenderLayers.For(Owner.Slot, Owner.World);
        bool visible = Owner.Enabled;
        var source = MaterialSource();
        if (_node != null && _surfaces != null && _pass != null)
        {
            _node.Visible = visible;
            var shadows = source?.CastShadows.Value == true ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
            _node.CastShadow = shadows;
            if (_nearInstance.IsValid)
                RenderingServer.InstanceGeometrySetCastShadowsSetting(_nearInstance, (RenderingServer.ShadowCastingSetting)(long)shadows);
            ApplyLayers();
            _pass.Spacing = Spacing();
            _pass.CellScale = Owner.CellScale.Value > 0f ? Owner.CellScale.Value : _pass.Capacity.CellScale;
            _pass.Smoothing = Owner.Smoothing.Value;
            _pass.Strands = Owner.Strands.Value;
            _pass.Spray = Owner.Spray.Value;
            ApplyMaterials(source);
            ApplyRange();
        }
        if (_blobs != null)
        {
            _blobs.SetLayers(_layers);
            _blobs.SetVisible(visible && attachedNode.IsVisibleInTree());
            // Blobs always wear the cheap blob look, in the FluidMaterial's colours: whatever the bulk wears reads
            // vertex data the blob mesh does not have.
            _blobMaterial ??= new ShaderMaterial { Shader = GD.Load<Shader>(FluidMaterial.BlobShader) };
            CopyLookValues(source, _blobMaterial);
            _blobs.SetMaterials(_blobMaterial.GetRid(), default);
            _cellsCheckedAt = double.NegativeInfinity;
        }
    }

    // The renderer's own material, else the FluidMaterial beside it or beside its system.
    private FluidMaterial? MaterialSource()
    {
        if (Owner.Material.Target is FluidMaterial own)
            return own;
        return Owner.Slot?.GetComponent<FluidMaterial>() ?? Owner.System.Target?.Slot?.GetComponent<FluidMaterial>();
    }

    private void ApplyMaterials(FluidMaterial? source)
    {
        if (_surfaces == null)
            return;
        MaterialAsset? bulkAsset = Owner.Material.Target != null ? Owner.Material.Asset : source?.Asset;
        _bulk = bulkAsset?.GodotMaterial as global::Godot.Material;
        if (_bulk != null)
            _surfaces.SetMaterial(FluidSurfaceBuffers.Bulk, _bulk.GetRid());
        _edge ??= new ShaderMaterial { Shader = GD.Load<Shader>(FluidMaterial.EdgeShader) };
        _edgeSource = source;
        CopyEdgeValues();
        _surfaces.SetMaterial(FluidSurfaceBuffers.Tubes, _edge.GetRid());
        _surfaces.SetMaterial(FluidSurfaceBuffers.Spray, _edge.GetRid());
        BuildNearMaterials();
    }

    // The edge material follows the goo it came from; a FluidMaterial edit does not reach this hook, so the values
    // are copied again now and then.
    private void CopyEdgeValues()
    {
        if (_edge == null)
            return;
        CopyLookValues(_edgeSource, _edge);
        if (_pass != null)
            _edge.SetShaderParameter("tube_sides", (float)_pass.Capacity.TubeSides);
        if (_nearEdge != null)
        {
            CopyUniforms(_edge, _nearEdge);
            _nearEdge.SetShaderParameter("near_eye_radius", NearEyeRadius);
        }
        if (_nearBulk != null && _bulk is ShaderMaterial bulk)
            CopyUniforms(bulk, _nearBulk);
    }

    // The twin's materials: the edge with its cut on, and the bulk's own look in its Near file. A bulk material the
    // module does not ship has no Near file, and the twin then wears it as it is: no cut, but nothing lost either.
    private void BuildNearMaterials()
    {
        if (!_nearInstance.IsValid || _surfaces == null)
            return;
        _nearEdge ??= new ShaderMaterial { Shader = _edge?.Shader };
        string? nearPath = _bulk is ShaderMaterial { Shader: { } shader } ? FluidMaterial.NearVariantOf(shader.ResourcePath) : null;
        if (nearPath == null)
            _nearBulk = null;
        else if (_nearBulk?.Shader?.ResourcePath != nearPath)
            _nearBulk = new ShaderMaterial { Shader = GD.Load<Shader>(nearPath) };
        CopyEdgeValues();
        var instance = _nearInstance;
        var bulkRid = _nearBulk?.GetRid() ?? _bulk?.GetRid() ?? default;
        RenderingServer.InstanceSetSurfaceOverrideMaterial(instance, FluidSurfaceBuffers.Bulk, bulkRid);
        RenderingServer.InstanceSetSurfaceOverrideMaterial(instance, FluidSurfaceBuffers.Tubes, _nearEdge.GetRid());
        RenderingServer.InstanceSetSurfaceOverrideMaterial(instance, FluidSurfaceBuffers.Spray, _nearEdge.GetRid());
    }

    // FluidMaterial's values by uniform name, into a material this hook built from one of the shipped looks. A
    // liquid's spray, strands and view-only blobs take the clear look (liquid 1).
    private static void CopyLookValues(FluidMaterial? m, ShaderMaterial to)
    {
        if (m == null)
            return;
        var t = m.Tint.Value;
        var a = m.ShaderAbsorption();
        var e = m.Emission.Value;
        to.SetShaderParameter("tint", new Color(t.r, t.g, t.b, t.a));
        to.SetShaderParameter("absorption", new Vector4(a.x, a.y, a.z, a.w));
        to.SetShaderParameter("liquid", m.Look.Value == FluidLook.Liquid ? 1f : 0f);
        to.SetShaderParameter("colour_depth", m.Look.Value == FluidLook.Liquid ? 12f : 6f);
        to.SetShaderParameter("gloss", m.ShaderGloss());
        to.SetShaderParameter("clearcoat", m.Clearcoat.Value);
        to.SetShaderParameter("transmission", m.Transmission.Value);
        to.SetShaderParameter("foam", m.Foam.Value);
        to.SetShaderParameter("emission", new Color(e.r, e.g, e.b, e.a));
    }

    private static void CopyUniforms(ShaderMaterial from, ShaderMaterial to)
    {
        if (from.Shader == null)
            return;
        foreach (var entry in from.Shader.GetShaderUniformList())
        {
            var name = entry.AsGodotDictionary()["name"].AsStringName();
            to.SetShaderParameter(name, from.GetShaderParameter(name));
        }
    }

    // Normal bands, or near the eyes: the mesh on the local head band (every view but the eyes) and the twin on the
    // eyes-only band. Only fluid on the public band takes the twin; a selective or private band keeps its own.
    private void ApplyLayers()
    {
        if (_node == null)
            return;
        bool twin = _near && (_layers & RenderHelper.PUBLIC_LAYER) != 0;
        _node.Layers = twin ? (_layers & ~(uint)RenderHelper.PUBLIC_LAYER) | (uint)RenderHelper.LOCAL_HEAD_LAYER : _layers;
        bool visible = twin && Owner.Enabled && _node.IsVisibleInTree();
        if (_nearInstance.IsValid && visible != _nearVisible)
        {
            _nearVisible = visible;
            if (visible)
                PlaceTwin();
            RenderingServer.InstanceSetVisible(_nearInstance, visible);
        }
    }

    // The twin follows the mesh node; only while it is drawn.
    private void PlaceTwin()
    {
        if (_node == null || !_nearInstance.IsValid)
            return;
        var t = _node.GlobalTransform;
        if (t == _nearTransform)
            return;
        _nearTransform = t;
        RenderingServer.InstanceSetTransform(_nearInstance, t);
    }

    private void ApplyRange()
    {
        if (_node == null)
            return;
        float viewDistance = Owner.System.Target?.MaxViewDistance.Value ?? 0f;
        var effective = LodVisibilityRange.Tightest(in _lod, LodVisibilityRange.To(viewDistance, 0f));
        effective.ApplyTo(_node);
        if (_nearInstance.IsValid)
            RenderingServer.InstanceGeometrySetVisibilityRange(_nearInstance, effective.Begin, effective.End, effective.BeginMargin, effective.EndMargin, RenderingServer.VisibilityRangeFadeMode.Disabled);
    }

    public void SetLodVisibilityRange(in LodVisibilityRange range)
    {
        _lod = range;
        ApplyRange();
    }

    public bool TryGetLargestDimension(out float size)
    {
        var s = _bounds.Size;
        size = Math.Max(s.X, Math.Max(s.Y, s.Z));
        return size > 0f;
    }

    // Bounds in the renderer's local space: FluidSystem.Bounds when set, else the read-back bounds grown by
    // vmax x 4 frames + 0.25 m (5.2). While the surface moves, a shadow-casting light needs the AABB set again to
    // render its shadow again; that happens at the user's moving-shadow rate, never faster.
    public void SetBounds(Aabb localBounds, bool moving)
    {
        if (_node == null)
            return;
        bool changed = localBounds != _bounds;
        _bounds = localBounds;
        double now = _world?.Time.TotalTime ?? 0.0;
        int rate = EngineSettings.MovingShadowRate;
        double interval = rate <= 0 ? 0.0 : 1.0 / rate;
        bool shadowDue = moving && _node.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off && now - _boundsSetAt >= interval;
        if (changed || shadowDue)
        {
            _node.CustomAabb = _bounds;
            if (_nearInstance.IsValid)
                RenderingServer.InstanceSetCustomAabb(_nearInstance, _bounds);
            _boundsSetAt = now;
        }
    }

    // The per-origin hide table (blocked people's fluid) the source carries; a change re-extracts everything.
    private void HideOrigins(IReadOnlyList<uint>? words)
    {
        if (_pass == null)
            return;
        bool changed = false;
        for (int i = 0; i < _hidden.Length; i++)
        {
            uint word = words != null && i < words.Count ? words[i] : 0u;
            if (_hidden[i] != word)
            {
                _hidden[i] = word;
                _pass.HiddenOrigins[i] = word;
                changed = true;
            }
        }
        if (changed)
            _pass.MarkAllDirty();
    }

    private static Transform3D ToGodot(in float4x4 m)
        => new(new Basis(new Vector3(m.c0.x, m.c0.y, m.c0.z), new Vector3(m.c1.x, m.c1.y, m.c1.z), new Vector3(m.c2.x, m.c2.y, m.c2.z)),
            new Vector3(m.c3.x, m.c3.y, m.c3.z));

    // One frame of this renderer's surface. The particles' frame is the world for world systems (a container's
    // own frame for container systems); the mesh lives in the slot's space.
    public int Record(in FluidSurfaceSource source)
    {
        if (_pass == null || _node == null || !_node.IsInsideTree() || !Owner.Enabled)
            return 0;
        ReadCapacityDemand();
        EnsureCapacity();
        if (_capacityFailed)
            return 0;
        var local = source;
        local.ToLocal = _node.GlobalTransform.AffineInverse() * source.ToLocal;
        if (++_edgeRefresh >= 30)
        {
            _edgeRefresh = 0;
            CopyEdgeValues();
            AskForMaterialUpdate();
        }
        HideOrigins(source.HiddenOrigins);
        float frame = _world?.Time.Delta ?? (1f / 60f);
        _pass.TrailTime = Math.Clamp(float.IsFinite(frame) ? frame : 1f / 60f, TrailMinTime, TrailMaxTime);
        UpdateNearEye();
        if (_nearVisible)
            PlaceTwin();
        int dispatches = _pass.Record(local);
        if (_world != null && FluidSpace.Find(_world)?.Gpu is FluidDeviceWorld gpu)
        {
            if (_air == null && !_airCreationFailed && Owner.AirBubbles.Value && Owner.AirAmount.Value > 0f)
            {
                try { _air = new FluidAirBubbles(RenderingServer.GetRenderingDevice(), _node); }
                catch (Exception ex) { _airCreationFailed = true; Owner.AirStatus = "Air bubbles: " + ex.Message; }
            }
            if (_air != null)
            {
                dispatches += _air.Record(source, gpu.Solver, local.ToLocal, _bounds, _layers,
                    Owner.AirBubbles.Value, Owner.AirAmount.Value, Spacing());
                Owner.AirBubbleCount = _air.Count;
                Owner.AirAllocatedBytes = _air.AllocatedBytes;
                Owner.AirStatus = _air.Status;
                if (_air.Count > 0) FluidSpace.Find(_world)?.Wake();
            }
        }
        UpdateMeasurements();
        if (!_readingCounts && (_countsFrame++ % 15) == 0)
        {
            _readingCounts = true;
            int generation = _surfaces!.Generation;
            var callback = Callable.From<byte[]>(bytes =>
            {
                _readingCounts = false;
                if (!_destroying && _surfaces?.Generation == generation && bytes.Length >= FluidSurfacePass.CounterCount * 4)
                {
                    _surfaceCounts = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan()).ToArray();
                    if (_surfaceCounts[9] > 0 || _surfaceCounts[10] > 0 || _surfaceCounts[20] > 0 || _surfaceCounts[21] > 0 || _surfaceCounts[19] > 0)
                        FluidSpace.Find(_world)?.Wake();
                }
            });
            RenderingServer.GetRenderingDevice().BufferGetDataAsync(_pass.Counters, callback, 0, FluidSurfacePass.CounterCount * 4);
        }
        return dispatches;
    }

    // Surface work grows from its own counters. A small initial allocation is not a permanent mesh limit. -xlinka
    private void ReadCapacityDemand()
    {
        var counts = _surfaceCounts;
        _surfaceCounts = null;
        if (counts == null || _pass == null || _capacityFailed)
            return;
        bool meshFull = counts[9] > 0 || counts[20] > 0 || counts[21] > 0;
        bool fieldsFull = counts[10] > 0;
        bool tubesFull = counts[19] > 0;
        if (tubesFull)
            _automaticTubeVertices = (int)Math.Min(Math.Max(2L * _pass.Capacity.TubeVertices, Math.Max(counts[12], counts[14])), TubeVertexLimit());
        if (meshFull && Owner.TriangleBudget.Value <= 0)
            _automaticTriangles = (int)Math.Min(2L * _pass.Capacity.BulkTriangles, int.MaxValue / 64);
        if (fieldsFull && Owner.SurfaceWorkLimit.Value <= 0)
            _automaticFields = (int)Math.Min(Math.Max(2L * _pass.FieldCapacity, counts[6]), int.MaxValue / (FluidSurfacePass.Cells * 16));
        Owner.ResourceStatus = meshFull && Owner.TriangleBudget.Value > 0 ? "Triangle capacity reached; increase TriangleBudget"
            : fieldsFull && Owner.SurfaceWorkLimit.Value > 0 ? "Surface work limit reached; increase SurfaceWorkLimit"
            : tubesFull && _automaticTubeVertices <= _pass.Capacity.TubeVertices ? "Strand storage reached the graphics device address or dispatch limit"
            : meshFull || fieldsFull || tubesFull ? "Growing surface buffers" : string.Empty;
        if (meshFull || fieldsFull || tubesFull)
            FluidSpace.Find(_world)?.Wake();
    }

    private void UpdateMeasurements()
    {
        Owner.AllocatedBytes = (_surfaces?.AllocatedBytes ?? 0) + (_pass?.AllocatedBytes ?? 0) + (_air?.AllocatedBytes ?? 0);
        Owner.AllocatedTriangles = _pass?.Capacity.BulkTriangles ?? 0;
        Owner.SurfaceBricks = _pass?.FieldCapacity ?? 0;
    }

    private void EnsureCapacity()
    {
        if (_pass == null || _surfaces == null || _capacityFailed)
            return;
        var next = CapacityFor(CurrentTier());
        var current = _pass.Capacity;
        if (!_retryCapacity && CurrentTier() == _tier && next.BulkTriangles == current.BulkTriangles && next.TubeSides == current.TubeSides
            && next.FieldSlots == current.FieldSlots && next.TubeVertices == current.TubeVertices)
            return;
        // Land the small diagnostic read before replacing the buffers it refers to. -xlinka
        if (_readingCounts)
            RenderingServer.GetRenderingDevice().BufferGetData(_pass.Counters, 0, 4);
        _surfaceCounts = null;
        _retryCapacity = false;
        try
        {
            Retier();
            UpdateMeasurements();
        }
        catch (Exception ex)
        {
            _capacityFailed = true;
            Owner.ResourceStatus = "Surface allocation failed: " + ex.Message;
            _node?.SetBase(default);
            if (_nearInstance.IsValid) RenderingServer.InstanceSetVisible(_nearInstance, false);
            Lumora.Core.Logging.Logger.Warn("Fluids: " + Owner.ResourceStatus);
        }
    }

    // The near-eye twin while the local head is within the cut of the bounds (with two cells and 5 cm of slack, so
    // it does not flicker on the edge). Only the focused world's head is the eye.
    private void UpdateNearEye()
    {
        if (_node == null || _pass == null)
            return;
        bool near = false;
        var head = _world?.LocalUser?.Root?.HeadSlot;
        if (head != null && _world!.Focus is World.WorldFocus.Focused or World.WorldFocus.Overlay && _node.IsVisibleInTree())
        {
            var p = head.GlobalPosition;
            var local = _node.GlobalTransform.AffineInverse() * new Vector3(p.x, p.y, p.z);
            var lo = _bounds.Position;
            var hi = _bounds.End;
            var q = new Vector3(Math.Clamp(local.X, lo.X, hi.X), Math.Clamp(local.Y, lo.Y, hi.Y), Math.Clamp(local.Z, lo.Z, hi.Z));
            float reach = NearEyeRadius + 2f * _pass.CellSize + (_near ? 0.05f : 0f);
            near = (q - local).Length() < reach;
        }
        if (near == _near)
            return;
        _near = near;
        ApplyLayers();
    }

    // A FluidMaterial that updated before this machine reported its renderer picked blind; it updates again.
    private void AskForMaterialUpdate()
    {
        var source = MaterialSource();
        if (source == null || source.PickedForThisMachine || _materialAsked)
            return;
        _materialAsked = true;
        _world?.RunSynchronously(() =>
        {
            _materialAsked = false;
            if (!source.IsDestroyed)
                source.ForceUpdate();
        });
    }

    // A tier change rebuilds the surfaces at the new capacity; everything is extracted again next frame.
    private void Retier()
    {
        if (_surfaces == null || _pass == null)
            return;
        _tier = CurrentTier();
        var capacity = CapacityFor(_tier);
        _surfaces.Rebuild(capacity.BulkVertices, capacity.BulkQuads, capacity.TubeVertices, capacity.TubeQuads, capacity.SprayVertices, capacity.SprayQuads);
        if (!_surfaces.LayoutOk)
            throw new InvalidOperationException(_surfaces.LayoutProblems);
        _pass.Resize(capacity);
        _node?.SetBase(_surfaces.Mesh);
        CopyEdgeValues();
        BuildNearMaterials();
    }

    // THE VIEW-ONLY PATH. Nothing records on a machine that does not simulate, so its renderers look at their
    // system's ledger themselves, at FramePreDraw (after every world update, so the cells are not being written)
    // and at 4 Hz, the rate the host writes them at. A changed ledger is drawn again as blobs: one per filled cell,
    // flattened onto the system's up, its volume the cell's fill. Cells keyed to an anchor slot are left out.
    // Blobs are raw instances in the shared scenario, so they follow the slot's visibility by hand.
    private static void ViewOnlyTick()
    {
        for (int i = _viewOnly.Count - 1; i >= 0; i--)
        {
            var hook = _viewOnly[i];
            if (hook._blobs == null)
            {
                _viewOnly.RemoveAt(i);
                continue;
            }
            try
            {
                hook.ViewOnlyFrame();
            }
            catch (Exception ex)
            {
                Lumora.Core.Logging.Logger.Warn($"Fluids: view-only blobs failed: {ex.Message}");
                _viewOnly.RemoveAt(i);
            }
        }
    }

    private void ViewOnlyFrame()
    {
        if (_blobs == null || attachedNode == null || !GodotObject.IsInstanceValid(attachedNode))
            return;
        bool visible = Owner.Enabled && attachedNode.IsInsideTree() && attachedNode.IsVisibleInTree();
        _blobs.SetVisible(visible);
        if (!visible)
            return;
        _blobs.SetTransform(attachedNode.GlobalTransform);
        double now = _world?.Time.TotalTime ?? 0.0;
        if (now - _cellsCheckedAt < LedgerCheckInterval)
            return;
        _cellsCheckedAt = now;
        AskForMaterialUpdate();
        PullLedger();
    }

    // The view-only pull: reads the system's ledger through the data side's own members (FluidSystem.Cells and
    // CellTint, FluidLedger.CellSizeOf, FluidCell) and draws it again if it changed since the last pull. Main thread
    // only. Returns the blobs drawn, or -1 when nothing changed or there is nothing to draw into.
    public int PullLedger()
    {
        var system = Owner.System.Target;
        if (_blobs == null || system == null || system.IsDestroyed)
            return -1;
        ulong hash = 1469598103934665603UL;
        var cells = system.Cells;
        var tints = system.CellTint;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            hash = (hash ^ (uint)c.x) * 1099511628211UL;
            hash = (hash ^ (uint)c.y) * 1099511628211UL;
            hash = (hash ^ (uint)c.z) * 1099511628211UL;
            hash = (hash ^ (uint)c.w) * 1099511628211UL;
            if (i < tints.Count)
                hash = (hash ^ (uint)tints[i]) * 1099511628211UL;
        }
        if (hash == _cellsHash)
            return -1;
        _cellsHash = hash;
        DrawCells(system);
        return _blobs.BlobCount;
    }

    private void DrawCells(FluidSystem system)
    {
        if (_blobs == null)
            return;
        float cellSize = FluidLedger.CellSizeOf(system);
        if (!(cellSize > 0f))
            return;
        var toLocal = attachedNode.GlobalTransform.AffineInverse() * ToGodot(system.Slot.LocalToWorld);
        var up = toLocal.Basis * Vector3.Up;
        double cellVolume = (double)cellSize * cellSize * cellSize;
        var cells = system.Cells;
        var tints = system.CellTint;
        _blobs.BeginBlobs();
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            int fill = FluidCell.Fill(cell);
            if (fill == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor || (FluidCell.Flags(cell) & FluidCell.FlagFading) != 0)
                continue;
            var c = FluidCell.Centre(cell, cellSize);
            var centre = toLocal * new Vector3(c.x, c.y, c.z);
            float volume = (float)(fill / (double)FluidCell.FillMax * cellVolume);
            int packed = i < tints.Count ? tints[i] : 0;
            var tint = packed != 0 ? Unpack((uint)packed) : ToColor(system.TintOf(FluidCell.Substance(cell)));
            float wetness = (FluidCell.Flags(cell) & FluidCell.FlagCuring) != 0 ? 0.3f : 1f;
            if (!_blobs.AddBlob(centre, volume, 0.4f, up, tint, wetness))
                break;
            float reach = cellSize;
            min = new Vector3(Math.Min(min.X, centre.X - reach), Math.Min(min.Y, centre.Y - reach), Math.Min(min.Z, centre.Z - reach));
            max = new Vector3(Math.Max(max.X, centre.X + reach), Math.Max(max.Y, centre.Y + reach), Math.Max(max.Z, centre.Z + reach));
        }
        if (_blobs.BlobCount > 0)
            _blobs.SetBounds(new Aabb(min, max - min));
        _blobs.CommitBlobs();
    }

    private static Color ToColor(in colorHDR c) => new(c.r, c.g, c.b, c.a);

    // RGBA8, r in the low byte.
    private static Color Unpack(uint v)
        => new((v & 255) / 255f, ((v >> 8) & 255) / 255f, ((v >> 16) & 255) / 255f, ((v >> 24) & 255) / 255f);

    // The quality pick can turn view only on or off mid-session. Every renderer then rebuilds the other way, from its
    // own world's update.
    private static void WatchDrawPath()
    {
        var path = FluidGpu.DrawPath;
        if (path == _drawPath)
            return;
        _drawPath = path;
        foreach (var list in _byWorld.Values)
        {
            foreach (var hook in list)
                hook.AskForApply();
        }
    }

    private void AskForApply()
    {
        var world = _world;
        if (world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (!Owner.IsDestroyed)
                Owner.MarkChangeDirty();
        });
    }

    // Nothing may still draw the mesh when it goes: the twin is freed, the node lets go of it, then the mesh. The
    // bulk material belongs to the material's asset, which a world teardown may already have freed, so every
    // material reference is dropped first: an instance or surface left pointing at a freed material is read
    // again when it is freed.
    private void DropSurfaces()
    {
        _air?.Dispose();
        _air = null;
        Owner.AirBubbleCount = 0;
        Owner.AirAllocatedBytes = 0;
        Owner.AirStatus = string.Empty;
        if (_readingCounts && _pass != null)
            RenderingServer.GetRenderingDevice().BufferGetData(_pass.Counters, 0, 4);
        _readingCounts = false;
        _surfaceCounts = null;
        Owner.AllocatedBytes = 0;
        Owner.AllocatedTriangles = 0;
        Owner.SurfaceBricks = 0;
        _pass?.Dispose();
        _pass = null;
        if (_surfaces != null)
        {
            for (int i = 0; i < FluidSurfaceBuffers.SurfaceCount; i++)
                _surfaces.SetMaterial(i, default);
        }
        if (_nearInstance.IsValid)
        {
            for (int i = 0; i < FluidSurfaceBuffers.SurfaceCount; i++)
                RenderingServer.InstanceSetSurfaceOverrideMaterial(_nearInstance, i, default);
            RenderingServer.FreeRid(_nearInstance);
        }
        _nearInstance = default;
        _nearVisible = false;
        _near = false;
        if (_node != null && GodotObject.IsInstanceValid(_node))
        {
            _node.SetBase(default);
            _node.QueueFree();
        }
        _node = null;
        _surfaces?.Dispose();
        _surfaces = null;
        _capacityFailed = false;
        _retryCapacity = false;
    }

    private void DropBlobs()
    {
        _viewOnly.Remove(this);
        _blobs?.Dispose();
        _blobs = null;
    }

    public override void Destroy(bool destroyingWorld)
    {
        _destroying = true;
        if (_world != null && _byWorld.TryGetValue(_world, out var list))
        {
            list.Remove(this);
            if (list.Count == 0)
                _byWorld.Remove(_world);
        }
        DropSurfaces();
        DropBlobs();
        // Materials this hook made are let go now, while the renderer is still there, not by a finalizer later.
        _edge?.Dispose();
        _edge = null;
        _nearEdge?.Dispose();
        _nearEdge = null;
        _nearBulk?.Dispose();
        _nearBulk = null;
        _blobMaterial?.Dispose();
        _blobMaterial = null;
        base.Destroy(destroyingWorld);
    }
}
