// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// What the material shows. Base color, metallic and roughness are always written; the rest only when
// something is wired or typed in, so an unused input costs nothing. Normal takes a tangent space normal
// (Normal Map, Bump). Alpha only shows on a Cutout or see-through surface; typing or wiring an alpha clip
// clips on any surface. Vertex offset moves each vertex in the object's own space. -xlinka
[Node("Output/Material Output", Description = "Connects the shader's colour, surface properties and other outputs to the material.")]
public sealed class MaterialOutput : ShaderNode
{
    public override bool IsOutput => true;

    public static void Run([GlslDefault("vec4(0.8, 0.8, 0.8, 1.0)")] color baseColor, float alpha = 1f, float metallic = 0f,
        float roughness = 0.5f, colorHDR emission = default, float3 normal = default, float occlusion = 1f, float alphaClip = 0.5f,
        [VertexStage] float3 vertexOffset = default, [Setting] ShaderSurface surface = ShaderSurface.Opaque,
        [Setting] ShaderLighting lighting = ShaderLighting.Lit, [Setting] ShaderCull cull = ShaderCull.Back) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        if (context.Stage == ShaderStage.Vertex)
        {
            if (Used(context, "vertexOffset"))
                context.Line($"VERTEX += {context.Input("vertexOffset").Code};");
            return;
        }

        var surface = context.Option<ShaderSurface>("surface");
        context.RenderMode(context.Option<ShaderCull>("cull") switch
        {
            ShaderCull.Front => "cull_front",
            ShaderCull.Both => "cull_disabled",
            _ => "cull_back",
        });
        if (context.Option<ShaderLighting>("lighting") == ShaderLighting.Unlit)
            context.RenderMode("unshaded");
        switch (surface)
        {
            case ShaderSurface.Transparent:
                context.RenderMode("blend_mix");
                break;
            case ShaderSurface.Additive:
                context.RenderMode("blend_add");
                break;
            case ShaderSurface.Multiply:
                context.RenderMode("blend_mul");
                break;
        }

        context.Line($"ALBEDO = {context.Input("baseColor").Code}.rgb;");
        context.Line($"METALLIC = {context.Input("metallic").Code};");
        context.Line($"ROUGHNESS = {context.Input("roughness").Code};");
        if (Used(context, "emission"))
            context.Line($"EMISSION = {context.Input("emission").Code}.rgb;");
        if (Used(context, "normal"))
            context.Line($"NORMAL_MAP = normalize({context.Input("normal").Code}) * 0.5 + 0.5;");
        if (Used(context, "occlusion"))
            context.Line($"AO = {context.Input("occlusion").Code};");

        bool clip = surface == ShaderSurface.Cutout || Used(context, "alphaClip");
        if (surface != ShaderSurface.Opaque || clip)
            context.Line($"ALPHA = {context.Input("alpha").Code};");
        else if (context.IsWired("alpha"))
            context.Warning("alpha does nothing on an Opaque surface; pick Cutout or a see-through surface, or type an alpha clip");
        if (clip)
            context.Line($"ALPHA_SCISSOR_THRESHOLD = {context.Input("alphaClip").Code};");
    }

    private static bool Used(ShaderNodeContext context, string port) => context.IsWired(port) || context.IsSet(port);
}
