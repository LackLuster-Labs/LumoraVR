// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using Lumora.Audio;

namespace Lumora.Core.Components.Audio;

[ComponentCategory("Audio")]
public sealed class ToneGenerator : Component, IWorldAudioDataSource
{
    [Range(0f, 24000f, "0.00")]
    public readonly Sync<float> Frequency = new(440f);
    [Range(0f, 1f, "0.000")]
    public readonly Sync<float> Amplitude = new(0.1f);
    public readonly Sync<bool> Playing = new(true);

    private sealed record Parameters(float Frequency, float Amplitude, bool Active);
    private Parameters _parameters = new(440f, 0.1f, false);
    private double _snapshotTime;
    private double _lastDsp = double.NaN;
    private double _phase;
    private double _frequency = 440;
    private float _gain;
    private float _targetGain;

    public int ChannelCount => 1;
    public bool IsActive
    {
        get
        {
            var values = Volatile.Read(ref _parameters);
            return values.Active && (values.Frequency > 0f && values.Amplitude > 0f || System.MathF.Abs(Volatile.Read(ref _gain)) > 1e-6f);
        }
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        float frequency = float.IsFinite(Frequency.Value) ? System.Math.Clamp(Frequency.Value, 0f, 24000f) : 0f;
        float amplitude = Playing.Value && float.IsFinite(Amplitude.Value) ? System.Math.Clamp(Amplitude.Value, 0f, 1f) : 0f;
        bool active = Enabled.Value && Slot.IsActive && World?.Focus != World.WorldFocus.Background;
        var before = Volatile.Read(ref _parameters);
        if (before.Frequency != frequency || before.Amplitude != amplitude || before.Active != active)
            Volatile.Write(ref _parameters, new Parameters(frequency, amplitude, active));
        Volatile.Write(ref _snapshotTime, Engine.Current?.AudioSystem?.Simulator.DspTime ?? 0);
    }

    public override void OnDisabled()
    {
        Volatile.Write(ref _parameters, Volatile.Read(ref _parameters) with { Active = false });
        base.OnDisabled();
    }

    public override void OnDestroy()
    {
        Volatile.Write(ref _parameters, Volatile.Read(ref _parameters) with { Active = false });
        base.OnDestroy();
    }

    public void Read(Span<StereoSample> buffer, AudioSimulator simulator)
    {
        var values = Volatile.Read(ref _parameters);
        double now = simulator.DspTime;
        if (!values.Active || now - Volatile.Read(ref _snapshotTime) > 0.5)
        {
            buffer.Clear();
            _lastDsp = double.NaN;
            _gain = 0f;
            return;
        }
        // A source can feed several outputs in one mixer frame. Anchor its phase to DSP time so each
        // gets the same samples, and changing frequency does not restart the wave. -xlinka
        if (now != _lastDsp)
        {
            double elapsed = double.IsNaN(_lastDsp) ? 0 : System.Math.Max(0, now - _lastDsp);
            _phase = (_phase + elapsed * _frequency) % 1.0;
            _gain += (_targetGain - _gain) * (float)(1.0 - System.Math.Exp(-elapsed / 0.005));
            _frequency = System.Math.Min(values.Frequency, simulator.SampleRate * 0.49);
            _targetGain = _frequency > 0 ? values.Amplitude : 0f;
            _lastDsp = now;
        }
        for (int i = 0; i < buffer.Length; i++)
        {
            double seconds = i * simulator.InvSampleRate;
            float gain = _gain + (_targetGain - _gain) * (float)(1.0 - System.Math.Exp(-seconds / 0.005));
            float sample = gain * (float)System.Math.Sin(2.0 * System.Math.PI * (_phase + seconds * _frequency));
            buffer[i] = new StereoSample(sample, sample);
        }
    }
}
