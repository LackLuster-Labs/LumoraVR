// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A value in one range carried to the same place in another, unclamped. Both ranges default to 0 to 1.
[Node("Math/Interpolation/Remap", Description = "A value in one range carried to the same place in another, unclamped.",
    Keywords = "map range, rescale, range")]
public sealed class Remap : ValueNode
{
    public static float Run(float value, float fromMin = 0f, float fromMax = 1f, float toMin = 0f, float toMax = 1f)
        => fromMin == fromMax ? toMin : toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin);
    public static double Run(double value, double fromMin = 0d, double fromMax = 1d, double toMin = 0d, double toMax = 1d)
        => fromMin == fromMax ? toMin : toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin);
}
