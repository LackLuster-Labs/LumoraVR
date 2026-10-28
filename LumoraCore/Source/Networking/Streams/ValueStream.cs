// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Protocol;

namespace Lumora.Core.Networking.Streams;

public enum ValueEncoding
{
    Full,

    Quantized,

    // Quantized full keyframes with bit-packed deltas between - lower bandwidth for slow-changing
    // values.
    Delta
}

public class ValueStream<T> : ImplicitStream, IValue<T>
{
    private struct DataPoint
    {
        public T Value;
        public DateTime Time;

        public DataPoint(T value, DateTime time)
        {
            Value = value;
            Time = time;
        }
    }

    protected T _value = default!;

    protected readonly Sync<bool> _isInterpolated = new();
    protected readonly Sync<float> _interpolationOffset = new();
    protected readonly Sync<ValueEncoding> _encoding = new();
    protected readonly Sync<int> _fullFrameBits = new();
    protected readonly Sync<T> _fullFrameMin = new();
    protected readonly Sync<T> _fullFrameMax = new();
    protected readonly Sync<int> _deltaFrameBits = new();
    protected readonly Sync<T> _deltaFrameMin = new();
    protected readonly Sync<T> _deltaFrameMax = new();

    // Delta-frame state: a periodic full keyframe then bit-packed deltas; bounds drift if a frame is lost.
    private const int KeyframeInterval = 30;
    private T _lastEncodedValue = default!;
    private bool _hasLastEncoded;
    private int _keyframeCounter;
    private T _lastDecodedValue = default!;
    private bool _hasLastDecoded;

    private List<DataPoint> _dataPoints = new();
    private float _timeTransition;
    private DateTime _trailingTime;
    private DateTime _leadingTime;

    private bool _receivedFirstData;

    // SMOOTHING WHAT ARRIVES. A stream whose owner asks for no interpolation (tracking: head, hands, body) used to
    // show each sample the moment it landed, so a remote body moved in steps of the sender's tick, and a sender
    // further off that is sent fewer samples (StreamInterest) would step visibly. Here the last few samples are
    // kept with the owner's own time and played back a little behind the newest, by about the gap they arrive at,
    // so a body moves every frame at whatever rate it is sent. The delay follows the gap slowly, so a change of
    // rate speeds or slows the playback a little instead of jumping it. Remote only; the owner sees its own value.
    // -xlinka
    private const double SmoothMaxDelay = 0.3;
    private const double SmoothBreak = 0.6;
    private T _sample0 = default!, _sample1 = default!, _sample2 = default!;
    private double _sampleTime0, _sampleTime1, _sampleTime2;
    private int _samples;
    private double _sampleGap;
    private double _clockOffset;
    private double _smoothDelay;
    private double _smoothSteppedAt;
    // How late samples land behind the earliest ones, as a peak that fades over a couple of seconds. The playback
    // delay used to be only the gap between samples, but they land when this machine reads the network, once a
    // frame, so a sample sent at 60 Hz arrives up to a frame late and two sometimes come together: the playback
    // caught up with the newest sample on every other frame and held there, and a remote body stood still every
    // second frame whenever nothing else moved. The delay now covers the gap plus that lateness. -xlinka
    private double _latePeak;
    private double _lateAt;

    // Whether samples are smoothed on arrival when the owner did not ask for interpolation.
    protected virtual bool SmoothsArrivals => false;

    private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    public ValueEncoding Encoding
    {
        get => _encoding.Value;
        set
        {
            CheckOwnership();
            _encoding.Value = value;
        }
    }

    public int FullFrameBits
    {
        get => _fullFrameBits.Value;
        set { CheckOwnership(); _fullFrameBits.Value = value; }
    }

    public T FullFrameMin
    {
        get => _fullFrameMin.Value;
        set { CheckOwnership(); _fullFrameMin.Value = value; }
    }

    public T FullFrameMax
    {
        get => _fullFrameMax.Value;
        set { CheckOwnership(); _fullFrameMax.Value = value; }
    }

    public int DeltaFrameBits
    {
        get => _deltaFrameBits.Value;
        set { CheckOwnership(); _deltaFrameBits.Value = value; }
    }

    public T DeltaFrameMin
    {
        get => _deltaFrameMin.Value;
        set { CheckOwnership(); _deltaFrameMin.Value = value; }
    }

    public T DeltaFrameMax
    {
        get => _deltaFrameMax.Value;
        set { CheckOwnership(); _deltaFrameMax.Value = value; }
    }

    public bool IsInterpolated
    {
        get => _isInterpolated.Value;
        set
        {
            CheckOwnership();
            _isInterpolated.Value = value;
        }
    }

    // Seconds.
    public float InterpolationOffset
    {
        get => _interpolationOffset.Value;
        set
        {
            CheckOwnership();
            _interpolationOffset.Value = value;
        }
    }

    private DateTime CurrentTime => Lerp(_trailingTime, _leadingTime, _timeTransition);

    public override bool HasValidData
    {
        get
        {
            if (!_receivedFirstData)
                return IsLocal;
            return true;
        }
    }

    public T Value
    {
        get => _value;
        set
        {
            CheckOwnership();
            _value = value;
        }
    }

    public event Action<IChangeable> Changed = null!;

    protected override void OnInit()
    {
        base.OnInit();

        // Members wire via the worker pipeline now; OnInit just seeds config defaults (in the init phase, no deltas). -xlinka
        _interpolationOffset.Value = 0.05f;
        _encoding.Value = ValueEncoding.Full;
        _fullFrameBits.Value = 16;
        _deltaFrameBits.Value = 8;
    }

    public void SetInterpolation()
    {
        CheckOwnership();
        _isInterpolated.Value = true;
        _interpolationOffset.Value = 0.05f;
    }

    public override void Update()
    {
        if (IsInterpolated && !IsLocal)
        {
            if (_dataPoints.Count == 0)
                return;

            if (_dataPoints.Count == 1)
            {
                _value = _dataPoints[0].Value;
                return;
            }

            var delta = World?.LastDelta ?? 0.016f;
            _trailingTime = _trailingTime.AddSeconds(delta);
            _leadingTime = _leadingTime.AddSeconds(delta);

            var latestTime = _dataPoints[_dataPoints.Count - 1].Time;
            if (_trailingTime > latestTime) _trailingTime = latestTime;
            if (_leadingTime > latestTime) _leadingTime = latestTime;

            _timeTransition = Progress01(_timeTransition, delta / InterpolationOffset);

            var currentTime = CurrentTime;
            int lastIndex = -1;
            for (int i = _dataPoints.Count - 1; i >= 0; i--)
            {
                if (currentTime >= _dataPoints[i].Time)
                {
                    lastIndex = i;
                    break;
                }
            }

            if (lastIndex > 0)
            {
                _dataPoints.RemoveRange(0, System.Math.Min(lastIndex, _dataPoints.Count - 2));
            }

            if (_dataPoints.Count >= 2)
            {
                float lerp = InverseLerp(_dataPoints[0].Time, _dataPoints[1].Time, currentTime);
                _value = Interpolate(_dataPoints[0].Value, _dataPoints[1].Value, lerp);
            }
        }
        else if (_samples > 0 && !IsLocal)
        {
            PlaySamples();
        }

        Changed?.Invoke(this);
    }

    // SYNC THREAD, under the data model lock like every decode.
    private void KeepSample(T value, double time)
    {
        double arrived = Now;
        if (_samples > 0)
        {
            // A clock that went back by more than a moment started again; anything else older is a late packet.
            if (time <= _sampleTime2 && time > _sampleTime2 - 1.0)
                return;
            double gap = time - _sampleTime2;
            // A long silence is a new start, not a slow beat: the body goes straight to where it is now.
            if (gap > SmoothBreak || gap < 0.0)
                _samples = 0;
        }
        if (_samples == 0)
        {
            _sample0 = _sample1 = _sample2 = value;
            _sampleTime0 = _sampleTime1 = _sampleTime2 = time;
            _samples = 1;
            _sampleGap = 0.0;
            _clockOffset = arrived - time;
            _value = value;
            return;
        }
        double step = time - _sampleTime2;
        _sampleGap = _samples < 2 ? step : _sampleGap + (step - _sampleGap) * 0.15;
        _sample0 = _sample1;
        _sampleTime0 = _sampleTime1;
        _sample1 = _sample2;
        _sampleTime1 = _sampleTime2;
        _sample2 = value;
        _sampleTime2 = time;
        _samples = System.Math.Min(_samples + 1, 3);
        // How far the owner's clock runs behind ours plus the trip: an early arrival is believed at once, a late
        // one only a little, so one slow packet does not pull everything after it back.
        double offset = arrived - time;
        _clockOffset += (offset - _clockOffset) * (offset < _clockOffset ? 0.3 : 0.02);
        double late = System.Math.Max(0.0, offset - _clockOffset);
        double faded = _latePeak * System.Math.Exp(-System.Math.Max(0.0, arrived - _lateAt) / 2.0);
        _latePeak = System.Math.Min(System.Math.Max(late, faded), SmoothMaxDelay);
        _lateAt = arrived;
    }

    // WORLD THREAD.
    private void PlaySamples()
    {
        if (_samples < 2)
        {
            _value = _sample2;
            return;
        }
        double now = Now;
        double dt = _smoothSteppedAt > 0.0 ? System.Math.Clamp(now - _smoothSteppedAt, 0.0, 0.1) : 0.0;
        _smoothSteppedAt = now;
        double wanted = System.Math.Min(_sampleGap * 1.2 + _latePeak + 0.002, SmoothMaxDelay);
        double drift = wanted - _smoothDelay;
        double most = dt * 0.25;
        _smoothDelay += drift > most ? most : drift < -most ? -most : drift;

        double play = now - _clockOffset - _smoothDelay;
        if (play >= _sampleTime2)
        {
            _value = _sample2;
            return;
        }
        if (play >= _sampleTime1)
        {
            double span = _sampleTime2 - _sampleTime1;
            _value = Interpolate(_sample1, _sample2, span > 1e-9 ? (float)((play - _sampleTime1) / span) : 1f);
            return;
        }
        if (_samples < 3 || play <= _sampleTime0)
        {
            _value = _samples < 3 ? _sample1 : _sample0;
            return;
        }
        double first = _sampleTime1 - _sampleTime0;
        _value = Interpolate(_sample0, _sample1, first > 1e-9 ? (float)((play - _sampleTime0) / first) : 1f);
    }

    public override void Decode(BinaryReader reader, StreamMessage message)
    {
        var enc = _encoding.Value;
        bool quantizable = StreamCoder.SupportsQuantization(typeof(T));
        T value;

        if (enc == ValueEncoding.Quantized && quantizable)
        {
            var br = BitScratch.Read(reader, StreamCoder.QuantizedByteCount<T>(_fullFrameBits.Value));
            value = StreamCoder.DecodeQuantized(br, _fullFrameMin.Value, _fullFrameMax.Value, _fullFrameBits.Value);
        }
        else if (enc == ValueEncoding.Delta && quantizable)
        {
            if (!TryDecodeDeltaFrame(reader, out value))
                return; // a delta arrived before any keyframe - skip it, keep the current value
        }
        else
        {
            value = SyncCoder.Decode<T>(reader);
        }

        WriteDataPoint(value, DateTime.FromBinary((long)(message.StreamTime * TimeSpan.TicksPerSecond)));
        _receivedFirstData = true;
    }

    public override void Encode(BinaryWriter writer)
    {
        var enc = _encoding.Value;
        bool quantizable = StreamCoder.SupportsQuantization(typeof(T));

        // Quantized when requested AND the type has a quantized coder; otherwise full precision. Both sides
        // know FullFrameBits (synced), so the quantized byte count is implied - no length prefix. -xlinka
        if (enc == ValueEncoding.Quantized && quantizable)
        {
            var bw = BitScratch.Writer();
            StreamCoder.EncodeQuantized(bw, _value, _fullFrameMin.Value, _fullFrameMax.Value, _fullFrameBits.Value);
            writer.Write(bw.Packed());
        }
        else if (enc == ValueEncoding.Delta && quantizable)
        {
            EncodeDeltaFrame(writer);
        }
        else
        {
            SyncCoder.Encode(writer, _value);
        }
    }

    // Delta frame: a 1-bit full/delta flag then the bit-packed value (full = quantized absolute, delta =
    // quantized change from the last value). Length-prefixed because full and delta frames differ in size.
    // A full keyframe goes out every KeyframeInterval frames (and on the first), so a lost delta's drift is
    // bounded and recovers at the next keyframe. -xlinka
    private void EncodeDeltaFrame(BinaryWriter writer)
    {
        bool full = !_hasLastEncoded || (_keyframeCounter % KeyframeInterval) == 0;

        var bw = BitScratch.Writer();
        bw.WriteBits(full ? 1u : 0u, 1);
        if (full)
            StreamCoder.EncodeQuantized(bw, _value, _fullFrameMin.Value, _fullFrameMax.Value, _fullFrameBits.Value);
        else
            StreamCoder.EncodeDelta(bw, _value, _lastEncodedValue, _deltaFrameMin.Value, _deltaFrameMax.Value, _deltaFrameBits.Value);

        var bytes = bw.Packed();
        writer.Write7BitEncodedInt(bytes.Length);
        writer.Write(bytes);

        _lastEncodedValue = _value;
        _hasLastEncoded = true;
        _keyframeCounter++;
    }

    private bool TryDecodeDeltaFrame(BinaryReader reader, out T value)
    {
        value = default!;

        int length = reader.Read7BitEncodedInt();
        if (length <= 0)
            return false;

        var br = BitScratch.Read(reader, length);
        bool full = br.ReadBits(1) == 1u;

        if (full)
        {
            value = StreamCoder.DecodeQuantized(br, _fullFrameMin.Value, _fullFrameMax.Value, _fullFrameBits.Value);
        }
        else
        {
            if (!_hasLastDecoded)
                return false; // can't apply a delta without a base value yet

            value = StreamCoder.DecodeDelta(br, _lastDecodedValue, _deltaFrameMin.Value, _deltaFrameMax.Value, _deltaFrameBits.Value);
        }

        _lastDecodedValue = value;
        _hasLastDecoded = true;
        return true;
    }

    protected void WriteDataPoint(T value, DateTime time)
    {
        if (IsInterpolated)
        {
            if (_dataPoints.Count > 0 && time < _dataPoints[_dataPoints.Count - 1].Time)
                return; // Out of order, skip

            _dataPoints.Add(new DataPoint(value, time));

            if (_dataPoints.Count > 1)
            {
                _trailingTime = CurrentTime;
                _timeTransition = 0f;
                _leadingTime = Earlier(time, InterpolationOffset);
                if (_leadingTime < _trailingTime) _leadingTime = _trailingTime;
            }
            else
            {
                _value = value;
                _trailingTime = _leadingTime = time;
                _timeTransition = 1f;
            }
        }
        else if (SmoothsArrivals && !IsLocal)
        {
            KeepSample(value, time.Ticks / (double)TimeSpan.TicksPerSecond);
        }
        else
        {
            _value = value;
        }
    }

    protected virtual T Interpolate(T a, T b, float lerp)
    {
        return lerp < 0.5f ? a : b;
    }

    // A sample's time is the sender's seconds since its world began, so in a world's first moments it sits
    // closer to the start of the calendar than the interpolation offset, and stepping back by the offset
    // ran off it: every such sample threw, and the decode dropped the rest of its message. -xlinka
    private static DateTime Earlier(DateTime time, float seconds)
        => new DateTime(System.Math.Max(0L, time.Ticks - (long)(seconds * TimeSpan.TicksPerSecond)));

    private static DateTime Lerp(DateTime a, DateTime b, float t)
    {
        long ticksA = a.Ticks;
        long ticksB = b.Ticks;
        long result = ticksA + (long)((ticksB - ticksA) * t);
        return new DateTime(result);
    }

    private static float InverseLerp(DateTime a, DateTime b, DateTime value)
    {
        long range = b.Ticks - a.Ticks;
        if (range == 0) return 0f;
        return (float)(value.Ticks - a.Ticks) / range;
    }

    private static float Progress01(float current, float delta)
    {
        return System.Math.Min(1f, current + delta);
    }

    public override void Dispose()
    {
        _dataPoints.Clear();
        Changed = null!;
        base.Dispose();
    }
}

public class Float3ValueStream : ValueStream<Lumora.Core.Math.float3>
{
    protected override bool SmoothsArrivals => true;

    public override bool HasValidData
    {
        get
        {
            if (!base.HasValidData)
                return false;

            var v = _value;
            return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        }
    }

    protected override Lumora.Core.Math.float3 Interpolate(
        Lumora.Core.Math.float3 a,
        Lumora.Core.Math.float3 b,
        float lerp)
    {
        return new Lumora.Core.Math.float3(
            a.x + (b.x - a.x) * lerp,
            a.y + (b.y - a.y) * lerp,
            a.z + (b.z - a.z) * lerp
        );
    }

}

public class FloatQValueStream : ValueStream<Lumora.Core.Math.floatQ>
{
    protected override bool SmoothsArrivals => true;

    // A zero quaternion would NaN when normalized.
    public override bool HasValidData
    {
        get
        {
            if (!base.HasValidData)
                return false;

            var q = _value;
            float lengthSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return lengthSq > 0.0001f;
        }
    }

    protected override Lumora.Core.Math.floatQ Interpolate(
        Lumora.Core.Math.floatQ a,
        Lumora.Core.Math.floatQ b,
        float lerp)
    {
        // Proper spherical interpolation - constant angular velocity, no normalize-induced speed-up. -xlinka
        return Lumora.Core.Math.floatQ.Slerp(a, b, lerp);
    }
}

// Concrete subtype so it resolves by name on decode; the generic isn't directly instantiable over
// the wire.
public class BoolValueStream : ValueStream<bool>
{
}
