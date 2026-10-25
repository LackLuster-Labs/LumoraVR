// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// Dual-sided material in the metallic workflow: how metal the surface is and how smooth, for both sides.
[ComponentCategory("Assets/Materials")]
public class PBS_MetallicDualSided : PBS_DualSidedMaterial
{
    [Group("Surface")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic;

    [Group("Surface")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness;

    protected override MaterialType MaterialType => MaterialType.PBS_DualSided;

    public PBS_MetallicDualSided()
    {
        Metallic = new Sync<float>(this, 0f);
        Smoothness = new Sync<float>(this, 0.25f);
    }

    protected override void UpdateWorkflow(MaterialAsset asset)
    {
        asset.SetFloat("Metallic", Metallic.Value);
        asset.SetFloat("Smoothness", Smoothness.Value);
    }
}
