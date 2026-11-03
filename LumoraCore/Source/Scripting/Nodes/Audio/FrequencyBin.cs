// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The FFT bin nearest a frequency, for length samples taken at rate: what to Get At in the bins, or where to
// Slice a band out of them. Kept between 0 and length / 2, the last bin there is; -1 for a length or rate of
// 0 or less, or a frequency that is not a number.
[Node("Audio/Signal Processing/Frequency Bin", Description = "The FFT bin nearest a frequency, for length samples taken at rate: what to Get At in the bins, or where to Slice a band out of them.")]
public sealed class FrequencyBin : ValueNode
{
    [return: Port("bin")]
    public static int Run(float hz, int length = 1024, float rate = 48000f)
    {
        if (length <= 0 || !(rate > 0f) || !float.IsFinite(hz))
            return -1;
        double bin = System.Math.Round((double)hz * length / rate);
        return (int)System.Math.Clamp(bin, 0, length / 2);
    }
}
