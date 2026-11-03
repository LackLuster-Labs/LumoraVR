// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// One band of an analyser's spectrum, lowest band 0: its level as an amplitude (a sine of amplitude A inside
// the band reads A) and in decibels. A band that does not exist, or an analyser the script cannot see, reads
// 0 and minus infinity.
[Node("Audio/Analysis/Band Level", Capability.WorldRead, Description = "One band of an analyser's spectrum, lowest band 0: its level as an amplitude (a sine of amplitude A inside the band reads A) and in decibels.",
    Keywords = "frequency bands, equalizer, visualizer, eq bars, bass, treble")]
public sealed class BandLevel : ReadNode
{
    [return: Port("level")]
    public static float Run(ScriptContext ctx, AudioAnalyzer? analyzer, int band, out float decibels)
    {
        float level = ScriptReads.Visible(ctx, analyzer) ? analyzer!.BandLevel(band) : 0f;
        decibels = ScriptAudioAnalysis.Decibels(level);
        return level;
    }
}
