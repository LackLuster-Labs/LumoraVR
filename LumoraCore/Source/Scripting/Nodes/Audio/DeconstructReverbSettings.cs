// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("Audio/Reverb/Deconstruct Reverb Settings", Description = "Separates reverb settings into individual values for inspection or editing.")]
public sealed class DeconstructReverbSettings : ValueNode
{
    public static void Run(ReverbSettings settings, out float preDelay, out float crossover, out float lowRT60, out float midRT60,
        out float hfDamping, out float eq1Frequency, out float eq1Level, out float eq2Frequency, out float eq2Level, out float mix,
        out float level)
    {
        preDelay = settings.PreDelay;
        crossover = settings.Crossover;
        lowRT60 = settings.LowRT60;
        midRT60 = settings.MidRT60;
        hfDamping = settings.HFDamping;
        eq1Frequency = settings.EQ1Frequency;
        eq1Level = settings.EQ1Level;
        eq2Frequency = settings.EQ2Frequency;
        eq2Level = settings.EQ2Level;
        mix = settings.Mix;
        level = settings.Level;
    }
}
