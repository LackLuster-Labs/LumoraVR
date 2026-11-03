// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Puts a texture in a shader material's texture property, or empties it with no texture, judged like the
// author setting it on the material panel. Fails when the material has no texture property of that name: an
// Image Texture node's property is there once the graph it is in has been made into the material's shader.
[Node("Assets/Materials/Set Shader Texture", Capability.WorldWrite, Description = "Puts a texture in a shader material's texture property, or empties it with no texture, judged like the author setting it on the material panel.",
    Keywords = "material texture, texture slot")]
public sealed class SetShaderTexture : ActionNode
{
    public static void Run(ScriptContext ctx, IAssetProvider<MaterialAsset>? material, string? name, IAssetProvider<TextureAsset>? texture)
    {
        if (!ScriptReads.Reached(ctx, material as Component, "material"))
            return;
        if (ShaderPropertyFields.Texture(material, name) is not { } param)
        {
            ctx.Fail($"the material has no texture called {name}");
            return;
        }
        if (texture != null && !ScriptReads.ReachedElement(ctx, texture, "texture"))
            return;
        ScriptWrites.WriteReference(ctx, param.Texture, texture);
    }
}
