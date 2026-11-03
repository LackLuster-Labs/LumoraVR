// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Audio;
using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("Audio/Analysis/On Audio Beat", Description = "Starts flow when a beat is detected in the source audio, with beat strength, tempo and confidence.",
    Keywords = "beat detection, onset, kick, drum hit, rhythm")]
[SourcedEvent("component")]
public sealed class OnAudioBeat : EventNode<AudioBeat>
{
    public static void Listen(EventHearing<AudioBeat> hearing)
    {
        if (hearing.Source is not Component { IsDestroyed: false } source || hearing.Home is not { IsDestroyed: false } home)
            return;
        var analyzer = source as AudioAnalyzer;
        if (analyzer == null)
        {
            if (source is not AudioOutput && source is not IWorldAudioDataSource)
                return;
            var listening = home.AddLocalSlot("Beat Analysis");
            analyzer = listening.AttachComponent<AudioAnalyzer>();
            analyzer.Source.Target = source;
            hearing.Until(() => { if (!listening.IsDestroyed) listening.Destroy(); });
        }
        Action<AudioAnalyzer, float> beat = (_, strength) => hearing.Raise(new AudioBeat(strength, analyzer.Tempo, analyzer.TempoConfidence));
        analyzer.Beat += beat;
        hearing.Until(() => analyzer.Beat -= beat);
    }
}
