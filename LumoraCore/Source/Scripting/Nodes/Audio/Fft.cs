// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The Fourier transform of a list of samples, bin by bin: the real and imaginary parts, and the same as a
// magnitude and a phase in radians. Bin k is the sum of sample n times e^(-2 pi i n k / length), unscaled, over
// the samples exactly as given, nothing padded. That is length / 2 + 1 bins, from 0 Hz up to half the rate the
// samples were taken at, bin k sitting at k times rate / length. With full on it is all length bins, the upper
// half the mirror of the lower. A window shapes the samples first; Rectangular leaves them as they are. Inverse
// FFT takes real and imaginary back to the samples. Up to 65536 samples; one that is not a number counts as 0.
[Node("Audio/Signal Processing/FFT", Description = "The Fourier transform of a list of samples, bin by bin: the real and imaginary parts, and the same as a magnitude and a phase in radians.",
    Keywords = "fourier, fast fourier transform, frequency domain")]
public sealed class Fft : ReadNode
{
    [return: Port("real")]
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? samples, out ScriptList<float> imaginary,
        out ScriptList<float> magnitudes, out ScriptList<float> phases, WindowShape window = WindowShape.Rectangular,
        bool full = false)
    {
        var real = new ScriptList<float>(ctx.Collections);
        imaginary = new ScriptList<float>(ctx.Collections);
        magnitudes = new ScriptList<float>(ctx.Collections);
        phases = new ScriptList<float>(ctx.Collections);
        int count = System.Math.Min(samples?.Count ?? 0, ScriptAudioAnalysis.MaxSamples);
        if (count == 0)
            return real;

        var frame = new float[count];
        var shape = window == WindowShape.Rectangular ? null : SpectralWindow.Create(count, window);
        var items = samples!.Items;
        for (int i = 0; i < count; i++)
        {
            float v = items[i];
            frame[i] = (float.IsFinite(v) ? v : 0f) * (shape?[i] ?? 1f);
        }

        var fft = RealFft.Get(count);
        var re = new float[fft.BinCount];
        var im = new float[fft.BinCount];
        fft.Forward(frame, re, im);

        int bins = full ? count : fft.BinCount;
        for (int k = 0; k < bins; k++)
        {
            // Past the half a real signal's spectrum is the lower half mirrored and conjugated.
            bool mirrored = k >= fft.BinCount;
            float r = mirrored ? re[count - k] : re[k];
            float i = mirrored ? -im[count - k] : im[k];
            real.Add(r);
            imaginary.Add(i);
            magnitudes.Add(MathF.Sqrt(r * r + i * i));
            phases.Add(MathF.Atan2(i, r));
        }
        return real;
    }
}
