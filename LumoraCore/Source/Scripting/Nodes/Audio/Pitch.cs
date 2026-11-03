// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The pitch an analyser hears: its fundamental in hertz, the nearest MIDI note (A4 = 69) and how sure it is,
// 0 to 1. With nothing pitched playing, 0 Hz and note -1.
[Node("Audio/Analysis/Pitch", Capability.WorldRead, Description = "The pitch an analyser hears: its fundamental in hertz, the nearest MIDI note (A4 = 69) and how sure it is, 0 to 1.",
    Keywords = "frequency detection, note detection, tuner, fundamental")]
public sealed class Pitch : ReadNode
{
    [return: Port("hz")]
    public static float Run(ScriptContext ctx, AudioAnalyzer? analyzer, out int note, out float confidence)
    {
        bool visible = ScriptReads.Visible(ctx, analyzer);
        float hz = visible ? analyzer!.Pitch : 0f;
        confidence = visible ? analyzer!.PitchConfidence : 0f;
        note = ScriptAudioAnalysis.NearestNote(hz, 440f, out _);
        return hz;
    }
}
