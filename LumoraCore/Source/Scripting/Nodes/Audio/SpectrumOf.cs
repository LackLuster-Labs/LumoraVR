// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The magnitude spectrum of a list of samples, windowed before the transform: bins from 0 Hz up to half the
// rate the samples were taken at, as amplitudes, so a sine landing on a bin reads its amplitude there. A
// list whose length is not made of twos, threes and fives is padded with silence up to the next one that
// is (a power of two stays as it is), which gives length / 2 + 1 bins of that padded length. Up to 65536
// samples are used.
[Node("Audio/Signal Processing/Spectrum Of", Description = "The magnitude spectrum of a list of samples, windowed before the transform: bins from 0 Hz up to half the rate the samples were taken at, as amplitudes, so a sine landing on a bin reads its amplitude there.")]
public sealed class SpectrumOf : ReadNode
{
    [return: Port("magnitudes")]
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? samples, WindowShape window = WindowShape.Hann)
    {
        var magnitudes = new ScriptList<float>(ctx.Collections);
        int count = System.Math.Min(samples?.Count ?? 0, ScriptAudioAnalysis.MaxSamples);
        if (count == 0)
            return magnitudes;
        int size = FftSizes.NextFast(count);
        var shape = SpectralWindow.Create(count, window);
        var frame = new float[size];
        double sum = 0.0;
        var items = samples!.Items;
        for (int i = 0; i < count; i++)
        {
            float v = items[i];
            frame[i] = (float.IsFinite(v) ? v : 0f) * shape[i];
            sum += shape[i];
        }
        var fft = RealFft.Get(size);
        var re = new float[fft.BinCount];
        var im = new float[fft.BinCount];
        fft.Forward(frame, re, im, new float[fft.ScratchLength]);
        float scale = sum > 0.0 ? (float)(1.0 / sum) : 0f;
        int nyquist = size % 2 == 0 ? fft.BinCount - 1 : -1;
        for (int k = 0; k < fft.BinCount; k++)
        {
            float fold = k == 0 || k == nyquist ? 1f : 2f;
            magnitudes.Add(MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) * fold * scale);
        }
        return magnitudes;
    }
}
