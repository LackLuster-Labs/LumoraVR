// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Input;

// SEATED MODE
//
// Someone playing in a chair wears their headset well under the eye height their UserHeight gives, so their
// avatar crouches all evening. Turned on in a headset, this takes the headset's height over the play-space floor
// at that moment and raises the whole tracked space by the difference. Head, hands, trackers and the eye cameras
// all hang from that one slot (UserRoot's tracking space), so they rise together and everyone else sees the
// avatar standing. Off drops the lift; off and on again measures again. On a screen there is no headset to
// measure and the lift is zero. -xlinka
public static class SeatedMode
{
    // Lower than this is not a seated head, so a measurement there is refused.
    private const float LowestHead = 0.2f;

    public static bool Enabled => EngineSettings.SeatedMode;

    public static bool InHeadset => Engine.Current?.InputInterface?.IsVRActive == true;

    // Where a standing person's eyes are for the calibrated height: the rule the screen head uses.
    public static float StandingEyeHeight
    {
        get
        {
            float height = Engine.Current?.InputInterface?.UserHeight ?? InputInterface.DEFAULT_USER_HEIGHT;
            return MathF.Max(height - InputInterface.EYE_HEAD_OFFSET, LowestHead);
        }
    }

    // Metres of play space the tracked body is raised by now. Derived every time from the stored measurement
    // and the current height, never added to itself.
    public static float Lift => EngineSettings.SeatedMode && InHeadset ? LiftFor(EngineSettings.SeatedHeadHeight) : 0f;

    public static float LiftFor(float seatedHead)
        => seatedHead < LowestHead || !float.IsFinite(seatedHead) ? 0f : MathF.Max(StandingEyeHeight - seatedHead, 0f);

    // The headset over the play-space floor, before any lift or user scale.
    public static bool TryMeasureHead(out float height)
    {
        height = 0f;
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsVRActive)
            return false;
        if (input.GetBodyNode(BodyNode.Head) is { IsTracking: true } head)
            height = head.RawPosition.y;
        else if (input.HeadDevice is { IsTracked: true } device)
            height = device.Position.Y;
        else
            return false;
        return float.IsFinite(height) && height >= LowestHead;
    }

    // Turning on measures, and so does asking for on while already on. Off keeps the last measurement and stops
    // using it. Without a headset to measure it stays off and returns false.
    public static bool SetEnabled(bool on)
    {
        if (!on)
        {
            EngineSettings.SeatedMode = false;
            return true;
        }
        if (!TryMeasureHead(out float head))
            return false;
        EngineSettings.SeatedHeadHeight = head;
        EngineSettings.SeatedMode = true;
        return true;
    }
}
