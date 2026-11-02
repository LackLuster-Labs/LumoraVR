// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// From the surface toward the eye, normalized.
[Node("Input/View Direction", Description = "From the surface toward the eye, normalized.")]
public sealed class ViewDirection : ShaderNode
{
    [return: Port("direction")]
    public static float3 Run([Setting] ShaderSpace space = ShaderSpace.World) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        var space = context.Option<ShaderSpace>("space");
        const string objectSpace = "normalize((inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz - VERTEX)";
        if (context.Stage == ShaderStage.Vertex)
        {
            context.Output("direction", space switch
            {
                ShaderSpace.Object => objectSpace,
                ShaderSpace.View => "normalize(-(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz)",
                _ => "normalize(CAMERA_POSITION_WORLD - (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz)",
            });
            return;
        }
        context.Output("direction", space switch
        {
            ShaderSpace.Object => $"normalize({context.Varying("object_view", ShaderType.Float3, objectSpace)})",
            ShaderSpace.View => "VIEW",
            _ => "normalize((INV_VIEW_MATRIX * vec4(VIEW, 0.0)).xyz)",
        });
    }
}
