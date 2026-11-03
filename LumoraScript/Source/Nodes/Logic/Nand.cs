// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// An input left unwired is true, or every bit set, as it is for And, so Nand of one wired input is its
// Not. -xlinka
[Node("Logic/Nand", Description = "Inverts the AND result. For booleans, the result is false only when both inputs are true.",
    Keywords = "not and")]
public sealed class Nand : ValueNode
{
    public static bool Run(bool a = true, bool b = true) => !(a & b);
    public static bool2 Run([Default(1)] bool2 a, [Default(1)] bool2 b) => !(a & b);
    public static bool3 Run([Default(1)] bool3 a, [Default(1)] bool3 b) => !(a & b);
    public static bool4 Run([Default(1)] bool4 a, [Default(1)] bool4 b) => !(a & b);
    public static int Run(int a = -1, int b = -1) => ~(a & b);
    public static long Run(long a = -1L, long b = -1L) => ~(a & b);
    public static uint Run(uint a = uint.MaxValue, uint b = uint.MaxValue) => ~(a & b);
    public static ulong Run(ulong a = ulong.MaxValue, ulong b = ulong.MaxValue) => ~(a & b);
}
