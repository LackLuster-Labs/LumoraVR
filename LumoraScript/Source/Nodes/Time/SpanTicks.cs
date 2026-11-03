// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The span in 100-nanosecond ticks; negative for a negative span.
[Node("Time/Spans/Span Ticks", Description = "The span in 100-nanosecond ticks; negative for a negative span.")]
public sealed class SpanTicks : ValueNode
{
    [return: Port("ticks")]
    public static long Run(TimeSpan span) => span.Ticks;
}
