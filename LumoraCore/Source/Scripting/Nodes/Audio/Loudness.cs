// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How loud an analyser hears its output. RMS and peak are amplitudes over the last 50 ms (a full-scale sine
// reads 0.707 and 1); momentary, short-term and integrated are EBU R128 loudness in LUFS over 400 ms, 3 s
// and everything since the analyser started, minus infinity for silence. An analyser the script cannot see
// hears silence.
[Node("Audio/Analysis/Loudness", Capability.WorldRead, Description = "How loud an analyser hears its output.",
    Keywords = "volume, level, how loud, rms, peak, lufs, meter, amplitude")]
public sealed class Loudness : ReadNode
{
    public static void Run(ScriptContext ctx, AudioAnalyzer? analyzer, out float rms, out float peak, out float momentary,
        out float shortTerm, out float integrated)
    {
        bool visible = ScriptReads.Visible(ctx, analyzer);
        rms = visible ? analyzer!.Rms : 0f;
        peak = visible ? analyzer!.Peak : 0f;
        momentary = visible ? analyzer!.MomentaryLoudness : float.NegativeInfinity;
        shortTerm = visible ? analyzer!.ShortTermLoudness : float.NegativeInfinity;
        integrated = visible ? analyzer!.IntegratedLoudness : float.NegativeInfinity;
    }
}
