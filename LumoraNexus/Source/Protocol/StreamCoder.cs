// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Math;

namespace Lumora.Nexus.Protocol;

// Generic quantized + delta encode/decode for stream value types - the quantized counterpart to
// SyncCoder's full-precision encoding. Each value is reduced to its float components; each component is
// bit-packed to N bits within a range (absolute for quantized frames, or the change from the previous
// value for delta frames). Quaternions renormalize on compose. Type-keyed, so any supported value type
// works without a per-stream override. -xlinka
//
// The components go through a span on the stack. A table of boxing delegates did this before, and every value
// coded was a box and a float array for it and for each end of its range: a crowd's bones made thousands of
// them a second on the sync threads. The wire is the same. -xlinka
public static class StreamCoder
{
    private static readonly Dictionary<Type, int> Coded = new()
    {
        [typeof(float)] = 1,
        [typeof(double)] = 1,
        [typeof(float2)] = 2,
        [typeof(float3)] = 3,
        [typeof(float4)] = 4,
        [typeof(floatQ)] = 4,
        [typeof(color)] = 4,
    };

    // False means callers fall back to full precision.
    public static bool SupportsQuantization(Type type) => Coded.ContainsKey(type);

    public static int ComponentCount(Type type) => Coded.TryGetValue(type, out var n) ? n : 0;

    // Byte size of a quantized T at the given bit depth. No length prefix on the wire.
    public static int QuantizedByteCount<T>(int bits) => (ComponentCount(typeof(T)) * bits + 7) / 8;

    public static void EncodeQuantized<T>(BitWriter bw, T value, T min, T max, int bits)
    {
        int n = Components<T>();
        Span<float> v = stackalloc float[4];
        Span<float> lo = stackalloc float[4];
        Span<float> hi = stackalloc float[4];
        Extract(value, v); Extract(min, lo); Extract(max, hi);
        for (int i = 0; i < n; i++)
            bw.WriteBits(Quantization.Quantize(v[i], lo[i], hi[i], bits), bits);
    }

    public static T DecodeQuantized<T>(BitReader br, T min, T max, int bits)
    {
        int n = Components<T>();
        Span<float> v = stackalloc float[4];
        Span<float> lo = stackalloc float[4];
        Span<float> hi = stackalloc float[4];
        Extract(min, lo); Extract(max, hi);
        for (int i = 0; i < n; i++)
            v[i] = Quantization.Dequantize(br.ReadBits(bits), lo[i], hi[i], bits);
        return Compose<T>(v);
    }

    // Delta: quantize (value - previous) componentwise within the (small) delta range, reconstructed as
    // previous + dequantized delta. Lets slowly-changing values spend fewer bits than a full frame; the
    // stream sends periodic full keyframes so a lost frame's drift is bounded. -xlinka
    public static void EncodeDelta<T>(BitWriter bw, T value, T previous, T deltaMin, T deltaMax, int bits)
    {
        int n = Components<T>();
        Span<float> v = stackalloc float[4];
        Span<float> p = stackalloc float[4];
        Span<float> lo = stackalloc float[4];
        Span<float> hi = stackalloc float[4];
        Extract(value, v); Extract(previous, p); Extract(deltaMin, lo); Extract(deltaMax, hi);
        for (int i = 0; i < n; i++)
            bw.WriteBits(Quantization.Quantize(v[i] - p[i], lo[i], hi[i], bits), bits);
    }

    public static T DecodeDelta<T>(BitReader br, T previous, T deltaMin, T deltaMax, int bits)
    {
        int n = Components<T>();
        Span<float> v = stackalloc float[4];
        Span<float> p = stackalloc float[4];
        Span<float> lo = stackalloc float[4];
        Span<float> hi = stackalloc float[4];
        Extract(previous, p); Extract(deltaMin, lo); Extract(deltaMax, hi);
        for (int i = 0; i < n; i++)
            v[i] = p[i] + Quantization.Dequantize(br.ReadBits(bits), lo[i], hi[i], bits);
        return Compose<T>(v);
    }

    private static int Components<T>()
    {
        if (typeof(T) == typeof(float) || typeof(T) == typeof(double)) return 1;
        if (typeof(T) == typeof(float2)) return 2;
        if (typeof(T) == typeof(float3)) return 3;
        if (typeof(T) == typeof(float4) || typeof(T) == typeof(floatQ) || typeof(T) == typeof(color)) return 4;
        throw new KeyNotFoundException($"No quantized coder for {typeof(T)}.");
    }

    private static void Extract<T>(T value, Span<float> c)
    {
        if (typeof(T) == typeof(float)) { c[0] = Unsafe.As<T, float>(ref value); return; }
        if (typeof(T) == typeof(double)) { c[0] = (float)Unsafe.As<T, double>(ref value); return; }
        if (typeof(T) == typeof(float2)) { var a = Unsafe.As<T, float2>(ref value); c[0] = a.x; c[1] = a.y; return; }
        if (typeof(T) == typeof(float3)) { var a = Unsafe.As<T, float3>(ref value); c[0] = a.x; c[1] = a.y; c[2] = a.z; return; }
        if (typeof(T) == typeof(float4)) { var a = Unsafe.As<T, float4>(ref value); c[0] = a.x; c[1] = a.y; c[2] = a.z; c[3] = a.w; return; }
        if (typeof(T) == typeof(floatQ)) { var a = Unsafe.As<T, floatQ>(ref value); c[0] = a.x; c[1] = a.y; c[2] = a.z; c[3] = a.w; return; }
        if (typeof(T) == typeof(color)) { var a = Unsafe.As<T, color>(ref value); c[0] = a.r; c[1] = a.g; c[2] = a.b; c[3] = a.a; return; }
        throw new KeyNotFoundException($"No quantized coder for {typeof(T)}.");
    }

    private static T Compose<T>(ReadOnlySpan<float> c)
    {
        if (typeof(T) == typeof(float)) { float v = c[0]; return Unsafe.As<float, T>(ref v); }
        if (typeof(T) == typeof(double)) { double v = c[0]; return Unsafe.As<double, T>(ref v); }
        if (typeof(T) == typeof(float2)) { var v = new float2(c[0], c[1]); return Unsafe.As<float2, T>(ref v); }
        if (typeof(T) == typeof(float3)) { var v = new float3(c[0], c[1], c[2]); return Unsafe.As<float3, T>(ref v); }
        if (typeof(T) == typeof(float4)) { var v = new float4(c[0], c[1], c[2], c[3]); return Unsafe.As<float4, T>(ref v); }
        // Quaternion: renormalize on compose so quantization/delta error doesn't leave it non-unit.
        if (typeof(T) == typeof(floatQ)) { var v = new floatQ(c[0], c[1], c[2], c[3]).Normalized; return Unsafe.As<floatQ, T>(ref v); }
        if (typeof(T) == typeof(color)) { var v = new color(c[0], c[1], c[2], c[3]); return Unsafe.As<color, T>(ref v); }
        throw new KeyNotFoundException($"No quantized coder for {typeof(T)}.");
    }
}
