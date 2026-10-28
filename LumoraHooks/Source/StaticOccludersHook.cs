// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Phos;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// The occluders a StaticOccluders asks for. Each chosen surface gets one built from the triangles its solid
// materials draw, hung under the renderer's own instance: it goes where the instance goes and shows when the
// instance shows, so a surface switched off, a world sent to the background or a wall someone carries off take
// theirs along with nothing kept here. Only views that cull by occluders use them: the desktop view and cameras
// asked to, never a mirror or a portal (they film from behind their own glass) and never a headset, whose two eyes
// see round edges one centre view would call hidden.
//
// A pick runs once nothing under the slot has changed for a couple of seconds, and comes round again while
// renderers are still waiting for their meshes or materials. Picking is the only work; the renderer does the
// culling. -xlinka
[ImplementableHook(typeof(StaticOccluders))]
public sealed class StaticOccludersHook : ComponentHook<StaticOccluders>
{
    private const float SettleSeconds = 2f;
    private const int PendingRounds = 30;

    private enum Verdict { No, Yes, Waiting }

    private sealed class Entry
    {
        public MeshInstance3D Instance = null!;
        public Mesh Mesh = null!;
        public Aabb Bounds;
        public OccluderInstance3D Node = null!;
        public int Triangles;
        public int Pass;
    }

    private readonly Dictionary<MeshRenderer, Entry> _entries = new();
    private readonly List<MeshRenderer> _renderers = new();
    private readonly List<MeshRenderer> _gone = new();
    private readonly List<int> _indices = new();
    private int _pass;
    private int _rounds;
    private bool _waiting;
    private double _due;
    private bool _subscribed;
    private bool _reported;

    public static IHook<StaticOccluders> Constructor() => new StaticOccludersHook();

    protected override bool ReadsNodePose => false;

    private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    private static readonly bool OcclusionCulling
        = ProjectSettings.GetSetting("rendering/occlusion_culling/use_occlusion_culling", false).AsBool();

    public override void Initialize()
    {
        base.Initialize();
        Owner.Invalidated += OnInvalidated;
        _subscribed = true;
    }

    public override void ApplyChanges()
    {
        if (!Owner.Enabled)
        {
            Clear();
            return;
        }
        _rounds = 0;
        Later();
    }

    // Structure changes come from the sync thread as well; the pick waits for the world thread.
    private void OnInvalidated()
    {
        var world = Owner?.World;
        world?.RunSynchronously(() =>
        {
            if (Owner is { IsDestroyed: false } && Owner.Enabled)
            {
                _rounds = 0;
                Later();
            }
        });
    }

    // Every change pushes the pick back, so a world arriving piece by piece is picked once it has arrived.
    private void Later()
    {
        _due = Now + SettleSeconds;
        if (_waiting || Owner?.World == null)
            return;
        _waiting = true;
        Owner.World.RunInSeconds(SettleSeconds, Tick);
    }

    private void Tick()
    {
        _waiting = false;
        if (Owner == null || Owner.IsDestroyed || !Owner.Enabled)
            return;
        double left = _due - Now;
        if (left > 0.05)
        {
            _waiting = true;
            Owner.World.RunInSeconds((float)left, Tick);
            return;
        }
        Pick();
    }

    private void Pick()
    {
        var slot = Owner.Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        // Nothing to build for while the renderer's occlusion culling is off in the project (the default), which is
        // read once at start and cannot be turned on later. A headset drawing through the main window's own viewport
        // tests occluders from one centre view, and two eyes see round edges it would call hidden; switching that
        // viewport's culling at run time left the frame's draw three times slower, so a headset there gets none.
        // -xlinka
        if (!OcclusionCulling || (global::Godot.Engine.GetMainLoop() as SceneTree)?.Root is { UseXR: true })
        {
            Clear();
            return;
        }
        _pass++;
        float minimum = Owner.MinimumSize.Value;
        int maxTriangles = Owner.MaximumTriangles.Value;
        int waiting = 0;
        _renderers.Clear();
        slot.GetComponentsInChildren(_renderers);
        foreach (var renderer in _renderers)
        {
            if (renderer == null || renderer.IsDestroyed)
                continue;
            if (Consider(renderer, minimum, maxTriangles) == Verdict.Waiting)
                waiting++;
        }
        _renderers.Clear();

        _gone.Clear();
        foreach (var (renderer, entry) in _entries)
        {
            if (entry.Pass != _pass)
                _gone.Add(renderer);
        }
        foreach (var renderer in _gone)
        {
            Free(_entries[renderer]);
            _entries.Remove(renderer);
        }
        _gone.Clear();

        if (!_reported || waiting == 0)
        {
            _reported = waiting == 0;
            int triangles = 0;
            foreach (var entry in _entries.Values)
                triangles += entry.Triangles;
            LumoraLogger.Log($"StaticOccluders '{slot.SlotName.Value}': {_entries.Count} occluders of {triangles} triangles, {waiting} surfaces still arriving");
        }
        if (waiting > 0 && ++_rounds < PendingRounds)
            Later();
    }

    private Verdict Consider(MeshRenderer renderer, float minimum, int maxTriangles)
    {
        var slot = renderer.Slot;
        if (slot == null || slot.ActiveUserRoot != null || renderer.Mesh.Target == null)
            return Verdict.No;
        if (renderer.Hook is not IDrawnInstance drawn)
            return Verdict.Waiting;
        var instance = drawn.DrawnInstance;
        if (instance == null || !GodotObject.IsInstanceValid(instance))
            return renderer.PerSurfaceOrdering ? Verdict.No : Verdict.Waiting;
        // Drawn for every camera, and by nothing that moves it about on its own.
        if (instance.Layers != (uint)Helpers.RenderHelper.PUBLIC_LAYER || renderer.HasOverlayOrder || renderer.ViewLayers != 0
            || slot.GetComponentInParents<RigidBody>() != null)
            return Verdict.No;
        var mesh = instance.Mesh;
        if (mesh == null)
            return Verdict.Waiting;
        var bounds = mesh.GetAabb();
        var basis = instance.Transform.Basis;
        float scale = Mathf.Max(basis.X.Length(), Mathf.Max(basis.Y.Length(), basis.Z.Length()));
        if (Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z)) * scale < minimum)
            return Verdict.No;

        if (_entries.TryGetValue(renderer, out var entry))
        {
            if (ReferenceEquals(entry.Instance, instance) && ReferenceEquals(entry.Mesh, mesh) && entry.Bounds == bounds
                && GodotObject.IsInstanceValid(entry.Node))
            {
                entry.Pass = _pass;
                return Verdict.Yes;
            }
            Free(entry);
            _entries.Remove(renderer);
        }

        var phos = PhosOf(renderer.Mesh.Target);
        if (phos == null || phos.VertexCount == 0)
            return Verdict.Waiting;

        // Only what solid materials draw: a window or a cut-out leaf hides nothing behind it.
        _indices.Clear();
        for (int i = 0; i < phos.Submeshes.Count; i++)
        {
            if (phos.Submeshes[i] is not PhosTriangleSubmesh triangles || triangles.IndexCount == 0)
                continue;
            var solid = SubmeshSolid(renderer, i);
            if (solid == null)
                return Verdict.Waiting;
            if (!solid.Value)
                continue;
            var raw = triangles.RawIndices;
            for (int k = 0; k < triangles.IndexCount; k++)
                _indices.Add(raw[k]);
        }
        if (_indices.Count == 0 || _indices.Count / 3 > maxTriangles)
        {
            _indices.Clear();
            return Verdict.No;
        }

        var positions = phos.RawPositions;
        var vertices = new Vector3[phos.VertexCount];
        for (int i = 0; i < vertices.Length; i++)
        {
            var p = positions[i];
            vertices[i] = new Vector3(p.x, p.y, p.z);
        }
        var indices = _indices.ToArray();
        _indices.Clear();
        var occluder = new ArrayOccluder3D();
        occluder.SetArrays(vertices, indices);
        System.GC.KeepAlive(vertices);
        System.GC.KeepAlive(indices);
        var node = new OccluderInstance3D { Name = "Occluder", Occluder = occluder };
        instance.AddChild(node);
        _entries[renderer] = new Entry { Instance = instance, Mesh = mesh, Bounds = bounds, Node = node, Triangles = indices.Length / 3, Pass = _pass };
        return Verdict.Yes;
    }

    private static PhosMesh? PhosOf(Component mesh) => mesh switch
    {
        MeshProvider provider => provider.Asset?.MeshData,
        LumoraMeshes.ProceduralMesh procedural => procedural.PhosMesh,
        _ => null,
    };

    // Materials are numbered by submesh, the last one standing in for any past the end. Null while still arriving.
    private static bool? SubmeshSolid(MeshRenderer renderer, int submesh)
    {
        int count = renderer.Materials.Count;
        if (count == 0)
            return false;
        var reference = renderer.Materials.GetElement(System.Math.Min(submesh, count - 1));
        if (reference == null || (reference.Target == null && reference.Value.IsNull))
            return false;
        if (reference.Asset?.Hook is not MaterialAssetHook material)
            return null;
        if (material.GodotMaterial == null)
            return null;
        return material.DrawsSolid;
    }

    private static void Free(Entry entry)
    {
        if (GodotObject.IsInstanceValid(entry.Node))
            entry.Node.QueueFree();
    }

    private void Clear()
    {
        foreach (var entry in _entries.Values)
            Free(entry);
        _entries.Clear();
        _reported = false;
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_subscribed && Owner != null)
            Owner.Invalidated -= OnInvalidated;
        _subscribed = false;
        if (!destroyingWorld)
            Clear();
        else
            _entries.Clear();
        base.Destroy(destroyingWorld);
    }
}
