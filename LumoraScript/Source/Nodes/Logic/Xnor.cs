// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Logic/Xnor", Description = "Inverts the XOR result. For booleans, the result is true when both inputs match.",
    Keywords = "not xor, equivalent, both or neither, same")]
public sealed class Xnor : ValueNode
{
    public static bool Run(bool a, bool b) => !(a ^ b);
    public static bool2 Run(bool2 a, bool2 b) => !(a ^ b);
    public static bool3 Run(bool3 a, bool3 b) => !(a ^ b);
    public static bool4 Run(bool4 a, bool4 b) => !(a ^ b);
    public static int Run(int a, int b) => ~(a ^ b);
    public static long Run(long a, long b) => ~(a ^ b);
    public static uint Run(uint a, uint b) => ~(a ^ b);
    public static ulong Run(ulong a, ulong b) => ~(a ^ b);
}
