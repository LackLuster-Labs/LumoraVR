// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

public static class DrivenValueTypes
{
    // Deliberately NOT a second hand-kept list. GenericComponentTypes.ValueTypes is what the component
    // browser will actually offer, so anything else here would either advertise a type nobody can pick
    // or refuse one that is already on the menu. Enums ride along: both coders handle them. -xlinka
    private static readonly HashSet<Type> _primitives = new(GenericComponentTypes.ValueTypes);

    public static bool IsPrimitive(Type type) => type != null && (_primitives.Contains(type) || type.IsEnum);

    public static bool SupportsBlend(Type type) => type != null && ValueOpsRegistry.BlendTypes.Contains(type);

    public static bool SupportsStep(Type type) => type != null && ValueOpsRegistry.StepTypes.Contains(type);
}

// Type sets kept next to the delegate table so the two can never disagree about what is implemented.
internal static class ValueOpsRegistry
{
    public static readonly HashSet<Type> BlendTypes = new()
    {
        typeof(float), typeof(double), typeof(int), typeof(long),
        typeof(float2), typeof(float3), typeof(float4), typeof(floatQ),
        typeof(color), typeof(colorHDR),
        typeof(int2), typeof(int3), typeof(int4),
    };

    public static readonly HashSet<Type> StepTypes = new()
    {
        typeof(int), typeof(long), typeof(float), typeof(double),
        typeof(float2), typeof(float3), typeof(float4), typeof(floatQ),
        typeof(color), typeof(colorHDR),
        typeof(int2), typeof(int3), typeof(int4),
    };
}

// Per-type arithmetic for the generic utility components: blending, stepping and range wrapping.
//
// The delegates are built once in the static constructor and cast through object exactly once each.
// Doing the type test at the call site instead would box both operands and the result on every push,
// which a smoother pays every frame on every driven field. A type with no entry leaves its delegate
// null, and that null IS the unsupported answer the IsValidGenericType guards report. -xlinka
internal static class ValueOps<T>
{
    // Rotations slerp, everything else is a component-wise lerp.
    public static readonly Func<T, T, float, T>? Blend;

    // Rotations compose, everything else adds.
    public static readonly Func<T, T, T>? Step;

    // For measuring how far a stepped value has travelled through its range.
    public static readonly Func<T, T, T>? Difference;

    // Component-wise, into [min, max].
    public static readonly Func<T, T, T, T>? Clamp;

    // Component-wise, into [min, max), for step-with-wraparound.
    public static readonly Func<T, T, T, T>? Wrap;

    public static bool CanBlend => Blend != null;

    public static bool CanStep => Step != null;

    static ValueOps()
    {
        if (typeof(T) == typeof(float))
        {
            Blend = Cast3F<float>((a, b, t) => a + (b - a) * t);
            Step = Cast2<float>((a, b) => a + b);
            Difference = Cast2<float>((a, b) => a - b);
            Clamp = Cast3<float>(LuminaMath.Clamp);
            Wrap = Cast3<float>(WrapScalar);
        }
        else if (typeof(T) == typeof(double))
        {
            Blend = Cast3F<double>((a, b, t) => a + (b - a) * t);
            Step = Cast2<double>((a, b) => a + b);
            Difference = Cast2<double>((a, b) => a - b);
            Clamp = Cast3<double>((v, lo, hi) => System.Math.Max(lo, System.Math.Min(hi, v)));
            Wrap = Cast3<double>((v, lo, hi) => WrapScalar((float)v, (float)lo, (float)hi));
        }
        else if (typeof(T) == typeof(int))
        {
            Blend = Cast3F<int>((a, b, t) => (int)System.Math.Round(a + (b - a) * t));
            Step = Cast2<int>((a, b) => a + b);
            Difference = Cast2<int>((a, b) => a - b);
            Clamp = Cast3<int>((v, lo, hi) => System.Math.Max(lo, System.Math.Min(hi, v)));
            Wrap = Cast3<int>((v, lo, hi) => (int)WrapScalar(v, lo, hi));
        }
        else if (typeof(T) == typeof(long))
        {
            Blend = Cast3F<long>((a, b, t) => (long)System.Math.Round(a + (b - a) * t));
            Step = Cast2<long>((a, b) => a + b);
            Difference = Cast2<long>((a, b) => a - b);
            Clamp = Cast3<long>((v, lo, hi) => System.Math.Max(lo, System.Math.Min(hi, v)));
            Wrap = Cast3<long>((v, lo, hi) => (long)WrapScalar(v, lo, hi));
        }
        else if (typeof(T) == typeof(float2))
        {
            Blend = Cast3F<float2>(float2.Lerp);
            Step = Cast2<float2>((a, b) => a + b);
            Difference = Cast2<float2>((a, b) => a - b);
            Clamp = Cast3<float2>((v, lo, hi) => new float2(
                LuminaMath.Clamp(v.x, lo.x, hi.x), LuminaMath.Clamp(v.y, lo.y, hi.y)));
            Wrap = Cast3<float2>((v, lo, hi) => new float2(
                WrapScalar(v.x, lo.x, hi.x), WrapScalar(v.y, lo.y, hi.y)));
        }
        else if (typeof(T) == typeof(float3))
        {
            Blend = Cast3F<float3>(float3.Lerp);
            Step = Cast2<float3>((a, b) => a + b);
            Difference = Cast2<float3>((a, b) => a - b);
            Clamp = Cast3<float3>((v, lo, hi) => new float3(
                LuminaMath.Clamp(v.x, lo.x, hi.x), LuminaMath.Clamp(v.y, lo.y, hi.y),
                LuminaMath.Clamp(v.z, lo.z, hi.z)));
            Wrap = Cast3<float3>((v, lo, hi) => new float3(
                WrapScalar(v.x, lo.x, hi.x), WrapScalar(v.y, lo.y, hi.y), WrapScalar(v.z, lo.z, hi.z)));
        }
        else if (typeof(T) == typeof(float4))
        {
            Blend = Cast3F<float4>(float4.Lerp);
            Step = Cast2<float4>((a, b) => a + b);
            Difference = Cast2<float4>((a, b) => a - b);
            Clamp = Cast3<float4>((v, lo, hi) => new float4(
                LuminaMath.Clamp(v.x, lo.x, hi.x), LuminaMath.Clamp(v.y, lo.y, hi.y),
                LuminaMath.Clamp(v.z, lo.z, hi.z), LuminaMath.Clamp(v.w, lo.w, hi.w)));
            Wrap = Cast3<float4>((v, lo, hi) => new float4(
                WrapScalar(v.x, lo.x, hi.x), WrapScalar(v.y, lo.y, hi.y),
                WrapScalar(v.z, lo.z, hi.z), WrapScalar(v.w, lo.w, hi.w)));
        }
        else if (typeof(T) == typeof(int2))
        {
            // Whole-number vectors blend through float and round each part, the way int does.
            Blend = Cast3F<int2>((a, b, t) => new int2(Round(a.x, b.x, t), Round(a.y, b.y, t)));
            Step = Cast2<int2>((a, b) => a + b);
            Difference = Cast2<int2>((a, b) => a - b);
            Clamp = Cast3<int2>((v, lo, hi) => new int2(ClampInt(v.x, lo.x, hi.x), ClampInt(v.y, lo.y, hi.y)));
            Wrap = Cast3<int2>((v, lo, hi) => new int2((int)WrapScalar(v.x, lo.x, hi.x), (int)WrapScalar(v.y, lo.y, hi.y)));
        }
        else if (typeof(T) == typeof(int3))
        {
            Blend = Cast3F<int3>((a, b, t) => new int3(Round(a.x, b.x, t), Round(a.y, b.y, t), Round(a.z, b.z, t)));
            Step = Cast2<int3>((a, b) => a + b);
            Difference = Cast2<int3>((a, b) => a - b);
            Clamp = Cast3<int3>((v, lo, hi) => new int3(ClampInt(v.x, lo.x, hi.x), ClampInt(v.y, lo.y, hi.y),
                ClampInt(v.z, lo.z, hi.z)));
            Wrap = Cast3<int3>((v, lo, hi) => new int3((int)WrapScalar(v.x, lo.x, hi.x), (int)WrapScalar(v.y, lo.y, hi.y),
                (int)WrapScalar(v.z, lo.z, hi.z)));
        }
        else if (typeof(T) == typeof(int4))
        {
            Blend = Cast3F<int4>((a, b, t) => new int4(Round(a.x, b.x, t), Round(a.y, b.y, t), Round(a.z, b.z, t),
                Round(a.w, b.w, t)));
            Step = Cast2<int4>((a, b) => a + b);
            Difference = Cast2<int4>((a, b) => a - b);
            Clamp = Cast3<int4>((v, lo, hi) => new int4(ClampInt(v.x, lo.x, hi.x), ClampInt(v.y, lo.y, hi.y),
                ClampInt(v.z, lo.z, hi.z), ClampInt(v.w, lo.w, hi.w)));
            Wrap = Cast3<int4>((v, lo, hi) => new int4((int)WrapScalar(v.x, lo.x, hi.x), (int)WrapScalar(v.y, lo.y, hi.y),
                (int)WrapScalar(v.z, lo.z, hi.z), (int)WrapScalar(v.w, lo.w, hi.w)));
        }
        else if (typeof(T) == typeof(floatQ))
        {
            Blend = Cast3F<floatQ>(floatQ.Slerp);
            // A rotation steps by composing, not adding, and has no meaningful component range: clamp
            // and wrap stay null so the shift component reports its limits as unsupported instead of
            // handing back an unnormalized quaternion. -xlinka
            Step = Cast2<floatQ>((a, b) => a * b);
            Difference = Cast2<floatQ>((a, b) => b.Inverse * a);
        }
        else if (typeof(T) == typeof(color))
        {
            Blend = Cast3F<color>(color.Lerp);
            Step = Cast2<color>((a, b) => a + b);
            Difference = Cast2<color>((a, b) => a - b);
            Clamp = Cast3<color>((v, lo, hi) => new color(
                LuminaMath.Clamp(v.r, lo.r, hi.r), LuminaMath.Clamp(v.g, lo.g, hi.g),
                LuminaMath.Clamp(v.b, lo.b, hi.b), LuminaMath.Clamp(v.a, lo.a, hi.a)));
            Wrap = Cast3<color>((v, lo, hi) => new color(
                WrapScalar(v.r, lo.r, hi.r), WrapScalar(v.g, lo.g, hi.g),
                WrapScalar(v.b, lo.b, hi.b), WrapScalar(v.a, lo.a, hi.a)));
        }
        else if (typeof(T) == typeof(colorHDR))
        {
            Blend = Cast3F<colorHDR>(colorHDR.Lerp);
            Step = Cast2<colorHDR>((a, b) => a + b);
            Difference = Cast2<colorHDR>((a, b) => a - b);
            Clamp = Cast3<colorHDR>((v, lo, hi) => new colorHDR(
                LuminaMath.Clamp(v.r, lo.r, hi.r), LuminaMath.Clamp(v.g, lo.g, hi.g),
                LuminaMath.Clamp(v.b, lo.b, hi.b), LuminaMath.Clamp(v.a, lo.a, hi.a)));
            Wrap = Cast3<colorHDR>((v, lo, hi) => new colorHDR(
                WrapScalar(v.r, lo.r, hi.r), WrapScalar(v.g, lo.g, hi.g),
                WrapScalar(v.b, lo.b, hi.b), WrapScalar(v.a, lo.a, hi.a)));
        }
    }

    private static int Round(int a, int b, float t) => (int)System.Math.Round(a + (b - a) * t);

    private static int ClampInt(int value, int low, int high) => System.Math.Max(low, System.Math.Min(high, value));

    // An empty range means no wrap. Taking the modulo of zero would divide by zero.
    private static float WrapScalar(float value, float low, float high)
    {
        float span = high - low;
        if (span <= 0f)
            return value;
        float offset = (value - low) % span;
        if (offset < 0f)
            offset += span;
        return low + offset;
    }

    private static Func<T, T, float, T> Cast3F<TConcrete>(Func<TConcrete, TConcrete, float, TConcrete> op)
        => (Func<T, T, float, T>)(object)op;

    private static Func<T, T, T> Cast2<TConcrete>(Func<TConcrete, TConcrete, TConcrete> op)
        => (Func<T, T, T>)(object)op;

    private static Func<T, T, T, T> Cast3<TConcrete>(Func<TConcrete, TConcrete, TConcrete, TConcrete> op)
        => (Func<T, T, T, T>)(object)op;
}

// Seconds on the session clock: the time base for anything whose PHASE has to match across peers.
public static class UtilityClock
{
    // World.Time.TotalTime starts at zero when each peer OPENS the world, so a spinner driven from it
    // sits at a different angle on every machine and a late joiner starts from scratch. The session
    // clock is the authority's reading with this peer's offset folded in, so every peer computes the
    // same angle for the same moment. Detached elements (no world) fall back to wall clock, which is
    // where all of this used to live and is still the right answer for something with no session to
    // agree with. -xlinka
    public static double Seconds(World world)
        => world?.SessionClock.SessionSeconds ?? DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
}
