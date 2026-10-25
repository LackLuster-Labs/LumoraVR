// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// PBS projected on three planes from the world position (or the mesh's own with ObjectSpace) instead of read
// off UVs, in either workflow, with emission, occlusion, alpha handling and culling. TextureScale tiles every
// plane alike. Its maps pack metallic in red and smoothness in alpha, like the rest of the generated PBS set.
//
// Not PBS_Triplanar, which reads the glTF packing (roughness green, metallic blue) as multipliers and carries
// no emission map or occlusion. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_TriplanarSurface : PBS_SurfaceBase
{
    [Group("Projection")]
    [Range(0f, 16f, "0.00")]
    public readonly Sync<float> TriplanarBlendPower;

    [Group("Projection")]
    public readonly Sync<bool> ObjectSpace;

    protected override PBSSurfaceFeatures Features => PBSSurfaceFeatures.Triplanar;

    public PBS_TriplanarSurface() : base(Assets.Culling.Back, Assets.AlphaHandling.Opaque)
    {
        TriplanarBlendPower = new Sync<float>(this, 4f);
        ObjectSpace = new Sync<bool>(this, false);
    }

    protected override void UpdateEffect(MaterialAsset asset)
    {
        asset.SetFloat("TriplanarBlendPower", TriplanarBlendPower.Value);
        asset.SetBool("ObjectSpace", ObjectSpace.Value);
    }
}
