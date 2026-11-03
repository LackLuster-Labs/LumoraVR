// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// 1 for a UTC date, 2 for a local one, 0 for one marked as neither.
[Node("Time/Dates/Date Kind", Description = "1 for a UTC date, 2 for a local one, 0 for one marked as neither.")]
public sealed class DateKind : ValueNode
{
    [return: Port("kind")]
    public static int Run(DateTime date) => (int)date.Kind;
}
