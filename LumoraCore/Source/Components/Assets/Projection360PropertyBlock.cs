// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A picture and a perspective frame for one renderer, over a Projection360Material shared with others: a wall of
// screens each showing its own camera through one material. The field of view and angle offset are in degrees
// and act in the Perspective projection, as the material's own do.
//
// The picture shows to both eyes. A right-eye frame the shared material carries is cleared here, or the right
// eye would keep the material's picture while the left showed this one. -xlinka
[ComponentCategory("Assets/Material Property Blocks")]
public class Projection360PropertyBlock : MaterialPropertyBlockProvider
{
    public readonly AssetRef<TextureAsset> Texture;
    public readonly Sync<float2> PerspectiveFieldOfView;
    public readonly Sync<float2> PerspectiveAngleOffset;

    public Projection360PropertyBlock()
    {
        Texture = new AssetRef<TextureAsset>(this);
        PerspectiveFieldOfView = new Sync<float2>(this, new float2(45f, 45f));
        PerspectiveAngleOffset = new Sync<float2>(this, float2.Zero);
    }

    protected override void UpdateBlock(MaterialPropertyBlockAsset asset)
    {
        const float radians = MathF.PI / 180f;
        asset.SetTexture("MainTexture", Texture.Asset);
        asset.SetTexture("MainTextureRight", null!);
        var fov = PerspectiveFieldOfView.Value;
        var angle = PerspectiveAngleOffset.Value;
        asset.SetFloat4("PerspectiveFov", new float4(fov.x, fov.y, angle.x, angle.y) * radians);
    }
}
