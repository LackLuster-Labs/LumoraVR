// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Media;

// Anything with a transport: a video, an animation, a sound. Buttons, seekers and drivers talk to
// this and never to the concrete component, so one set of controls works for all of them. Positions
// are seconds; NormalizedPosition is 0..1 of ClipLength and -1 when there is no length (a live
// stream). IsStreaming is exactly "ClipLength is infinite". -xlinka
public interface IPlayable
{
    float Position { get; set; }
    float Speed { get; set; }
    double ClipLength { get; }
    bool IsStreaming { get; }
    bool IsPlaying { get; }
    bool IsFinished { get; }
    float NormalizedPosition { get; set; }
    bool Loop { get; set; }

    void Play();
    void Stop();
    void Pause();
    void Resume();
}

public enum PlaybackAction : byte
{
    None,
    Play,
    Resume,
    Pause,
    Stop,
    ToggleResumePause,
    TogglePlayStop,
    // Loop on and off, wherever the playhead is.
    ToggleLoop,
    EnableLoop,
    DisableLoop,
}
