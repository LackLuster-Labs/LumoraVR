// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

/// <summary>
/// Plays animation clips on a slot hierarchy.
/// Drives slot transforms over time based on animation data.
/// </summary>
[ComponentCategory("Animation")]
public class Animator : Component
{
    /// <summary>
    /// Whether the animation loops.
    /// </summary>
    public readonly Sync<bool> Loop = new();

    /// <summary>
    /// Playback speed multiplier.
    /// </summary>
    public readonly Sync<float> Speed = new();

    /// <summary>
    /// Current playback position in seconds.
    /// </summary>
    public readonly Sync<float> Position = new();

    /// <summary>
    /// Whether the animation is currently playing.
    /// </summary>
    public readonly Sync<bool> Playing = new();

    public override void OnInit()
    {
        base.OnInit();
        Loop.Value = true;
        Speed.Value = 1.0f;
        // Position = 0.0f (C# default, skip)
        // Playing = false (C# default, skip)
    }
}
