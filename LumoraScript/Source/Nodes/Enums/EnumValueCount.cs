// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// How many values the enum has, two names for one number counted once: the steps Next Value takes to
// come round.
[Node("Data/Enums/Value Count", Description = "How many values the enum has, two names for one number counted once: the steps Next Value takes to come round.",
    Keywords = "enum count, number of values, enum length")]
public sealed class EnumValueCount<T> : ValueNode where T : struct, Enum
{
    [return: Port("count")]
    public static int Run() => EnumSteps<T>.Values.Length;
}
