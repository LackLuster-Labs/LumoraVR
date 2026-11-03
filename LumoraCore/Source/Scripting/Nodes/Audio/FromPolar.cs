// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Bins given as a magnitude and a phase in radians, the way FFT gives them, back as real and imaginary parts,
// the way Inverse FFT takes them: so a spectrum can be shaped by its magnitudes and turned back into samples.
// Pair by pair, as long as the shorter list.
[Node("Audio/Signal Processing/From Polar", Description = "Converts frequency magnitudes and phases into real and imaginary parts for an inverse FFT.")]
public sealed class FromPolar : ReadNode
{
    [return: Port("real")]
    public static ScriptList<float> Run(ScriptContextBase ctx, ScriptList<float>? magnitudes, ScriptList<float>? phases,
        out ScriptList<float> imaginary)
    {
        var real = new ScriptList<float>(ctx.Collections);
        imaginary = new ScriptList<float>(ctx.Collections);
        if (magnitudes == null || phases == null)
            return real;
        int count = System.Math.Min(magnitudes.Count, phases.Count);
        var m = magnitudes.Items;
        var p = phases.Items;
        for (int i = 0; i < count; i++)
        {
            real.Add(m[i] * MathF.Cos(p[i]));
            imaginary.Add(m[i] * MathF.Sin(p[i]));
        }
        return real;
    }
}
