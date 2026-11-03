// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where an analyser's spectrum carries its weight. The centroid is the magnitude-weighted mean frequency in
// hertz, high for bright sounds; flatness runs from near 0 for clean tones to near 1 for noise; rolloff is
// the frequency below which 85% of the power lies. All 0 for silence or an analyser the script cannot see.
[Node("Audio/Analysis/Spectral Centroid", Capability.WorldRead, Description = "Where an analyser's spectrum carries its weight.",
    Keywords = "brightness, timbre, flatness, rolloff")]
public sealed class SpectralCentroid : ReadNode
{
    [return: Port("hz")]
    public static float Run(ScriptContext ctx, AudioAnalyzer? analyzer, out float flatness, out float rolloff)
    {
        bool visible = ScriptReads.Visible(ctx, analyzer);
        flatness = visible ? analyzer!.SpectralFlatness : 0f;
        rolloff = visible ? analyzer!.SpectralRolloff : 0f;
        return visible ? analyzer!.SpectralCentroid : 0f;
    }
}
