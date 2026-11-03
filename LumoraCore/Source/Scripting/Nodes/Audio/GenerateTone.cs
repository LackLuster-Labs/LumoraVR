// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("Audio/Generate Tone", Capability.WorldStructure, Description = "Creates or updates a sine tone on a slot, with adjustable frequency, volume and playback.",
    Keywords = "sine wave, beep, oscillator, synth, tone generator")]
public sealed class GenerateTone : ActionNode
{
    public static Component? Run(ScriptContext ctx, Slot? slot, float frequency = 440f, float amplitude = 0.1f, bool playing = true)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return null;
        ScriptAttach.CheckPlace(ctx, slot!);
        var tone = slot!.GetComponent<ToneGenerator>();
        if (tone == null)
            tone = ScriptAttach.Attach(ctx, slot, () => slot.AttachComponent<ToneGenerator>(), made => !made.IsDestroyed);
        ScriptWrites.Write(ctx, tone.Frequency, frequency);
        ScriptWrites.Write(ctx, tone.Amplitude, amplitude);
        ScriptWrites.Write(ctx, tone.Playing, playing);
        foreach (var component in slot.Components)
            if (component is AudioOutput existing && ReferenceEquals(existing.Source.Target, tone))
                return tone;
        ScriptAttach.Attach(ctx, slot, () =>
        {
            var output = slot.AttachComponent<AudioOutput>();
            output.Source.Target = tone;
            return output;
        }, made => ReferenceEquals(made.Source.Target, tone));
        return tone;
    }
}
