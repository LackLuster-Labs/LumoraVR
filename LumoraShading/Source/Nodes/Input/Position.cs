// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Where the vertex or pixel is. Object space per pixel is the vertex's own position carried across, as
// it was before any vertex offset.
[Node("Input/Position", Description = "Where the vertex or pixel is.")]
public sealed class Position : ShaderNode
{
    [return: Port("position")]
    public static float3 Run([Setting] ShaderSpace space = ShaderSpace.World) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        var space = context.Option<ShaderSpace>("space");
        if (context.Stage == ShaderStage.Vertex)
        {
            context.Output("position", space switch
            {
                ShaderSpace.Object => "VERTEX",
                ShaderSpace.View => "(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz",
                _ => "(MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz",
            });
            return;
        }
        context.Output("position", space switch
        {
            ShaderSpace.Object => context.Varying("object_position", ShaderType.Float3, "VERTEX"),
            ShaderSpace.View => "VERTEX",
            _ => "(INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz",
        });
    }
}
