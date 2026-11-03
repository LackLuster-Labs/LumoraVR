// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The latest samples the analyser heard, oldest first, left and right averaged, and how many a second they
// were heard at: what goes into FFT or Spectrum Of. Up to 16384, fewer while it has heard fewer; empty, at
// rate 0, for an analyser the script cannot see or one not listening yet. What it hears is this machine's
// own, the same as every reading off it.
[Node("Audio/Analysis/Waveform", Capability.WorldRead, Description = "The latest samples the analyser heard, oldest first, left and right averaged, and how many a second they were heard at: what goes into FFT or Spectrum Of.",
    Keywords = "audio samples, oscilloscope, pcm, sample buffer")]
public sealed class Waveform : ReadNode
{
    [return: Port("samples")]
    public static ScriptList<float> Run(ScriptContext ctx, AudioAnalyzer? analyzer, out int rate, int count = 1024)
    {
        rate = 0;
        var samples = new ScriptList<float>(ctx.Collections);
        if (!ScriptReads.Visible(ctx, analyzer) || count <= 0)
            return samples;
        rate = analyzer!.SampleRate;
        var heard = new float[System.Math.Min(count, AudioAnalysisSettings.MaxFftSize)];
        int n = analyzer.CopyRecent(heard);
        for (int i = 0; i < n; i++)
            samples.Add(heard[i]);
        return samples;
    }
}
