// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// An Audio Analyzer on the slot bound as source heard an onset: a drum hit, a note starting, a clap. With
// nothing bound, the analyser on the script's own slot. Heard where the script runs, from that machine's analyser,
// and from one added to the slot after the script started.
[Node("Audio/Analysis/On Beat", Description = "An Audio Analyzer on the slot bound as source heard an onset: a drum hit, a note starting, a clap.",
    Keywords = "beat detection, onset, kick, drum hit, rhythm")]
[SourcedEvent]
public sealed class OnBeat : EventNode<AudioBeat>
{
    public static void Listen(EventHearing<AudioBeat> hearing)
    {
        if (hearing.Slot is not { IsDestroyed: false } slot)
            return;
        AudioAnalyzer? heard = null;
        Action<AudioAnalyzer, float> beat = (analyzer, strength) => hearing.Raise(new AudioBeat(strength, analyzer.Tempo, analyzer.TempoConfidence));
        void Hear()
        {
            if (heard is { IsDestroyed: false } || slot.IsDestroyed || slot.GetComponent<AudioAnalyzer>() is not { IsDestroyed: false } analyzer)
                return;
            if (heard != null)
                heard.Beat -= beat;
            heard = analyzer;
            analyzer.Beat += beat;
        }
        Action<Slot, Component> added = (_, component) =>
        {
            if (component is AudioAnalyzer)
                Hear();
        };
        slot.OnComponentAdded += added;
        Hear();
        hearing.Until(() =>
        {
            slot.OnComponentAdded -= added;
            if (heard != null)
                heard.Beat -= beat;
        });
    }
}
