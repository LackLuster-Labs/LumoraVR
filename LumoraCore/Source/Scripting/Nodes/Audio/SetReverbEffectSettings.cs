// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Writes a reverb effect's settings from one value: every field, or none when any of them is refused.
[Node("Audio/Reverb/Set Reverb Effect Settings", Capability.WorldWrite, Description = "Writes a reverb effect's settings from one value: every field, or none when any of them is refused.")]
public sealed class SetReverbEffectSettings : ActionNode
{
    public static void Run(ScriptContext ctx, ReverbEffect? effect, [Default(ReverbSettings.DefaultPreDelay, ReverbSettings.DefaultCrossover,
        ReverbSettings.DefaultLowRT60, ReverbSettings.DefaultMidRT60, ReverbSettings.DefaultHFDamping, ReverbSettings.DefaultEQ1Frequency,
        ReverbSettings.DefaultEQ1Level, ReverbSettings.DefaultEQ2Frequency, ReverbSettings.DefaultEQ2Level, ReverbSettings.DefaultMix,
        ReverbSettings.DefaultLevel)] ReverbSettings settings)
    {
        if (ScriptReads.Reached(ctx, effect, "reverb effect"))
            ScriptReverb.Write(ctx, effect!.Fields, settings);
    }
}
