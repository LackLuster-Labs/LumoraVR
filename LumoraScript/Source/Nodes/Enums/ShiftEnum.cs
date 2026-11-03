// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Steps through the values in number order, wrapping round either end: 1 is Next Value, -1 Previous
// Value, and a step count past the number of values goes round again.
[Node("Data/Enums/Shift Enum", Description = "Steps through the values in number order, wrapping round either end: 1 is Next Value, -1 Previous Value, and a step count past the number of values goes round again.",
    Keywords = "cycle, step enum, offset enum")]
public sealed class ShiftEnum<T> : ValueNode where T : struct, Enum
{
    [return: Port("shifted")]
    public static T Run(T value, int steps = 1) => EnumSteps<T>.Shift(value, steps);
}
