// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Samples back from the bins FFT gives: the real and imaginary parts of bins 0 up to half the length, as FFT
// lists them without full (any bins past those are ignored). Length is how many samples come out; 0 takes it
// from the bins as (bins - 1) * 2, which is right for an even length, so an odd one has to be given. Scaled by
// 1 / length, so FFT then Inverse FFT gives the samples back. A bin missing from the shorter list, or one that
// is not a number, counts as 0. Up to 65536 samples.
[Node("Audio/Signal Processing/Inverse FFT", Description = "Samples back from the bins FFT gives: the real and imaginary parts of bins 0 up to half the length, as FFT lists them without full (any bins past those are ignored).")]
public sealed class InverseFft : ReadNode
{
    [return: Port("samples")]
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? real, ScriptList<float>? imaginary, int length = 0)
    {
        var samples = new ScriptList<float>(ctx.Collections);
        int given = System.Math.Max(real?.Count ?? 0, imaginary?.Count ?? 0);
        int count = length > 0 ? length : (given - 1) * 2;
        count = System.Math.Min(count, ScriptAudioAnalysis.MaxSamples);
        if (count <= 0)
            return samples;

        var fft = RealFft.Get(count);
        var re = new float[fft.BinCount];
        var im = new float[fft.BinCount];
        for (int k = 0; k < fft.BinCount; k++)
        {
            re[k] = Bin(real, k);
            im[k] = Bin(imaginary, k);
        }
        var output = new float[count];
        fft.Inverse(re, im, output);
        foreach (float v in output)
            samples.Add(v);
        return samples;
    }

    private static float Bin(ScriptList<float>? list, int k)
    {
        if (list == null || k >= list.Count)
            return 0f;
        float v = list.Items[k];
        return float.IsFinite(v) ? v : 0f;
    }
}
