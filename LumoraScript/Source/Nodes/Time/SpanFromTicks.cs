// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// A span of so many 100-nanosecond ticks.
[Node("Time/Spans/From Ticks", Description = "A span of so many 100-nanosecond ticks.")]
public sealed class SpanFromTicks : ValueNode
{
    [return: Port("span")]
    public static TimeSpan Run(long ticks) => new(ticks);
}
