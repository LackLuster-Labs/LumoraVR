// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Input;

// Device-agnostic: each frame a hook folds its tracker's own shape set down to these. MouthStreamManager
// replicates them and MouthExpressionDriver maps them onto avatar blendshapes.
public enum MouthShape
{
    JawOpen,
    JawLeft,
    JawRight,
    JawForward,
    MouthPucker,
    MouthWide,
    SmileLeft,
    SmileRight,
    FrownLeft,
    FrownRight,
    UpperUp,
    LowerDown,
    CheekPuff,
    TongueOut,

    // The rest of what a lip tracker measures, kept apart where the set above averages a pair or has no channel.
    // A tracker that has no reading for one leaves it at 0. Indexed by ordinal in the mouth stream: append only.
    UpperUpLeft,
    UpperUpRight,
    LowerDownLeft,
    LowerDownRight,
    CheekPuffLeft,
    CheekPuffRight,
    CheekSuck,
    // The upper or lower lip shifted toward the wearer's left or right.
    UpperLipLeft,
    UpperLipRight,
    LowerLipLeft,
    LowerLipRight,
    UpperLipOverturn,
    LowerLipOverturn,
    UpperLipInside,
    LowerLipInside,
    LowerLipOverlay,
    // TongueOut is the first step out, this the rest of the way.
    TongueExtend,
    TongueUp,
    TongueDown,
    TongueLeft,
    TongueRight,
    TongueRoll,
    TongueUpLeft,
    TongueUpRight,
    TongueDownLeft,
    TongueDownRight
}

// The hardware hook sets IsTracking and the weights. Until one does it is untracked and all zero, so the
// mouth rests.
public class MouthDevice : InputDevice
{
    public static readonly int ShapeCount = Enum.GetValues(typeof(MouthShape)).Length;

    private readonly float[] _weights = new float[ShapeCount];

    public bool IsTracking { get; set; }

    public bool IsWorn { get; set; } = true;

    public float TrackingConfidence { get; set; } = 1f;

    public IReadOnlyList<float> Weights => _weights;

    public float GetWeight(MouthShape shape)
    {
        int i = (int)shape;
        return (uint)i < (uint)_weights.Length ? _weights[i] : 0f;
    }

    public void SetWeight(MouthShape shape, float weight)
    {
        int i = (int)shape;
        if ((uint)i < (uint)_weights.Length)
            _weights[i] = weight;
    }

    public void ClearWeights() => Array.Clear(_weights, 0, _weights.Length);
}
