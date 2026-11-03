// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The same date marked UTC with 1, local with 2, and neither with 0 or any other number. Nothing is
// converted: To Universal Time and To Local Time move a date between zones.
[Node("Time/Dates/Set Date Kind", Description = "The same date marked UTC with 1, local with 2, and neither with 0 or any other number.")]
public sealed class SetDateKind : ValueNode
{
    public static DateTime Run(DateTime date, int kind)
        => DateTime.SpecifyKind(date, kind is 1 or 2 ? (DateTimeKind)kind : DateTimeKind.Unspecified);
}
