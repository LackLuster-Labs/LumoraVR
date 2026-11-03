// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Builds a reverb settings value from delay, decay times, damping, EQ, mix and level. An unwired input is
// the medium room a new reverb starts as. Milliseconds, hertz, seconds and decibels, as on the component.
[Node("Audio/Reverb/Construct Reverb Settings", Description = "Builds a reverb settings value from delay, decay times, damping, EQ, mix and level.",
    Keywords = "echo, room sound, reverb parameters")]
public sealed class ConstructReverbSettings : ValueNode
{
    [return: Port("settings")]
    public static ReverbSettings Run(float preDelay = ReverbSettings.DefaultPreDelay, float crossover = ReverbSettings.DefaultCrossover,
        float lowRT60 = ReverbSettings.DefaultLowRT60, float midRT60 = ReverbSettings.DefaultMidRT60,
        float hfDamping = ReverbSettings.DefaultHFDamping, float eq1Frequency = ReverbSettings.DefaultEQ1Frequency,
        float eq1Level = ReverbSettings.DefaultEQ1Level, float eq2Frequency = ReverbSettings.DefaultEQ2Frequency,
        float eq2Level = ReverbSettings.DefaultEQ2Level, float mix = ReverbSettings.DefaultMix, float level = ReverbSettings.DefaultLevel)
        => new()
        {
            PreDelay = preDelay,
            Crossover = crossover,
            LowRT60 = lowRT60,
            MidRT60 = midRT60,
            HFDamping = hfDamping,
            EQ1Frequency = eq1Frequency,
            EQ1Level = eq1Level,
            EQ2Frequency = eq2Frequency,
            EQ2Level = eq2Level,
            Mix = mix,
            Level = level,
        };
}
