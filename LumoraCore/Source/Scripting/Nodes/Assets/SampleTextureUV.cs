// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The texture's colour at uv, blended from the four nearest texels of mip level mip: 0,0 is the top left
// corner and 1,1 the bottom right, the way meshes lay pictures on. Past the edge the wrap decides:
// repeat, clamp to the edge, mirror, or clear black past it. ok is false, and the colour clear black, for
// a level it does not keep here, a texture with no pixels to read, or while a loaded picture's pixels
// come back from its file: the first read starts that and they are there a moment later.
[Node("Assets/Textures/Sample Texture UV", Capability.WorldRead, Description = "The texture's colour at uv, blended from the four nearest texels of mip level mip: 0,0 is the top left corner and 1,1 the bottom right, the way meshes lay pictures on.",
    Keywords = "sample texture, color at uv, texture lookup, image color")]
public sealed class SampleTextureUV : ReadNode
{
    [return: Port("color")]
    public static color Run(ScriptContext ctx, IAssetProvider<TextureAsset>? texture, float2 uv, out bool ok,
        TextureWrapMode wrap = TextureWrapMode.Repeat, int mip = 0)
    {
        ok = false;
        if (!float.IsFinite(uv.x) || !float.IsFinite(uv.y) || !ScriptAssets.TryLevel(ScriptAssets.Texture(ctx, texture), mip, out var level))
            return default;
        ok = true;
        return level.Sample(uv, wrap);
    }
}
