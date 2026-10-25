// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// PBS with a glowing rim: RimColor is added to the emission as (1 - N.V) ^ RimPower, so the edges that turn
// away from the viewer light up and the face looking straight at them stays clean. A higher power pulls the
// rim in to a thinner line. The rim follows the normal map. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_RimLit : PBS_SurfaceBase
{
    [Group("Rim")]
    public readonly Sync<colorHDR> RimColor;

    [Group("Rim")]
    [Range(0f, 10f, "0.00")]
    public readonly Sync<float> RimPower;

    protected override PBSSurfaceFeatures Features => PBSSurfaceFeatures.Rim;

    public PBS_RimLit() : base(Assets.Culling.Back, Assets.AlphaHandling.Opaque)
    {
        RimColor = new Sync<colorHDR>(this, new colorHDR(1f, 0f, 0f, 1f));
        RimPower = new Sync<float>(this, 3f);
    }

    protected override void UpdateEffect(MaterialAsset asset)
    {
        asset.SetColor("RimColor", RimColor.Value);
        asset.SetFloat("RimPower", RimPower.Value);
    }
}
