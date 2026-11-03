// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// One texel as it is stored, at a pixel counted from the top left of mip level mip: 0 to 1 a channel for
// an ordinary picture, and past 1 where a half-float one holds more. ok is false, and the colour clear
// black, for a pixel outside the picture, a level it does not keep here, a texture with no pixels to
// read, or while a loaded picture's pixels come back from its file: the first read starts that and they
// are there a moment later. The pixels are this machine's copy, whose size can differ from another
// machine's.
[Node("Assets/Textures/Get Texture Pixel", Capability.WorldRead, Description = "Reads a texture pixel at the given coordinates and mip level. Reports whether the pixel data is available.",
    Keywords = "read pixel, texel, pixel color, image pixel")]
public sealed class GetTexturePixel : ReadNode
{
    [return: Port("color")]
    public static color Run(ScriptContext ctx, IAssetProvider<TextureAsset>? texture, int2 pixel, out bool ok, int mip = 0)
    {
        ok = ScriptAssets.TryLevel(ScriptAssets.Texture(ctx, texture), mip, out var level)
            && pixel.x >= 0 && pixel.y >= 0 && pixel.x < level.Width && pixel.y < level.Height;
        return ok ? level.Texel(pixel.x, pixel.y) : default;
    }
}
