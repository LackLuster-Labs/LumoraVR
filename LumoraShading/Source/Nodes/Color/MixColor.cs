// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Puts b over a the way mode says, faded in by factor. The result keeps a's alpha.
[Node("Color/Mix Color", Description = "Puts b over a the way mode says, faded in by factor.")]
public sealed class MixColor : ShaderNode
{
    [return: Port("color")]
    public static color Run(float factor = 0.5f, [GlslDefault("vec4(0.5, 0.5, 0.5, 1.0)")] color a = default,
        [GlslDefault("vec4(0.5, 0.5, 0.5, 1.0)")] color b = default, [Setting] MixMode mode = MixMode.Mix) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        var a = context.Input("a").Code;
        var b = context.Input("b").Code;
        var baseColor = $"{a}.rgb";
        var over = $"{b}.rgb";
        string blended;
        switch (context.Option<MixMode>("mode"))
        {
            case MixMode.Darken: blended = $"min({baseColor}, {over})"; break;
            case MixMode.Multiply: blended = $"({baseColor} * {over})"; break;
            case MixMode.Burn: context.Need("lm_blend_burn"); blended = $"lm_blend_burn({baseColor}, {over})"; break;
            case MixMode.Lighten: blended = $"max({baseColor}, {over})"; break;
            case MixMode.Screen: blended = $"(1.0 - (1.0 - {baseColor}) * (1.0 - {over}))"; break;
            case MixMode.Dodge: context.Need("lm_blend_dodge"); blended = $"lm_blend_dodge({baseColor}, {over})"; break;
            case MixMode.Add: blended = $"({baseColor} + {over})"; break;
            case MixMode.Overlay: context.Need("lm_blend_overlay"); blended = $"lm_blend_overlay({baseColor}, {over})"; break;
            case MixMode.SoftLight: context.Need("lm_blend_soft_light"); blended = $"lm_blend_soft_light({baseColor}, {over})"; break;
            case MixMode.LinearLight: blended = $"({baseColor} + 2.0 * {over} - 1.0)"; break;
            case MixMode.Difference: blended = $"abs({baseColor} - {over})"; break;
            case MixMode.Exclusion: blended = $"({baseColor} + {over} - 2.0 * {baseColor} * {over})"; break;
            case MixMode.Subtract: blended = $"({baseColor} - {over})"; break;
            case MixMode.Divide: context.Need("lm_blend_divide"); blended = $"lm_blend_divide({baseColor}, {over})"; break;
            default: blended = over; break;
        }
        context.Output("color", $"vec4(mix({baseColor}, {blended}, clamp({context.Input("factor").Code}, 0.0, 1.0)), {a}.a)");
    }
}
