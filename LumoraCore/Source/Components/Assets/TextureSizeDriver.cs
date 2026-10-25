// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Assets;

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
        MaxSide.Value       = 1f;
        ColliderDepth.Value = 0.02f;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var provider = Source.Target;
        var quad = Target.Target;
        if (provider == null || quad == null)
            return;

        // One frame's shape for an animated picture, and the file's own size for it, so a peer whose cap made the
        // sheet smaller works out the same quad.
        var asset = provider.Asset;
        if (asset == null || asset.FrameWidth <= 0 || asset.FrameHeight <= 0)
            return;
        int width = asset.FrameWidth;
        int height = asset.FrameHeight;

        float maxSide = MaxSide.Value;
        if (_hasApplied &&
            width == _lastWidth &&
            height == _lastHeight &&
            System.Math.Abs(maxSide - _lastMaxSide) < 0.0001f)
        {
            return;
        }

        float aspect = (float)width / height;
        float2 size = aspect >= 1f
            ? new float2(maxSide * aspect, maxSide)
            : new float2(maxSide, maxSide / aspect);

        quad.Size.Value = size;

        var collider = ColliderTarget.Target;
        if (collider != null)
        {
            collider.Size.Value = new float3(size.x, size.y, System.Math.Max(0.001f, ColliderDepth.Value));
        }

        _lastWidth = width;
        _lastHeight = height;
        _lastMaxSide = maxSide;
        _hasApplied = true;
    }
}
