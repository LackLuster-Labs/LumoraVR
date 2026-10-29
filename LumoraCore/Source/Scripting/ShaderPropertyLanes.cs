// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Scripting;

// How a property's one float4 reads as the type it is. A float is the first number, a float2 the first two, a
// float3 the first three, a float4 all four; a colour is its four channels as the material keeps them, the
// numbers its colour picker shows, and a colour declared as three numbers reads with full alpha. A whole
// number is the first number rounded, a switch is on from a half up. A write puts in only the numbers the type
// covers and leaves the rest where they stood. -xlinka
internal static class ShaderPropertyLanes
{
    public static bool IsLaneType(Type type)
        => type == typeof(float) || type == typeof(float2) || type == typeof(float3) || type == typeof(float4)
            || type == typeof(color) || type == typeof(colorHDR) || type == typeof(int) || type == typeof(bool);

    public static bool IsColor(Type type) => type == typeof(color) || type == typeof(colorHDR);

    // Whether a property of this kind reads as the type: only as what it is, the way a field is only its own type.
    public static bool Fits(Type type, ShaderUniformParam param) => param.Type.Value switch
    {
        ShaderUniformType.Float => type == typeof(float),
        ShaderUniformType.Vec2 => type == typeof(float2),
        ShaderUniformType.Vec3 => type == typeof(float3) || (param.IsColor.Value && IsColor(type)),
        ShaderUniformType.Vec4 => type == typeof(float4) || IsColor(type),
        ShaderUniformType.Int => type == typeof(int),
        ShaderUniformType.Bool => type == typeof(bool),
        _ => false,
    };

    // The type a property reads as when nothing asks for one.
    public static Type? Natural(ShaderUniformParam param) => param.Type.Value switch
    {
        ShaderUniformType.Float => typeof(float),
        ShaderUniformType.Vec2 => typeof(float2),
        ShaderUniformType.Vec3 => param.IsColor.Value ? typeof(colorHDR) : typeof(float3),
        ShaderUniformType.Vec4 => param.IsColor.Value ? typeof(colorHDR) : typeof(float4),
        ShaderUniformType.Int => typeof(int),
        ShaderUniformType.Bool => typeof(bool),
        _ => null,
    };

    public static T Read<T>(float4 v, bool threeNumbers)
    {
        if (typeof(T) == typeof(float))
            return (T)(object)v.x;
        if (typeof(T) == typeof(float2))
            return (T)(object)new float2(v.x, v.y);
        if (typeof(T) == typeof(float3))
            return (T)(object)new float3(v.x, v.y, v.z);
        if (typeof(T) == typeof(float4))
            return (T)(object)v;
        if (typeof(T) == typeof(colorHDR))
            return (T)(object)new colorHDR(v.x, v.y, v.z, threeNumbers ? 1f : v.w);
        if (typeof(T) == typeof(color))
            return (T)(object)new color(v.x, v.y, v.z, threeNumbers ? 1f : v.w);
        if (typeof(T) == typeof(int))
            return (T)(object)(float.IsFinite(v.x) ? (int)System.Math.Clamp(MathF.Round(v.x), int.MinValue, int.MaxValue) : 0);
        if (typeof(T) == typeof(bool))
            return (T)(object)(v.x >= 0.5f);
        return default!;
    }

    public static float4 Write<T>(float4 v, T value, bool threeNumbers)
    {
        switch (value)
        {
            case float f:
                v.x = f;
                break;
            case float2 f2:
                v.x = f2.x;
                v.y = f2.y;
                break;
            case float3 f3:
                v.x = f3.x;
                v.y = f3.y;
                v.z = f3.z;
                break;
            case float4 f4:
                v = f4;
                break;
            case colorHDR hdr:
                v.x = hdr.r;
                v.y = hdr.g;
                v.z = hdr.b;
                if (!threeNumbers)
                    v.w = hdr.a;
                break;
            case color c:
                v.x = c.r;
                v.y = c.g;
                v.z = c.b;
                if (!threeNumbers)
                    v.w = c.a;
                break;
            case int i:
                v.x = i;
                break;
            case bool b:
                v.x = b ? 1f : 0f;
                break;
        }
        return v;
    }

    public static bool IsThreeNumbers(ShaderUniformParam param) => param.Type.Value == ShaderUniformType.Vec3;
}
