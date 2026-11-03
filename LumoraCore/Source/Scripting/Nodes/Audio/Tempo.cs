// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The beat an analyser hears, in beats per minute, and how sure it is (0 to 1: a steady pulse near 1, noise
// near 0). 0 BPM until a few seconds of sound have been heard. Fast tempos above about 170 read as half.
[Node("Audio/Analysis/Tempo", Capability.WorldRead, Description = "The beat an analyser hears, in beats per minute, and how sure it is (0 to 1: a steady pulse near 1, noise near 0). 0 BPM until a few seconds of sound have been heard.",
    Keywords = "bpm, beats per minute, rhythm")]
public sealed class Tempo : ReadNode
{
    [return: Port("bpm")]
    public static float Run(ScriptContext ctx, AudioAnalyzer? analyzer, out float confidence)
    {
        bool visible = ScriptReads.Visible(ctx, analyzer);
        confidence = visible ? analyzer!.TempoConfidence : 0f;
        return visible ? analyzer!.Tempo : 0f;
    }
}
