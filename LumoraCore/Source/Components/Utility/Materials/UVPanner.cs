// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

// Scrolls a UV offset at a constant rate.
//
// The drive targets any float2 field; the one it exists for is a material's TextureOffset, which every
// material provider already carries and already pushes to its shader, so nothing new is needed on the
// asset side to make a conveyor belt or a waterfall move.
//
// Phase comes off the session clock rather than an accumulator, so every peer computes the same offset
// for the same moment and a late joiner does not start the scroll from zero. -xlinka
[ComponentCategory("Utility/Materials")]
[DefaultUpdateOrder(-100)]
public class UVPanner : Component
{
    // UV units per second, per axis.
    public readonly Sync<float2> Speed;

    // Added to the scroll before it wraps, for running several panners out of step.
    public readonly Sync<float2> StartOffset;

    // The span the offset wraps over. One is a full tile.
    public readonly Sync<float2> Repeat;

    // Scroll back down the span instead of jumping to its start.
    public readonly Sync<bool> PingPong;

    // A write to the field from anything else moves StartOffset so the scroll carries on from there.
    public readonly HoldingFieldDrive<float2> Offset;

    public UVPanner()
    {
        Speed = new Sync<float2>(this, new float2(0.1f, 0f));
        StartOffset = new Sync<float2>(this, float2.Zero);
        Repeat = new Sync<float2>(this, float2.One);
        PingPong = new Sync<bool>(this, false);
        Offset = new HoldingFieldDrive<float2>(this) { LocalValueOnly = true, Holds = () => true };
        Offset.SetupValueSetHook(Rebase);
    }

    private void Rebase(SyncField<float2> field, float2 value)
    {
        if (StartOffset.CanWrite)
            StartOffset.Value = StartOffset.Value + (value - Position);
        Offset.SetValue(value);
    }

    public float2 Position
    {
        get
        {
            double now = UtilityClock.Seconds(World);
            var speed = Speed.Value;
            var start = StartOffset.Value;
            var repeat = Repeat.Value;
            bool pingPong = PingPong.Value;
            return new float2(
                Advance(now, speed.x, start.x, repeat.x, pingPong),
                Advance(now, speed.y, start.y, repeat.y, pingPong));
        }
    }

    public override void OnUpdate(float delta)
    {
        if (!Offset.IsLinkValid)
            return;
        Offset.SetValue(Position);
    }

    // A span of zero or less would divide by zero, and there is no unwrapped answer worth handing back
    // either: the clock is wall-clock anchored, so the raw scroll is in the tens of billions and a float
    // that large cannot tell one frame from the next. Wrap in double over a full tile instead. A span with
    // no end (a package's never-wrapping scroll is stored as the largest float) is the same full tile: a
    // tiling texture scrolled one tile looks the same as one never wrapped. -xlinka
    private static float Advance(double seconds, float speed, float start, float repeat, bool pingPong)
    {
        double span = repeat > 0f && repeat < float.MaxValue ? repeat : 1.0;
        double period = pingPong ? span * 2.0 : span;
        double wrapped = (seconds * speed + start) % period;
        if (wrapped < 0.0)
            wrapped += period;
        if (pingPong && wrapped > span)
            wrapped = period - wrapped;
        return (float)wrapped;
    }
}
