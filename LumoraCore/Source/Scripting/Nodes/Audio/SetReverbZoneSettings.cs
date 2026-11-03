// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Audio;
using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Writes a reverb zone's settings from one value: every field, or none when any of them is refused. The
// zone's radius, blend distance and priority are its own fields and stay as they are.
[Node("Audio/Reverb/Set Reverb Zone Settings", Capability.WorldWrite, Description = "Writes a reverb zone's settings from one value: every field, or none when any of them is refused.")]
public sealed class SetReverbZoneSettings : ActionNode
{
    public static void Run(ScriptContext ctx, ReverbZone? zone, [Default(ReverbSettings.DefaultPreDelay, ReverbSettings.DefaultCrossover,
        ReverbSettings.DefaultLowRT60, ReverbSettings.DefaultMidRT60, ReverbSettings.DefaultHFDamping, ReverbSettings.DefaultEQ1Frequency,
        ReverbSettings.DefaultEQ1Level, ReverbSettings.DefaultEQ2Frequency, ReverbSettings.DefaultEQ2Level, ReverbSettings.DefaultMix,
        ReverbSettings.DefaultLevel)] ReverbSettings settings)
    {
        if (ScriptReads.Reached(ctx, zone, "reverb zone"))
            ScriptReverb.Write(ctx, zone!.Fields, settings);
    }
}
