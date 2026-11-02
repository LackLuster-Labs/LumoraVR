// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Which way the surface faces, normalized.
[Node("Input/Normal", Description = "Which way the surface faces, normalized.")]
public sealed class Normal : ShaderNode
{
    [return: Port("normal")]
    public static float3 Run([Setting] ShaderSpace space = ShaderSpace.World) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        var space = context.Option<ShaderSpace>("space");
        if (context.Stage == ShaderStage.Vertex)
        {
            context.Output("normal", space switch
            {
                ShaderSpace.Object => "NORMAL",
                ShaderSpace.View => "normalize(MODELVIEW_NORMAL_MATRIX * NORMAL)",
                _ => "normalize(MODEL_NORMAL_MATRIX * NORMAL)",
            });
            return;
        }
        context.Output("normal", space switch
        {
            ShaderSpace.Object => $"normalize({context.Varying("object_normal", ShaderType.Float3, "NORMAL")})",
            ShaderSpace.View => "NORMAL",
            _ => "normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz)",
        });
    }
}
