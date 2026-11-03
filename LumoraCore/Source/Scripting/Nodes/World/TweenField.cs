// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves a field to "to" over duration seconds along a curve, from where it stands or from "from" with
// useFrom, and carries on through next at once. done runs when it arrives, stopped when it could not get
// there: the field went, a write was refused, or another tween took the field over. Every frame's write is
// the author's own, judged like Set Field, so a Shared script's tween syncs like any of its writes. perUnit
// makes duration the seconds for each unit of the way: a metre of a position, a degree of a rotation. A
// duration that is not a positive number goes straight to "to". Whole numbers move by rounding. A rotation
// of no length, the unset one included, stops it at once and is never written.
[Node("World/Fields/Tween Field", Capability.WorldWrite, Description = "Animates a field to a target value over a duration, reporting when it completes or is interrupted.",
    Keywords = "animate, lerp, smooth, ease, transition, interpolate over time")]
[Answers("done", "stopped")]
public sealed class TweenField<T> : AwaitNode<TweenEnded>
{
    public static bool IsValidGenericType => ScriptTweens.CanTween(typeof(T));

    public static void Start(ScriptContext ctx, AwaitTicket<TweenEnded> ticket, IField<T>? field, T to, float duration = 1f,
        EaseCurve curve = EaseCurve.Smooth, EaseDirection direction = EaseDirection.InOut, bool useFrom = false, T from = default!, bool perUnit = false)
    {
        if (field == null || field.IsDestroyed || !ScriptTweens.Usable(ref to) || (useFrom && !ScriptTweens.Usable(ref from)))
        {
            ticket.Answer("stopped", new TweenEnded(0f));
            return;
        }
        try
        {
            if (ScriptWrites.Member(field) is { } element)
                ScriptWrites.Check(ctx, element);
            else if (ctx.IsLocal || !ReferenceEquals(field.World, ctx.World))
                throw new UnauthorizedAccessException("scripts tween only fields of their own world they may write");
        }
        catch (UnauthorizedAccessException)
        {
            ticket.Answer("stopped", new TweenEnded(0f));
            throw;
        }
        var start = useFrom ? from : field.Value;
        float seconds = perUnit ? duration * ScriptTweens.Distance(start, to) : duration;
        ScriptTweens.Start(ctx, ticket, field, start, to, seconds, curve, direction);
    }
}
