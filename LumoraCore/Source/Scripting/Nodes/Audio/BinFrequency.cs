// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The frequency in hertz a bin of an FFT stands for: bin times rate over length, length being how many
// samples went in. A length of 0 or less is 0 Hz.
[Node("Audio/Signal Processing/Bin Frequency", Description = "The frequency in hertz a bin of an FFT stands for: bin times rate over length, length being how many samples went in.")]
public sealed class BinFrequency : ValueNode
{
    [return: Port("hz")]
    public static float Run(int bin, int length = 1024, float rate = 48000f) => length > 0 ? bin * rate / length : 0f;
}
