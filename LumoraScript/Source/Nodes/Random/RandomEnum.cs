// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// One of the enum's values, each as likely as the others. Two names for one number are one value, so a
// value with a second name is not drawn twice as often.
[Node("Math/Random/Random Enum", Description = "One of the enum's values, each as likely as the others.",
    Keywords = "random choice, pick")]
public sealed class RandomEnum<T> : ReadNode where T : struct, Enum
{
    public static T Run(ScriptContextBase ctx)
    {
        var values = EnumSteps<T>.Values;
        return values.Length == 0 ? default : values[ctx.Random.Next(values.Length)];
    }
}
