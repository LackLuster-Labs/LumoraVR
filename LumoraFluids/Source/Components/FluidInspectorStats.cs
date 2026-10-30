// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components;

namespace Lumora.Fluids;

// Inspector-local readings refresh while the panel is open; GPU measurements never become world state. -xlinka
[ComponentCategory("Hidden")]
public sealed class FluidInspectorStats : Component
{
    public readonly SyncRef<FluidSystem> System = new();
    public readonly SyncRef<FluidRenderer> Renderer = new();
    public readonly SyncRef<Text> Memory = new();
    public readonly SyncRef<Text> Capacity = new();
    public readonly SyncRef<Text> Work = new();
    public readonly SyncRef<Text> Air = new();
    public readonly SyncRef<Text> Status = new();
    private float _elapsed;

    public static void ForSystem(UIBuilder ui, FluidSystem system)
    {
        var reader = ui.Root.AttachComponent<FluidInspectorStats>();
        reader.System.Target = system;
        reader.Memory.Target = InspectorStats.AddRow(ui, "Simulation VRAM", "");
        reader.Capacity.Target = InspectorStats.AddRow(ui, "Particle pool", "");
        reader.Work.Target = InspectorStats.AddRow(ui, "GPU time (world)", "");
        reader.Status.Target = InspectorStats.AddRow(ui, "Quality / status", "");
        reader.Refresh();
    }

    public static void ForRenderer(UIBuilder ui, FluidRenderer renderer)
    {
        var reader = ui.Root.AttachComponent<FluidInspectorStats>();
        reader.Renderer.Target = renderer;
        reader.Memory.Target = InspectorStats.AddRow(ui, "Surface VRAM", "");
        reader.Capacity.Target = InspectorStats.AddRow(ui, "Triangle capacity", "");
        reader.Work.Target = InspectorStats.AddRow(ui, "Surface work", "");
        reader.Air.Target = InspectorStats.AddRow(ui, "Air bubbles", "");
        reader.Status.Target = InspectorStats.AddRow(ui, "Status", "");
        reader.Refresh();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if ((_elapsed += delta) < 0.25f)
            return;
        _elapsed = 0;
        Refresh();
    }

    private void Refresh()
    {
        if (Renderer.Target is { IsDestroyed: false } renderer)
        {
            Write(Memory, $"{renderer.AllocatedBytes / 1048576.0:0.00} MiB buffers and mesh");
            Write(Capacity, $"{renderer.AllocatedTriangles:N0}");
            Write(Work, $"{renderer.SurfaceBricks:N0} bricks per update");
            Write(Air, renderer.AirStatus.Length > 0 ? renderer.AirStatus
                : $"{renderer.AirBubbleCount:N0} active, {renderer.AirAllocatedBytes / 1048576.0:0.00} MiB");
            Write(Status, renderer.ResourceStatus.Length > 0 ? renderer.ResourceStatus
                : renderer.AllocatedBytes > 0 ? "Ready" : "Not allocated here");
        }
        else if (System.Target is { IsDestroyed: false } system)
        {
            var space = FluidSpace.Find(system.World);
            var gpu = space?.Gpu;
            Write(Memory, $"{(gpu?.SimulationBytes ?? 0) / 1048576.0:0.00} MiB buffers shared by this world's fluids");
            Write(Capacity, $"{gpu?.ParticleCapacity ?? 0:N0} allocated; all may wake");
            Write(Work, gpu != null && double.IsFinite(gpu.LastGpuMs)
                ? $"{gpu.LastGpuMs:0.00} ms, {gpu.LastDispatches} dispatches" : "No reading");
            Write(Status, gpu?.ResourceStatus is { Length: > 0 } error ? error : space?.Budget.Describe() ?? "Not started");
        }
    }

    private static void Write(SyncRef<Text> reference, string value)
    {
        if (reference.Target is { IsDestroyed: false } text && text.Content.Value != value)
            text.Content.Value = value;
    }
}
