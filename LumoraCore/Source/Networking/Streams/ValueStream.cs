using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Value encoding mode for streams.
/// </summary>
public enum ValueEncoding
{
    /// <summary>
    /// Full precision encoding.
    /// </summary>
    Full,

    /// <summary>
    /// Quantized encoding with bit-packing.
    /// </summary>
    Quantized
}

/// <summary>
/// Generic value stream with optional interpolation support.
/// Used for streaming transforms, tracking data, and other continuous values.
/// </summary>
/// <typeparam name="T">The type of value to stream.</typeparam>
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

    protected T _value;

    // Sync members for configuration
    protected readonly Sync<bool> _isInterpolated = new();
    protected readonly Sync<float> _interpolationOffset = new();
    protected readonly Sync<ValueEncoding> _encoding = new();

    // Interpolation state
    private List<DataPoint> _dataPoints = new();
    private float _timeTransition;
    private DateTime _trailingTime;
    private DateTime _leadingTime;

    private bool _receivedFirstData;

    /// <summary>
    /// Value encoding mode.
    /// </summary>
    public ValueEncoding Encoding
    {
        get => _encoding.Value;
        set
        {
            CheckOwnership();
            // Only full encoding supported currently
            if (value != ValueEncoding.Full)
                throw new NotSupportedException("Only Full encoding is currently supported");
            _encoding.Value = value;
        }
    }

    /// <summary>
    /// Whether interpolation is enabled for smooth playback.
    /// </summary>
    public bool IsInterpolated
    {
        get => _isInterpolated.Value;
        set
        {
            CheckOwnership();
            _isInterpolated.Value = value;
        }
    }

    /// <summary>
    /// Time offset for interpolation in seconds.
    /// </summary>
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

    /// <summary>
    /// Whether this stream has valid data to read.
    /// </summary>
    public override bool HasValidData
    {
        get
        {
            if (!_receivedFirstData)
                return IsLocal;
            return true;
        }
    }

    /// <summary>
    /// Current value of the stream.
    /// </summary>
    public T Value
    {
        get => _value;
        set
        {
            CheckOwnership();
            _value = value;
        }
    }

    /// <summary>
    /// Event triggered when the value changes.
    /// </summary>
    public event Action<IChangeable> Changed;

    protected override void OnInit()
    {
        base.OnInit();

        // Initialize sync members
        _isInterpolated.Initialize(World, this);
        _interpolationOffset.Initialize(World, this);
        _encoding.Initialize(World, this);

        _isInterpolated.EndInitPhase();
        _interpolationOffset.EndInitPhase();
        _encoding.EndInitPhase();

        // Set defaults
        _interpolationOffset.Value = 0.05f;
        _encoding.Value = ValueEncoding.Full;
    }

    /// <summary>
    /// Enable interpolation with default settings.
    /// </summary>
    public void SetInterpolation()
    {
        CheckOwnership();
        _isInterpolated.Value = true;
        _interpolationOffset.Value = 0.05f;
    }

    /// <summary>
    /// Called every frame to update interpolation.
    /// </summary>
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

            // Advance time
            var delta = World?.LastDelta ?? 0.016f;
            _trailingTime = _trailingTime.AddSeconds(delta);
            _leadingTime = _leadingTime.AddSeconds(delta);

            var latestTime = _dataPoints[_dataPoints.Count - 1].Time;
            if (_trailingTime > latestTime) _trailingTime = latestTime;
            if (_leadingTime > latestTime) _leadingTime = latestTime;

            _timeTransition = Progress01(_timeTransition, delta / InterpolationOffset);

            var currentTime = CurrentTime;
            int lastIndex = _dataPoints.FindLastIndex(p => currentTime >= p.Time);

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

        Changed?.Invoke(this);
    }

    /// <summary>
    /// Decode stream data from the reader.
    /// </summary>
    public override void Decode(BinaryReader reader, StreamMessage message)
    {
        T value = SyncCoder.Decode<T>(reader);
        WriteDataPoint(value, DateTime.FromBinary((long)(message.StreamTime * TimeSpan.TicksPerSecond)));
        _receivedFirstData = true;
    }

    /// <summary>
    /// Encode stream data to the writer.
    /// </summary>
    public override void Encode(BinaryWriter writer)
    {
        SyncCoder.Encode(writer, _value);
    }

    /// <summary>
    /// Write a data point for interpolation.
    /// </summary>
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
                _leadingTime = time.AddSeconds(-InterpolationOffset);
                if (_leadingTime < _trailingTime) _leadingTime = _trailingTime;
            }
            else
            {
                _trailingTime = _leadingTime = time;
                _timeTransition = 1f;
            }
        }
        else
        {
            _value = value;
        }
    }

    /// <summary>
    /// Interpolate between two values.
    /// Override for custom interpolation behavior.
    /// </summary>
    protected virtual T Interpolate(T a, T b, float lerp)
    {
        // Default: no interpolation, just return target
        return lerp < 0.5f ? a : b;
    }

    // Helper methods
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
        Changed = null;
        base.Dispose();
    }
}

/// <summary>
/// Float3 value stream with linear interpolation.
/// </summary>
public class Float3ValueStream : ValueStream<Lumora.Core.Math.float3>
{
    /// <summary>
    /// Override to check for valid float3 (no NaN or Infinity values).
    /// </summary>
    public override bool HasValidData
    {
        get
        {
            if (!base.HasValidData)
                return false;

            // Check for NaN or Infinity
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

/// <summary>
/// Quaternion value stream with spherical interpolation.
/// </summary>
public class FloatQValueStream : ValueStream<Lumora.Core.Math.floatQ>
{
    /// <summary>
    /// Override to check for valid quaternion (non-zero length).
    /// A zero quaternion is invalid and would cause NaN when normalized.
    /// </summary>
    public override bool HasValidData
    {
        get
        {
            if (!base.HasValidData)
                return false;

            // Check if the quaternion has valid (non-zero) length
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
        // Simple linear interpolation (slerp would be better but requires more math)
        return new Lumora.Core.Math.floatQ(
            a.x + (b.x - a.x) * lerp,
            a.y + (b.y - a.y) * lerp,
            a.z + (b.z - a.z) * lerp,
            a.w + (b.w - a.w) * lerp
        ).Normalized;
    }
}
