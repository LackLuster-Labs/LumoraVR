// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Assets;

/// <summary>
/// Drives a QuadMesh size from an ImageProvider's loaded texture aspect ratio.
/// </summary>
[ComponentCategory("Assets")]
public sealed class TextureSizeDriver : Component
{
    public readonly SyncRef<ImageProvider> Source = new();
    public readonly SyncRef<QuadMesh> Target = new();
    public readonly SyncRef<BoxCollider> ColliderTarget = new();
    public readonly Sync<float> MaxSide = new();
    public readonly Sync<float> ColliderDepth = new();

    private int _lastWidth;
    private int _lastHeight;
    private float _lastMaxSide;
    private bool _hasApplied;

    public override void OnInit()
    {
        base.OnInit();
        MaxSide.Value = 1f;
        ColliderDepth.Value = 0.02f;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var provider = Source.Target;
        var quad = Target.Target;
        if (provider == null || quad == null)
            return;

        var asset = provider.Asset;
        if (asset == null || asset.Width <= 0 || asset.Height <= 0)
            return;

        float maxSide = MaxSide.Value;
        if (_hasApplied &&
            asset.Width == _lastWidth &&
            asset.Height == _lastHeight &&
            System.Math.Abs(maxSide - _lastMaxSide) < 0.0001f)
        {
            return;
        }

        float aspect = (float)asset.Width / asset.Height;
        float2 size = aspect >= 1f
            ? new float2(maxSide * aspect, maxSide)
            : new float2(maxSide, maxSide / aspect);

        quad.Size.Value = size;

        var collider = ColliderTarget.Target;
        if (collider != null)
        {
            collider.Size.Value = new float3(size.x, size.y, System.Math.Max(0.001f, ColliderDepth.Value));
        }

        _lastWidth = asset.Width;
        _lastHeight = asset.Height;
        _lastMaxSide = maxSide;
        _hasApplied = true;
    }
}
