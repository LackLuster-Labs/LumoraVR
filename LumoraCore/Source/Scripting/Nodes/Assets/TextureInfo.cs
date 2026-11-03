// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What a texture is on this machine: its size in pixels, how many mip levels a pixel read gives here,
// whether it holds values past 1 (a half-float picture), and how many frames a strip stacks down it.
// Readable says whether Get Texture Pixel and Sample Texture UV can read it: a camera's or a video's
// picture lives on the graphics card alone. A loaded picture's pixels come back from its file on the
// first read, so those nodes can answer not ok for a moment after readable says true. Nothing while it
// loads. The size can differ between
// machines, each loading the resolution its settings allow. format is how the picture is stored: as the
// graphics card holds it once the renderer has said, else as it was decoded, and unknown for a camera's
// or a video's picture.
[Node("Assets/Textures/Texture Info", Capability.WorldRead, Description = "What a texture is on this machine: its size in pixels, how many mip levels a pixel read gives here, whether it holds values past 1 (a half-float picture), and how many frames a strip stacks down it.",
    Keywords = "texture size, resolution, width, height, dimensions")]
public sealed class TextureInfo : ReadNode
{
    [return: Port("width")]
    public static int Run(ScriptContext ctx, IAssetProvider<TextureAsset>? texture, out int height, out int mips, out bool hdr,
        out int frames, out bool readable, out TextureFormatKind format)
    {
        height = 0;
        mips = 0;
        hdr = false;
        frames = 0;
        readable = false;
        format = TextureFormatKind.Unknown;
        switch (ScriptAssets.Texture(ctx, texture))
        {
            case RenderTexture render:
                height = render.RenderHeight;
                frames = 1;
                return render.RenderWidth;
            case VideoTexture video:
                height = video.VideoHeight;
                frames = video.VideoWidth > 0 ? 1 : 0;
                return video.VideoWidth;
            case { } picture when picture.CanReadPixels && picture.Width > 0:
                height = picture.Height;
                mips = picture.ReadableLevels;
                hdr = picture.PixelFormat == TextureFormatKind.RGBA16F;
                frames = System.Math.Max(1, picture.AnimationFrames);
                readable = true;
                format = picture.Metadata?.GpuFormat is { } stored and not TextureFormatKind.Unknown ? stored
                    : picture.Metadata?.ContentFormat is { } content and not TextureFormatKind.Unknown ? content
                    : picture.PixelFormat;
                return picture.Width;
            default:
                return 0;
        }
    }
}
