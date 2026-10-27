// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Utility;

// Drives a float that climbs at a constant rate and wraps over a span, or swings back down it: a progress
// that loops, a gradient that cycles. The one-number sibling of UVPanner, on the same session clock, so every
// peer sees the same value at the same moment and a late joiner does not start from zero.
//
// A span with no end (infinity, or the largest float) never wraps: the value climbs from Origin, the session
// second it was started at, rather than from the clock's own zero, which is billions of seconds back.
//
// Something else writing the field it drives does not fight it: the write moves Offset so the run carries on
// from the written value. A button that sets a progress back to zero restarts the sweep from there. -xlinka
[ComponentCategory("Utility/Values")]
[DefaultUpdateOrder(-100)]
public class ValuePanner : Component
{
    // Units per second.
    public readonly Sync<float> Speed;

    // Added before the value wraps, for running several of these out of step.
    public readonly Sync<float> StartOffset;

    // The span the value wraps over. Zero or less wraps over one; no end never wraps.
    public readonly Sync<float> Repeat;

    // Run back down the span instead of jumping to its start.
    public readonly Sync<bool> PingPong;

    // Added after the value wraps.
    public readonly Sync<float> Offset;

    // The session second the run counts from.
    public readonly Sync<double> Origin;

    public readonly HoldingFieldDrive<float> Target;

    public ValuePanner()
    {
        Speed = new Sync<float>(this, 1f);
        StartOffset = new Sync<float>(this, 0f);
        Repeat = new Sync<float>(this, 1f);
        PingPong = new Sync<bool>(this, false);
        Offset = new Sync<float>(this, 0f);
        Origin = new Sync<double>(this, 0d);
        Target = new HoldingFieldDrive<float>(this) { LocalValueOnly = true, Holds = () => true };
        Target.SetupValueSetHook(Rebase);
    }

    public override void OnAttach()
    {
        base.OnAttach();
        Origin.Value = UtilityClock.Seconds(World);
    }

    public bool Wraps => Repeat.Value < float.MaxValue;

    // Before the offset after the wrap.
    public float Wrapped
    {
        get
        {
            double run = (UtilityClock.Seconds(World) - Origin.Value) * Speed.Value + StartOffset.Value;
            if (!Wraps)
                return (float)run;
            // Wrapped in double before narrowing: the run since the clock's zero is huge.
            double span = Repeat.Value > 0f ? Repeat.Value : 1.0;
            bool pingPong = PingPong.Value;
            double period = pingPong ? span * 2.0 : span;
            double wrapped = run % period;
            if (wrapped < 0.0)
                wrapped += period;
            if (pingPong && wrapped > span)
                wrapped = period - wrapped;
            return (float)wrapped;
        }
    }

    public float Value => Wrapped + Offset.Value;

    private void Rebase(SyncField<float> field, float value)
    {
        if (Offset.CanWrite)
            Offset.Value = value - Wrapped;
        Target.SetValue(value);
    }

    public override void OnUpdate(float delta)
    {
        if (Target.IsLinkValid)
            Target.SetValue(Value);
    }
}
