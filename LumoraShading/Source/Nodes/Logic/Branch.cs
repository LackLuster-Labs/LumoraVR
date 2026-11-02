// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// ifTrue where condition holds, ifFalse where it does not.
[Node("Logic/Branch", Description = "IfTrue where condition holds, ifFalse where it does not.")]
public sealed class Branch : ShaderNode
{
    [Glsl("({condition} ? {ifTrue} : {ifFalse})")]
    public static float Run(bool condition, float ifTrue, float ifFalse) => throw GpuOnly;

    [Glsl("({condition} ? {ifTrue} : {ifFalse})")]
    public static float2 Run(bool condition, float2 ifTrue, float2 ifFalse) => throw GpuOnly;

    [Glsl("({condition} ? {ifTrue} : {ifFalse})")]
    public static float3 Run(bool condition, float3 ifTrue, float3 ifFalse) => throw GpuOnly;

    [Glsl("({condition} ? {ifTrue} : {ifFalse})")]
    public static float4 Run(bool condition, float4 ifTrue, float4 ifFalse) => throw GpuOnly;

    [Glsl("({condition} ? {ifTrue} : {ifFalse})")]
    public static color Run(bool condition, color ifTrue, color ifFalse) => throw GpuOnly;

    [Glsl("({condition} ? {ifTrue} : {ifFalse})")]
    public static bool Run(bool condition, bool ifTrue, bool ifFalse) => throw GpuOnly;
}
