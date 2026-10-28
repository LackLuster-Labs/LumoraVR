// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;

namespace Lumora.Core.Assets.Animation;

// Keyframed track that blends between keys: the workhorse for transforms, blend-shape weights and
// anything else with a meaningful midpoint.
//
// Interpolation is stored per key so one track can hold a stepped section next to a smooth one, but
// the common case (every key the same mode) costs one byte for the whole track instead of one byte
// per key - the per-key array only materializes when a key actually disagrees. Same trick for
// tangents: no cubic keys, no tangent arrays. On a skeletal clip that is thousands of keys per bone
// across dozens of bones, so it is worth the branch. -xlinka
public sealed class CurveAnimationTrack<T> : AnimationTrackBase, IAnimationTrack<T>
{
    private static readonly IAnimationValueCodec<T> Codec =
        AnimationCodecs.Get<T>() ?? throw new NotSupportedException($"No animation codec for {typeof(T)}");

    private T[] _values = Array.Empty<T>();

    // Null while every key shares _sharedInterpolation.
    private KeyframeInterpolation[]? _interpolations;
    private KeyframeInterpolation _sharedInterpolation = KeyframeInterpolation.Linear;

    // Null until a cubic key is added. Value-per-second, in the track's own units.
    private T[]? _inTangents;
    private T[]? _outTangents;

    public override AnimationTrackType TrackType => AnimationTrackType.Curve;

    public override Type ValueType => typeof(T);

    public override AnimationElementType ElementType => Codec.ElementType;

    public bool HasTangents => _outTangents != null;

    public T KeyValue(int index) => _values[index];

    public KeyframeInterpolation KeyInterpolation(int index)
        => _interpolations != null ? _interpolations[index] : _sharedInterpolation;

    // The type default on a track that has never held a cubic key.
    public T KeyInTangent(int index) => _inTangents != null ? _inTangents[index] : default!;

    public T KeyOutTangent(int index) => _outTangents != null ? _outTangents[index] : default!;

    // Keys may arrive in any order; they are kept sorted by time. A type with no meaningful midpoint
    // (bool, string) is forced to Step regardless of what is asked for, so the file can never claim
    // an interpolation the sampler would have to ignore.
    public void AddKey(float time, T value,
        KeyframeInterpolation interpolation = KeyframeInterpolation.Linear,
        T inTangent = default!, T outTangent = default!)
    {
        if (!float.IsFinite(time))
        {
            throw new ArgumentOutOfRangeException(nameof(time), "Keyframe time must be finite");
        }
        if (time < 0f)
        {
            time = 0f;
        }
        if (!Codec.IsInterpolable)
        {
            interpolation = KeyframeInterpolation.Step;
        }

        int index = InsertIndexFor(time);

        // Materialize the per-key arrays lazily, the first time a key disagrees with the shared state.
        if (_interpolations == null && interpolation != _sharedInterpolation)
        {
            if (_count == 0)
            {
                _sharedInterpolation = interpolation;
            }
            else
            {
                _interpolations = new KeyframeInterpolation[System.Math.Max(8, _count + 1)];
                for (int i = 0; i < _count; i++)
                {
                    _interpolations[i] = _sharedInterpolation;
                }
            }
        }
        if (_outTangents == null && interpolation.RequiresTangents())
        {
            _inTangents = new T[System.Math.Max(8, _count + 1)];
            _outTangents = new T[System.Math.Max(8, _count + 1)];
        }

        InsertInto(ref _times, _count, index, time);
        InsertInto(ref _values, _count, index, value);
        if (_interpolations != null)
        {
            InsertInto(ref _interpolations, _count, index, interpolation);
        }
        if (_outTangents != null)
        {
            var inArray = _inTangents!;
            InsertInto(ref inArray, _count, index, inTangent);
            _inTangents = inArray;
            var outArray = _outTangents;
            InsertInto(ref outArray, _count, index, outTangent);
            _outTangents = outArray;
        }

        _count++;
        ResetCursor();
    }

    public bool Evaluate(float time, out T value)
    {
        if (_count == 0)
        {
            value = default!;
            return false;
        }

        int i = FindKey(time);
        if (i < 0)
        {
            // Before the first key: hold the first value rather than extrapolating backwards.
            value = _values[0];
            return true;
        }
        if (i >= _count - 1)
        {
            value = _values[_count - 1];
            return true;
        }

        var mode = _interpolations != null ? _interpolations[i] : _sharedInterpolation;
        if (mode == KeyframeInterpolation.Step)
        {
            value = _values[i];
            return true;
        }

        float t0 = _times[i];
        float t1 = _times[i + 1];
        float span = t1 - t0;
        if (span <= 0f)
        {
            // Two keys at the same instant: a step, not a divide by zero.
            value = _values[i + 1];
            return true;
        }

        float u = (time - t0) / span;
        u = u < 0f ? 0f : (u > 1f ? 1f : u);

        if (mode == KeyframeInterpolation.Cubic && _outTangents != null)
        {
            value = Codec.Hermite(_values[i], _outTangents[i], _values[i + 1], _inTangents![i + 1], u, span);
            return true;
        }

        if (mode.IsEased())
            u = mode.Ease(u);
        value = Codec.Lerp(_values[i], _values[i + 1], u);
        return true;
    }

    public override object? EvaluateBoxed(float time) => Evaluate(time, out var v) ? v : null;

    internal override void EncodeBody(BinaryWriter bw)
    {
        bw.Write7BitEncodedInt(_count);

        byte flags = 0;
        if (_interpolations != null)
        {
            flags |= 1;
        }
        if (_outTangents != null)
        {
            flags |= 2;
        }
        bw.Write(flags);

        if (_interpolations != null)
        {
            for (int i = 0; i < _count; i++)
            {
                bw.Write((byte)_interpolations[i]);
            }
        }
        else
        {
            bw.Write((byte)_sharedInterpolation);
        }

        for (int i = 0; i < _count; i++)
        {
            bw.Write(_times[i]);
            Codec.Write(bw, _values[i]);
        }

        if (_outTangents != null)
        {
            for (int i = 0; i < _count; i++)
            {
                Codec.Write(bw, _inTangents![i]);
                Codec.Write(bw, _outTangents[i]);
            }
        }
    }

    internal override void DecodeBody(BinaryReader br)
    {
        int count = br.Read7BitEncodedInt();
        if (count < 0)
        {
            throw new InvalidDataException($"Negative keyframe count ({count})");
        }

        byte flags = br.ReadByte();
        bool perKeyInterpolation = (flags & 1) != 0;
        bool hasTangents = (flags & 2) != 0;

        if (perKeyInterpolation)
        {
            _interpolations = new KeyframeInterpolation[count];
            for (int i = 0; i < count; i++)
            {
                _interpolations[i] = (KeyframeInterpolation)br.ReadByte();
            }
        }
        else
        {
            _sharedInterpolation = (KeyframeInterpolation)br.ReadByte();
        }

        _times = new float[count];
        _values = new T[count];
        for (int i = 0; i < count; i++)
        {
            _times[i] = br.ReadSingle();
            _values[i] = Codec.Read(br);
        }

        if (hasTangents)
        {
            _inTangents = new T[count];
            _outTangents = new T[count];
            for (int i = 0; i < count; i++)
            {
                _inTangents[i] = Codec.Read(br);
                _outTangents[i] = Codec.Read(br);
            }
        }

        _count = count;
        ResetCursor();
    }
}
