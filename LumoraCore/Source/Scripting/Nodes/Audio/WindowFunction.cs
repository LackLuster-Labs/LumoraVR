// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The values of a window shape over length samples. Periodic is the form to multiply samples by before a
// transform; symmetric peaks at the middle. Beta is the Kaiser shape's, ignored by the others. Up to 65536
// values; a length of 0 or less gives none.
[Node("Audio/Signal Processing/Window", Description = "The values of a window shape over length samples.",
    Keywords = "hann, hamming, window function, taper")]
public sealed class WindowFunction : ReadNode
{
    [return: Port("values")]
    public static ScriptList<float> Run(ScriptContextBase ctx, int length = 1024, WindowShape shape = WindowShape.Hann, bool periodic = true,
        float beta = SpectralWindow.DefaultKaiserBeta)
    {
        var values = new ScriptList<float>(ctx.Collections);
        int n = System.Math.Clamp(length, 0, ScriptAudioAnalysis.MaxSamples);
        if (n == 0)
            return values;
        foreach (float v in SpectralWindow.Create(n, shape, periodic, beta))
            values.Add(v);
        return values;
    }
}
