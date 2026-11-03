// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every band of an analyser's spectrum, from the lowest up, as amplitudes or in decibels. Empty for an analyser the
// script cannot see or one not listening yet.
[Node("Audio/Analysis/Band Levels", Capability.WorldRead, Description = "Every band of an analyser's spectrum, from the lowest up, as amplitudes or in decibels.",
    Keywords = "frequency bands, equalizer, visualizer, eq bars, bass, treble")]
public sealed class BandLevels : ReadNode
{
    [return: Port("levels")]
    public static ScriptList<float> Run(ScriptContext ctx, AudioAnalyzer? analyzer, bool decibels = false)
    {
        var levels = new ScriptList<float>(ctx.Collections);
        if (!ScriptReads.Visible(ctx, analyzer))
            return levels;
        int count = analyzer!.ActiveBandCount;
        for (int band = 0; band < count; band++)
        {
            float level = analyzer.BandLevel(band);
            levels.Add(decibels ? ScriptAudioAnalysis.Decibels(level) : level);
        }
        return levels;
    }
}
