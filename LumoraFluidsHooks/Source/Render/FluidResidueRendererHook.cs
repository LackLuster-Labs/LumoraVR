// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Lumora.Core;
using Lumora.Godot.Hooks;

namespace Lumora.Fluids.Hooks;

// Draws the spots on coated people for a world: on a simulating machine through the GPU residue pass (spots times
// residue bones into a MultiMesh the kernel writes), on a view-only machine on the CPU every frame. Either way the
// wearer's own head spots sit on the local head band, so a mirror shows them and the wearer's eyes do not. The
// people pass packs the spots and bones in this record's format, and the hook pulls them itself, so nobody calls
// in: on a simulating machine RecordWorld(world), which the device calls every drawn frame (asleep ones too),
// takes them from the world's frame block and records; on a view-only machine, where no device runs, the hook
// takes FluidPeopleLink's ResidueGathered from the update. SetSpots stays open for a caller of its own. Spots are skipped for people drawn Hidden or as a stand-in before they get
// here. The MultiMeshes are raw instances in the shared scenario, so they are hidden while the world is not the
// one on screen. -xlinka
[ImplementableHook(typeof(FluidResidueRenderer))]
public sealed class FluidResidueRendererHook : ComponentHook<FluidResidueRenderer>
{
    private static readonly Dictionary<World, FluidResidueRendererHook> _byWorld = new();

    private FluidResiduePass? _gpu;
    private FluidViewOnlyBlobs? _cpu;
    private ShaderMaterial? _material;
    private Transform3D[] _boneTransforms = Array.Empty<Transform3D>();
    private World? _world;
    private Action<World, FluidFrameBlock>? _onGathered;
    private int _lastGathered;

    public FluidResiduePass? Gpu => _gpu;

    // Spot sets taken from the people pass, for tests and the inspector.
    public int Gathered { get; private set; }

    public FluidViewOnlyBlobs? Cpu => _cpu;

    public override void Initialize()
    {
        base.Initialize();
        FluidGodotStart.EnsureStarted();
        _world = Owner.World;
        if (_world != null)
            _byWorld[_world] = this;
        _onGathered = OnGathered;
        FluidPeopleLink.ResidueGathered += _onGathered;
    }

    // The view-only path's spots; a simulating machine pulls them in RecordWorld instead.
    private void OnGathered(World world, FluidFrameBlock block)
    {
        if (!ReferenceEquals(world, _world))
            return;
        Ensure();
        if (_gpu != null)
            return;
        Take(world, block);
    }

    private void Take(World world, FluidFrameBlock block)
    {
        // Nobody coated, as last time: nothing to write. Most frames of most worlds are this.
        if (block.Spots.Count == 0 && _lastGathered == 0)
            return;
        _lastGathered = block.Spots.Count;
        var people = FluidPeopleLink.Find(world)?.People;
        var bounds = new Aabb(Vector3.Zero, Vector3.Zero);
        if (people != null)
        {
            var lo = people.ResidueMin;
            var hi = people.ResidueMax;
            if (hi.x >= lo.x && hi.y >= lo.y && hi.z >= lo.z)
                bounds = new Aabb(new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z));
        }
        SetSpots(CollectionsMarshal.AsSpan(block.Spots), CollectionsMarshal.AsSpan(block.ResidueBones), bounds);
        Gathered++;
    }

    public static FluidResidueRendererHook? For(World world) => _byWorld.TryGetValue(world, out var hook) ? hook : null;

    // The device's call, every drawn frame of a simulating world: this frame's spots from the block the people pass
    // packed, then the record. This one-argument form is final.
    public static void RecordWorld(World world)
    {
        if (!_byWorld.TryGetValue(world, out var hook))
            return;
        hook.Ensure();
        if (hook._gpu != null && FluidSpace.Find(world)?.Gpu?.Block is { } block)
            hook.Take(world, block);
        hook.Record();
    }

    private void Record()
    {
        bool shown = Owner.Enabled && attachedNode != null && GodotObject.IsInstanceValid(attachedNode) && attachedNode.IsVisibleInTree();
        _gpu?.SetVisible(shown);
        _cpu?.SetVisible(shown);
        if (shown)
            _gpu?.Record();
    }

    // The quality pick can turn view only on or off mid-session: the spots then move to the other path.
    private void Ensure()
    {
        var path = FluidGpu.DrawPath;
        if (path == FluidGpuPath.ViewOnly && _gpu != null || path == FluidGpuPath.Gpu && _cpu != null)
            DropDrawing();
        if (_gpu != null || _cpu != null)
            return;
        if (path == FluidGpuPath.Pending)
            return;
        var scenario = attachedNode.GetWorld3D()?.Scenario ?? default;
        if (!scenario.IsValid)
            return;
        var tier = FluidGpu.Current?.Tier ?? FluidGpuTier.None;
        int capacity = FluidResidueRenderer.SpotsPerAvatar(tier) * FluidResidueRenderer.MaxPeople;
        uint layers = FluidRenderLayers.Public(Owner.World);
        uint head = FluidRenderLayers.WearerHead(Owner.World);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Modules/Fluids/Shaders/FluidResidue.gdshader") };
        if (path == FluidGpuPath.Gpu)
        {
            var rd = RenderingServer.GetRenderingDevice();
            var kernels = rd != null ? FluidSurfaceKernels.For(rd, out _) : null;
            if (rd == null || kernels == null)
                return;
            // about 30 bones carry spots on a person
            _gpu = new FluidResiduePass(rd, kernels, capacity, 32 * FluidResidueRenderer.MaxPeople, FluidResidueRenderer.FineSpots(tier), scenario, layers, head);
            _gpu.SetMaterial(_material.GetRid());
        }
        else
        {
            _cpu = new FluidViewOnlyBlobs(0, capacity, scenario, layers, head);
            _cpu.SetMaterials(default, _material.GetRid());
        }
    }

    public override void ApplyChanges()
    {
        if (attachedNode == null)
            return;
        Ensure();
        _gpu?.SetVisible(Owner.Enabled);
        _cpu?.SetVisible(Owner.Enabled);
    }

    // This frame's spots (wearer order, largest first per avatar) and the residue bones they ride, as 3x4 rows.
    // bounds covers every coated avatar in view.
    public void SetSpots(ReadOnlySpan<FluidSpotRecord> spots, ReadOnlySpan<FluidResidueBone> bones, Aabb bounds)
    {
        Ensure();
        if (_gpu != null)
        {
            _gpu.SetBounds(bounds);
            _gpu.Upload(spots, bones);
            return;
        }
        if (_cpu == null)
            return;
        if (_boneTransforms.Length < bones.Length)
            _boneTransforms = new Transform3D[Math.Max(bones.Length, _boneTransforms.Length * 2)];
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            _boneTransforms[i] = new Transform3D(
                new Vector3(b.Row0.x, b.Row1.x, b.Row2.x),
                new Vector3(b.Row0.y, b.Row1.y, b.Row2.y),
                new Vector3(b.Row0.z, b.Row1.z, b.Row2.z),
                new Vector3(b.Row0.w, b.Row1.w, b.Row2.w));
        }
        _cpu.SetBounds(bounds);
        _cpu.SetSpots(spots, _boneTransforms.AsSpan(0, bones.Length));
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_onGathered != null)
            FluidPeopleLink.ResidueGathered -= _onGathered;
        _onGathered = null;
        if (_world != null && _byWorld.TryGetValue(_world, out var hook) && ReferenceEquals(hook, this))
            _byWorld.Remove(_world);
        DropDrawing();
        base.Destroy(destroyingWorld);
    }

    private void DropDrawing()
    {
        _gpu?.Dispose();
        _gpu = null;
        _cpu?.Dispose();
        _cpu = null;
        _material?.Dispose();
        _material = null;
        _lastGathered = 0;
    }
}
