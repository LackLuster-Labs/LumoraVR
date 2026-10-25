// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Dual-sided material in the specular workflow, for both sides: SpecularColor's RGB is the reflectance and its
// alpha the smoothness.
[ComponentCategory("Assets/Materials")]
public class PBS_SpecularDualSided : PBS_DualSidedMaterial
{
    [Group("Surface")]
    public readonly Sync<colorHDR> SpecularColor;

    protected override MaterialType MaterialType => MaterialType.PBS_DualSidedSpecular;

    public float Smoothness
    {
        get => SpecularColor.Value.a;
        set
        {
            var c = SpecularColor.Value;
            SpecularColor.Value = new colorHDR(c.r, c.g, c.b, value);
        }
    }

    public PBS_SpecularDualSided()
    {
        SpecularColor = new Sync<colorHDR>(this, new colorHDR(0.2f, 0.2f, 0.2f, 0.25f));
    }

    protected override void UpdateWorkflow(MaterialAsset asset)
    {
        asset.SetColor("SpecularColor", SpecularColor.Value);
    }
}
