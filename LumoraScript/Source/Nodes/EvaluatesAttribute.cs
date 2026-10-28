// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Says how a ReadNode's answer changes, when it changes less than every time it is asked: Fixed, Tree or
// Field. A Field read takes the field it reads as an input. See Evaluation.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EvaluatesAttribute : Attribute
{
    public Evaluation Evaluation { get; }

    public EvaluatesAttribute(Evaluation evaluation)
    {
        Evaluation = evaluation;
    }
}
