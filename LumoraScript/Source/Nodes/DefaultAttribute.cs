// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// An input's default where a C# default cannot say it: a vector, a colour or a rotation. The numbers are the
// value's lanes in order, as a graph writes it; one number fills every lane, so [Default(1)] is one on a
// float3 and white on a colour, and a rotation spells all four. It wins over a C# default, which a parameter
// may still need so the ones after it stay optional. The registry checks it against the port by the same
// rules as a saved value, except that a vector's float lanes may also be infinities, as an empty box's are.
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class DefaultAttribute : Attribute
{
    public double[] Lanes { get; }

    public DefaultAttribute(params double[] lanes)
    {
        Lanes = lanes ?? Array.Empty<double>();
    }
}
