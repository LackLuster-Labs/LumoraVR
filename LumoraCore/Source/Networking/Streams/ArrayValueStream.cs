// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Protocol;

namespace Lumora.Core.Networking.Streams;

// One stream instead of N single-value streams when many related values move as a unit (finger bones,
// blendshape weights, bone chains, ...).
// Count is synced so both ends agree on the element count without a per-frame
// length field. Quantized (bit-packed, length-prefixed because the packed size is
// implied by Count+bits but the reader is shared) or full precision; optional
// element-wise interpolation, deferred to concrete subtypes via InterpolateElement. -xlinka
public class ArrayValueStream<T> : ImplicitStream
{
    protected readonly Sync<int> _count = new();
    protected readonly Sync<bool> _isInterpolated = new();
    protected readonly Sync<float> _interpolationOffset = new();
    protected readonly Sync<ValueEncoding> _encoding = new();
    protected readonly Sync<int> _fullFrameBits = new();
    protected readonly Sync<T> _fullFrameMin = new();
    protected readonly Sync<T> _fullFrameMax = new();

    protected T[] _values = Array.Empty<T>();

    private struct DataPoint
    {
        public T[] Value;
        public DateTime Time;
        public DataPoint(T[] value, DateTime time) { Value = value; Time = time; }
    }

    private readonly List<DataPoint> _dataPoints = new();
    private float _timeTransition;
    private DateTime _trailingTime;
    private DateTime _leadingTime;
    private bool _receivedFirstData;

    public int Count
    {
        get => _count.Value;
        set { CheckOwnership(); _count.Value = value; EnsureSize(value); }
    }

    public ValueEncoding Encoding
    {
        get => _encoding.Value;
        set { CheckOwnership(); _encoding.Value = value; }
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

    public bool IsInterpolated
    {
        get => _isInterpolated.Value;
        set { CheckOwnership(); _isInterpolated.Value = value; }
    }

    public float InterpolationOffset
    {
        get => _interpolationOffset.Value;
        set { CheckOwnership(); _interpolationOffset.Value = value; }
    }

    public T this[int index]
    {
        get => (uint)index < (uint)_values.Length ? _values[index] : default!;
        set
        {
            CheckOwnership();
            EnsureSize(_count.Value);
            if ((uint)index < (uint)_values.Length)
                _values[index] = value;
        }
    }

    public override bool HasValidData => (_receivedFirstData && _values.Length == _count.Value) || IsLocal;

    private DateTime CurrentTime => Lerp(_trailingTime, _leadingTime, _timeTransition);

    protected override void OnInit()
    {
        base.OnInit();
        _interpolationOffset.Value = 0.05f;
        _encoding.Value = ValueEncoding.Full;
        _fullFrameBits.Value = 12;
    }

    public void SetInterpolation()
    {
        CheckOwnership();
        _isInterpolated.Value = true;
        _interpolationOffset.Value = 0.05f;
    }

    private void EnsureSize(int count)
    {
        if (count < 0)
            count = 0;
        if (_values.Length != count)
            Array.Resize(ref _values, count);
    }

    public override void Encode(BinaryWriter writer)
    {
        int count = _count.Value;
        EnsureSize(count);

        bool quantized = _encoding.Value == ValueEncoding.Quantized && StreamCoder.SupportsQuantization(typeof(T));
        if (quantized)
        {
            var bw = BitScratch.Writer();
            for (int i = 0; i < count; i++)
                StreamCoder.EncodeQuantized(bw, _values[i], _fullFrameMin.Value, _fullFrameMax.Value, _fullFrameBits.Value);
            var bytes = bw.Packed();
            writer.Write7BitEncodedInt(bytes.Length);
            writer.Write(bytes);
        }
        else
        {
            for (int i = 0; i < count; i++)
                SyncCoder.Encode(writer, _values[i]);
        }
    }

    public override void Decode(BinaryReader reader, StreamMessage message)
    {
        int count = _count.Value;

        bool quantized = _encoding.Value == ValueEncoding.Quantized && StreamCoder.SupportsQuantization(typeof(T));
        int length = 0;
        if (quantized)
        {
            length = reader.Read7BitEncodedInt();
            if (length <= 0)
                return;
        }
        var temp = TakeSample(count);
        if (quantized)
        {
            var br = BitScratch.Read(reader, length);
            for (int i = 0; i < count; i++)
                temp[i] = StreamCoder.DecodeQuantized(br, _fullFrameMin.Value, _fullFrameMax.Value, _fullFrameBits.Value);
        }
        else
        {
            for (int i = 0; i < count; i++)
                temp[i] = SyncCoder.Decode<T>(reader);
        }

        // HasValidData becomes true below. Publish the first sample immediately, so a tracking
        // flag decoded in this batch cannot expose zero quaternions before the next Update.
        if (!_receivedFirstData && IsInterpolated)
            _values = (T[])temp.Clone();

        if (IsInterpolated)
        {
            if (!WriteDataPoint(temp, DateTime.FromBinary((long)(message.StreamTime * TimeSpan.TicksPerSecond))))
                GiveBack(temp);
        }
        else
        {
            EnsureSize(count);
            Array.Copy(temp, _values, count);
            GiveBack(temp);
        }

        _receivedFirstData = true;
    }

    // Samples go back here once interpolation is past them and come out again for the next decode: a pose a
    // message for every avatar in the session made a new array each time. Decode and Update both run under the
    // data model lock. -xlinka
    private readonly Stack<T[]> _spareSamples = new();

    private T[] TakeSample(int count)
    {
        while (_spareSamples.Count > 0)
        {
            var spare = _spareSamples.Pop();
            if (spare.Length == count)
                return spare;
        }
        return new T[count];
    }

    private void GiveBack(T[] sample)
    {
        if (_spareSamples.Count < 4)
            _spareSamples.Push(sample);
    }

    public override void Update()
    {
        if (!IsInterpolated || IsLocal)
            return;

        if (_dataPoints.Count == 0)
            return;

        if (_dataPoints.Count == 1)
        {
            // InterpolateInto writes into _values. Do not alias the buffered sample or subsequent
            // frames will interpolate from a pose we have already modified.
            var sample = _dataPoints[0].Value;
            EnsureSize(sample.Length);
            Array.Copy(sample, _values, sample.Length);
            return;
        }

        var delta = World?.LastDelta ?? 0.016f;
        _trailingTime = _trailingTime.AddSeconds(delta);
        _leadingTime = _leadingTime.AddSeconds(delta);

        var latestTime = _dataPoints[_dataPoints.Count - 1].Time;
        if (_trailingTime > latestTime) _trailingTime = latestTime;
        if (_leadingTime > latestTime) _leadingTime = latestTime;

        _timeTransition = Progress01(_timeTransition, delta / System.Math.Max(InterpolationOffset, 1e-4f));

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
            int drop = System.Math.Min(lastIndex, _dataPoints.Count - 2);
            for (int i = 0; i < drop; i++)
                GiveBack(_dataPoints[i].Value);
            _dataPoints.RemoveRange(0, drop);
        }

        if (_dataPoints.Count >= 2)
        {
            float lerp = InverseLerp(_dataPoints[0].Time, _dataPoints[1].Time, currentTime);
            InterpolateInto(_dataPoints[0].Value, _dataPoints[1].Value, lerp);
        }
    }

    private bool WriteDataPoint(T[] value, DateTime time)
    {
        if (_dataPoints.Count > 0 && time < _dataPoints[_dataPoints.Count - 1].Time)
            return false; // out of order

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
            _trailingTime = _leadingTime = time;
            _timeTransition = 1f;
        }
        return true;
    }

    private void InterpolateInto(T[] a, T[] b, float lerp)
    {
        int count = System.Math.Min(a.Length, b.Length);
        EnsureSize(count);
        for (int i = 0; i < count; i++)
            _values[i] = InterpolateElement(a[i], b[i], lerp);
    }

    protected virtual T InterpolateElement(T a, T b, float lerp) => lerp < 0.5f ? a : b;

    // A sample's time is the sender's seconds since its world began, so in a world's first moments it sits
    // closer to the start of the calendar than the interpolation offset, and stepping back by the offset
    // ran off it: every such sample threw, and the decode dropped the rest of its message. -xlinka
    private static DateTime Earlier(DateTime time, float seconds)
        => new DateTime(System.Math.Max(0L, time.Ticks - (long)(seconds * TimeSpan.TicksPerSecond)));

    private static DateTime Lerp(DateTime a, DateTime b, float t)
    {
        long result = a.Ticks + (long)((b.Ticks - a.Ticks) * t);
        return new DateTime(result);
    }

    private static float InverseLerp(DateTime a, DateTime b, DateTime value)
    {
        long range = b.Ticks - a.Ticks;
        if (range == 0) return 0f;
        return (float)(value.Ticks - a.Ticks) / range;
    }

    private static float Progress01(float current, float delta) => System.Math.Min(1f, current + delta);

    public override void Dispose()
    {
        _dataPoints.Clear();
        base.Dispose();
    }
}

// A run of plain floats. Viseme weights are fifteen of them and want quantizing to a few bits each,
// which is what this exists for: at ten bits a frame of mouth shape is under twenty bytes. -xlinka
public class FloatArrayValueStream : ArrayValueStream<float>
{
    protected override float InterpolateElement(float a, float b, float lerp) => a + (b - a) * lerp;
}

public class Float3ArrayValueStream : ArrayValueStream<float3>
{
    protected override float3 InterpolateElement(float3 a, float3 b, float lerp)
        => new float3(a.x + (b.x - a.x) * lerp, a.y + (b.y - a.y) * lerp, a.z + (b.z - a.z) * lerp);
}

public class FloatQArrayValueStream : ArrayValueStream<floatQ>
{
    protected override floatQ InterpolateElement(floatQ a, floatQ b, float lerp)
        => floatQ.Slerp(a, b, lerp);
}
