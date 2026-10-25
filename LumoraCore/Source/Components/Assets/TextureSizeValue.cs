// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Assets;

// Drives a float2 with a picture's size, for whatever wants its shape: a player's aspect ratio, a frame around a
// photo. A video gives the size of its frames once it knows them. The size is first multiplied by Premultiply,
// then stated as Mode says and multiplied by Ratio, and finally scaled down, keeping its shape, until it fits
// MaxSize. With no picture yet it drives zero. -xlinka
[ComponentCategory("Assets/Textures")]
public sealed class TextureSizeValue : Component
{
    public readonly AssetRef<TextureAsset> Texture;
    public readonly FieldDrive<float2> Target;
    public readonly Sync<TextureSizeMode> Mode;
    public readonly Sync<float2> Premultiply;
    public readonly Sync<float2> Ratio;
    public readonly Sync<float2> MaxSize;

    public TextureSizeValue()
    {
        Texture = new AssetRef<TextureAsset>(this);
        Target = new FieldDrive<float2>(this) { LocalValueOnly = true };
        Mode = new Sync<TextureSizeMode>(this, TextureSizeMode.UnitHeight);
        Premultiply = new Sync<float2>(this, float2.One);
        Ratio = new Sync<float2>(this, float2.One);
        MaxSize = new Sync<float2>(this, new float2(float.MaxValue, float.MaxValue));
    }

    public override void OnUpdate(float delta)
    {
        if (!Target.IsLinkValid)
            return;
        var asset = Texture.Asset;
        float2 size = asset switch
        {
            VideoTexture video => new float2(video.VideoWidth, video.VideoHeight),
            { } texture => new float2(texture.Width, texture.Height),
            _ => float2.Zero,
        };
        size *= Premultiply.Value;
        if (size.x > 0f && size.y > 0f)
        {
            switch (Mode.Value)
            {
                case TextureSizeMode.UnitHeight:
                    size /= size.y;
                    break;
                case TextureSizeMode.UnitWidth:
                    size /= size.x;
                    break;
                case TextureSizeMode.Normalized:
                    size = size.Normalized;
                    break;
            }
            size *= Ratio.Value;
        }
        var max = MaxSize.Value;
        if (size.x > max.x)
            size *= max.x / size.x;
        if (size.y > max.y)
            size *= max.y / size.y;
        Target.SetValue(size);
    }
}
