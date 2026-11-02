// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Linq;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// A texture slot and a read of it. The texture the slot holds becomes a property of the material, named
// by name or else after this node, so it can be swapped on the material without touching the graph.
// Color space says what the texture holds: a picture, plain numbers, or a normal map. Per vertex it reads
// the full size image, since a vertex has no pixel size to pick a smaller one by. -xlinka
[Node("Texture/Image Texture", Description = "Samples a named material texture at the given UV coordinates.")]
public sealed class ImageTexture : ShaderNode
{
    [return: Port("color")]
    public static color Run(ShaderTexture texture, [GlslDefault("UV")] float2 uv, out float alpha, [Setting] string name = "",
        [Setting] TextureColorSpace colorSpace = TextureColorSpace.Color, [Setting] TextureFilter filter = TextureFilter.Linear,
        [Setting] TextureWrap wrap = TextureWrap.Repeat) => throw GpuOnly;

    // The material property a node's texture slot is: the name typed on it, made usable, or else one made
    // from the node's id. The editor asks this too, to find the property a card's slot shows.
    public static string UniformName(string nodeId, string? name)
    {
        var own = "tex_" + new string((nodeId ?? string.Empty).ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(24).ToArray());
        return GlslNames.FromText(name, own);
    }

    public override void Emit(ShaderNodeContext context)
    {
        string sampler;
        if (context.IsWired("texture") && context.Input("texture").Code is { Length: > 0 } wired)
        {
            sampler = wired;
        }
        else
        {
            var colorSpace = context.Option<TextureColorSpace>("colorSpace");
            var hints = colorSpace switch
            {
                TextureColorSpace.Normal => "hint_normal",
                TextureColorSpace.Data => "hint_default_white",
                _ => "source_color, hint_default_white",
            };
            hints += context.Option<TextureFilter>("filter") == TextureFilter.Nearest ? ", filter_nearest_mipmap" : ", filter_linear_mipmap";
            hints += context.Option<TextureWrap>("wrap") == TextureWrap.Clamp ? ", repeat_disable" : ", repeat_enable";
            sampler = context.Uniform(UniformName(context.NodeId, context.Text("name")), ShaderType.Texture, hints,
                binding: context.Binding("texture") ?? -1);
        }

        var uv = context.Input("uv").Code;
        context.Output("color", context.Stage == ShaderStage.Vertex ? $"textureLod({sampler}, {uv}, 0.0)" : $"texture({sampler}, {uv})");
        context.Output("alpha", $"{context.OutputName("color")}.a");
    }
}
