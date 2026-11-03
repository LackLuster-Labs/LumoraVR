// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The enum value a number stands for. A number that names none of the values (for a flags enum, one
// with a bit no flag has) is not valid, and gives the enum's zero value rather than a value it does not
// have.
[Node("Data/Enums/Number To Enum", Description = "The enum value a number stands for.",
    Keywords = "cast, from number, integer")]
public sealed class NumberToEnum<T> : ValueNode where T : struct, Enum
{
    public static void Run(int number, out T value, out bool valid) => valid = EnumSteps<T>.TryFromInt(number, out value);
}
