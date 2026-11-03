// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// 10 to the power of decibels over 20: 0 dB is 1, -6 about 0.5, minus infinity 0.
[Node("Audio/Signal Processing/Decibels To Amplitude", Description = "10 to the power of decibels over 20: 0 dB is 1, -6 about 0.5, minus infinity 0.",
    Keywords = "db, decibel, gain, db to linear, db to volume")]
public sealed class DecibelsToAmplitude : ValueNode
{
    [return: Port("amplitude")]
    public static float Run(float decibels) => MathF.Pow(10f, decibels / 20f);
}
