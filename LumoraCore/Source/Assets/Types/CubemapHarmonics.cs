// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// The light a cubemap holds, projected onto nine spherical harmonic coefficients of linear RGB radiance.
// Evaluating the result in a direction gives the light arriving from there, blurred to the second band;
// coefficient 0 times the band 0 basis is the average over the whole sphere.
//
// Samples a grid of at most 32 x 32 texels a face, each weighted by the solid angle it covers. The low bands
// are smooth, so a finer grid changes nothing worth the time. -xlinka
public static class CubemapHarmonics
{
    private const int SamplesPerEdge = 32;

    public static SphericalHarmonicsL2 Project(byte[][] faces, int faceSize, bool hdr)
    {
        if (faces == null || faces.Length != 6 || faceSize <= 0)
            throw new ArgumentException("A cubemap needs six square faces", nameof(faces));

        int stride = System.Math.Max(1, faceSize / SamplesPerEdge);
        int samples = faceSize / stride;
        int pixel = hdr ? 8 : 4;
        Span<float> basis = stackalloc float[SphericalHarmonicsL2.Count];
        var sum = default(SphericalHarmonicsL2);
        double weightTotal = 0;
        float step = 2f / samples;

        for (int f = 0; f < 6; f++)
        {
            var face = faces[f];
            if (face == null || face.Length < faceSize * faceSize * pixel)
                continue;
            for (int sy = 0; sy < samples; sy++)
            {
                int y = sy * stride + stride / 2;
                float t = (y + 0.5f) / faceSize;
                for (int sx = 0; sx < samples; sx++)
                {
                    int x = sx * stride + stride / 2;
                    float s = (x + 0.5f) / faceSize;
                    var ray = CubemapProjector.FaceDirection((CubemapFace)f, s, t);
                    float length = ray.Length;
                    float weight = step * step / (length * length * length);
                    var light = Read(face, (y * faceSize + x) * pixel, hdr);

                    SphericalHarmonics.Basis(ray / length, basis);
                    for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
                        sum[i] += light * (basis[i] * weight);
                    weightTotal += weight;
                }
            }
        }

        if (weightTotal <= 0)
            return default;
        // The grid's weights add up to slightly off 4 pi; scale them back so a flat cube gives a flat answer.
        float normalize = (float)(4.0 * System.Math.PI / weightTotal);
        for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
            sum[i] *= normalize;
        return sum;
    }

    // The radiance averaged over the whole sphere, in linear RGB.
    public static float3 Average(in SphericalHarmonicsL2 harmonics) => harmonics.sh0 * 0.2820948f;

    private static float3 Read(byte[] face, int at, bool hdr)
    {
        if (hdr)
        {
            return new float3(
                Finite((float)BinaryPrimitives.ReadHalfLittleEndian(face.AsSpan(at))),
                Finite((float)BinaryPrimitives.ReadHalfLittleEndian(face.AsSpan(at + 2))),
                Finite((float)BinaryPrimitives.ReadHalfLittleEndian(face.AsSpan(at + 4))));
        }
        return new float3(
            ColorSpace.GammaToLinear(face[at] / 255f),
            ColorSpace.GammaToLinear(face[at + 1] / 255f),
            ColorSpace.GammaToLinear(face[at + 2] / 255f));
    }

    private static float Finite(float value) => float.IsFinite(value) && value > 0f ? value : 0f;
}
