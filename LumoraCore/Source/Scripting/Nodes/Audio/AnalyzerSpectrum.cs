// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The analyser's own spectrum as it last worked it out: bin 0 at 0 Hz and each bin binWidth hertz above the
// one before, as amplitudes (a sine of amplitude A on a bin reads A) or in decibels against a full-scale sine,
// floored at -120. The analyser's FFT Size decides how many and how fine: size / 2 + 1 bins, binWidth the rate
// over the size. Empty for an analyser the script cannot see or one not listening yet.
[Node("Audio/Analysis/Spectrum", Capability.WorldRead, Description = "Reads the audio analyser's latest frequency spectrum as amplitudes or decibels.",
    Keywords = "fft, frequency spectrum, frequency bins, visualizer, equalizer")]
public sealed class AnalyzerSpectrum : ReadNode
{
    [return: Port("magnitudes")]
    public static ScriptList<float> Run(ScriptContext ctx, AudioAnalyzer? analyzer, out float binWidth, bool decibels = false)
    {
        binWidth = 0f;
        var magnitudes = new ScriptList<float>(ctx.Collections);
        if (!ScriptReads.Visible(ctx, analyzer))
            return magnitudes;
        binWidth = analyzer!.SpectrumBinWidth;
        if (decibels)
        {
            var levels = new float[analyzer.SpectrumMagnitudes.Length];
            int n = analyzer.CopySpectrumDecibels(levels);
            for (int i = 0; i < n; i++)
                magnitudes.Add(levels[i]);
            return magnitudes;
        }
        foreach (float m in analyzer.SpectrumMagnitudes)
            magnitudes.Add(m);
        return magnitudes;
    }
}
