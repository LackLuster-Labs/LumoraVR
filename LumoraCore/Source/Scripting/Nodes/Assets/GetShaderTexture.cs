// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The texture a shader material's texture property holds: an Image Texture node's, by the name the node was given.
// Nothing when it holds none or the material has no texture of that name.
[Node("Assets/Materials/Get Shader Texture", Capability.WorldRead, Description = "The texture a shader material's texture property holds: an Image Texture node's, by the name the node was given.",
    Keywords = "material texture, texture slot")]
public sealed class GetShaderTexture : ReadNode
{
    [return: Port("texture")]
    public static IAssetProvider<TextureAsset>? Run(ScriptContext ctx, IAssetProvider<MaterialAsset>? material, string? name)
    {
        if (!ScriptReads.Visible(ctx, material as Component))
            return null;
        return ShaderPropertyFields.Texture(material, name)?.Texture.Target is { IsDestroyed: false } texture
            && ScriptReads.VisibleElement(ctx, texture) ? texture : null;
    }
}
