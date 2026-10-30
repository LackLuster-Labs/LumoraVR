// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;

namespace Lumora.Fluids;

// Draws a system as a mesh the GPU builds every frame: the bulk, the strand tubes and the spray. Any material goes
// on the bulk; strands keep the edge material built from the system's FluidMaterial.
[ComponentCategory("Physics/Fluids")]
public sealed class FluidRenderer : ImplementableComponent, ICustomInspectorUI
{
    public readonly SyncRef<FluidSystem> System = new();
    // null = the system's FluidMaterial
    public readonly AssetRef<MaterialAsset> Material = new();
    // x the spacing; 0 = the tier's
    public readonly Sync<float> CellScale = new(0f);
    public readonly Sync<float> Smoothing = new(0.5f);
    public readonly Sync<bool> Strands = new(true);
    public readonly Sync<bool> Spray = new(true);
    public readonly Sync<bool> AirBubbles = new(true);
    // Entrained air volume per dry-to-wet contact patch; zero disables it. -xlinka
    public readonly Sync<float> AirAmount = new(1f);
    // 0 = the tier's
    public readonly Sync<int> StrandSides = new(0);
    // 0 = grow when the surface needs more room; positive values are an explicit capacity. -xlinka
    public readonly Sync<int> TriangleBudget = new(0);
    // 0 = grow to the number of dirty surface bricks; positive values are an explicit work limit. -xlinka
    public readonly Sync<int> SurfaceWorkLimit = new(0);

    // Local measurements from the render hook; never synchronized or saved. -xlinka
    public long AllocatedBytes { get; set; }
    public int AllocatedTriangles { get; set; }
    public int SurfaceBricks { get; set; }
    public int AirBubbleCount { get; set; }
    public long AirAllocatedBytes { get; set; }
    public string AirStatus { get; set; } = string.Empty;
    public string ResourceStatus { get; set; } = string.Empty;

    public void BuildInspectorBody(UIBuilder ui) => FluidInspectorStats.ForRenderer(ui, this);
}
