// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

// Plays an animated picture (a FrameSheet: an imported GIF or animated WebP) on a material by driving its UV scale
// and offset onto one frame at a time, the way UVAtlasAnimator plays a hand-made sheet.
//
// The grid and the frame times come from the texture each machine loaded, never from synced fields: a machine with
// a lower size cap holds the same frames smaller, so only the picture itself knows its layout. The time is the
// session clock, so every peer shows the same frame at the same moment and a late joiner lands mid-loop where
// everyone else is; World.Time starts at zero on each machine and could not give that. The drives are local, each
// peer working its own out, so nothing goes over the network while it plays. -xlinka
[ComponentCategory("Utility/Materials")]
[DefaultUpdateOrder(-100)]
public class TextureFramePlayer : Component
{
    public readonly AssetRef<TextureAsset> Texture;

    // 1 plays at the file's own speed, 2 twice as fast; 0 holds the first frame.
    public readonly Sync<float> Speed;

    public readonly FieldDrive<float2> Scale;

    public readonly FieldDrive<float2> Offset;

    private TextureAsset? _shownOn;
    private int _shownFrame = -1;
    private int _shownWidth;
    private int _shownFrames;

    public TextureFramePlayer()
    {
        Texture = new AssetRef<TextureAsset>(this);
        Speed = new Sync<float>(this, 1f);
        Scale = new FieldDrive<float2>(this) { LocalValueOnly = true };
        Offset = new FieldDrive<float2>(this) { LocalValueOnly = true };
    }

    // The frame this machine shows now.
    public int CurrentFrame
    {
        get
        {
            var asset = Texture.Asset;
            float speed = Speed.Value;
            if (asset == null || asset.AnimationFrames <= 1 || speed == 0f || !float.IsFinite(speed))
                return 0;
            return asset.AnimationFrameAt(UtilityClock.Seconds(World) * speed);
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var asset = Texture.Asset;
        int frame = CurrentFrame;
        int frames = asset?.AnimationFrames ?? 1;
        int width = asset?.Width ?? 0;
        if (ReferenceEquals(asset, _shownOn) && frame == _shownFrame && frames == _shownFrames && width == _shownWidth)
            return;
        _shownOn = asset;
        _shownFrame = frame;
        _shownFrames = frames;
        _shownWidth = width;
        Apply(asset, frame);
    }

    private void Apply(TextureAsset? asset, int frame)
    {
        if (asset == null || asset.AnimationFrames <= 1 || asset.Width <= 0 || asset.Height <= 0)
        {
            Scale.SetValue(float2.One);
            Offset.SetValue(float2.Zero);
            return;
        }

        // Half a texel in from every edge of the cell, so filtering never reaches into the frame beside it.
        var cell = asset.AnimationFrameRect(frame);
        float insetU = 0.5f / asset.Width;
        float insetV = 0.5f / asset.Height;
        Scale.SetValue(new float2(cell.width - 2f * insetU, cell.height - 2f * insetV));
        Offset.SetValue(new float2(cell.x + insetU, cell.y + insetV));
    }
}
