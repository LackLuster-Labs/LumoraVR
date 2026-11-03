// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every value of a spread out by the shape of b and added up, a.Count + b.Count - 1 values long: samples run
// through an impulse response, a filter's taps, an echo. Worked out through FFTs, so long lists cost little
// more than short ones. Up to 65536 values each; one that is not a number counts as 0. An empty list, or none,
// gives none.
[Node("Audio/Signal Processing/Convolve", Description = "Every value of a spread out by the shape of b and added up, a.Count + b.Count - 1 values long: samples run through an impulse response, a filter's taps, an echo.",
    Keywords = "convolution, impulse response, fir filter")]
public sealed class Convolve : ReadNode
{
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? a, ScriptList<float>? b)
    {
        var result = new ScriptList<float>(ctx.Collections);
        int na = System.Math.Min(a?.Count ?? 0, ScriptAudioAnalysis.MaxSamples);
        int nb = System.Math.Min(b?.Count ?? 0, ScriptAudioAnalysis.MaxSamples);
        if (na == 0 || nb == 0)
            return result;

        int count = na + nb - 1;
        int size = FftSizes.NextFast(count);
        var fft = RealFft.Get(size);
        var reA = new float[fft.BinCount];
        var imA = new float[fft.BinCount];
        var reB = new float[fft.BinCount];
        var imB = new float[fft.BinCount];
        fft.Forward(Padded(a!, na, size), reA, imA);
        fft.Forward(Padded(b!, nb, size), reB, imB);
        for (int k = 0; k < fft.BinCount; k++)
        {
            float re = reA[k] * reB[k] - imA[k] * imB[k];
            float im = reA[k] * imB[k] + imA[k] * reB[k];
            reA[k] = re;
            imA[k] = im;
        }
        var output = new float[size];
        fft.Inverse(reA, imA, output);
        for (int i = 0; i < count; i++)
            result.Add(output[i]);
        return result;
    }

    private static float[] Padded(ScriptList<float> list, int count, int size)
    {
        var frame = new float[size];
        var items = list.Items;
        for (int i = 0; i < count; i++)
        {
            float v = items[i];
            frame[i] = float.IsFinite(v) ? v : 0f;
        }
        return frame;
    }
}
