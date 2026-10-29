// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Math;
using Lumora.Script;
using Lumora.Warden;

namespace Lumora.Core.Scripting;

// Tweens scripts started. Each one writes its field once a frame, as the script's author and judged like Set
// Field, from where it began to where it ends along its curve, and answers done once it is there, with the
// end value written exactly. One field runs one tween: a new one on it stops the one running, which answers
// stopped. A tween also stops, answering stopped, when its field goes or a write is refused, and quietly when
// its script stops for good. It carries on through a live graph swap and answers the new graph. Time is the
// world's clock, so a world held back in the background moves its tweens slowly too. -xlinka
internal static class ScriptTweens
{
    private static readonly ConditionalWeakTable<World, Dictionary<IField, Tween>> _running = new();

    public static bool CanTween(Type type)
        => type == typeof(float) || type == typeof(double) || type == typeof(float2) || type == typeof(float3) || type == typeof(float4)
            || type == typeof(floatQ) || type == typeof(color) || type == typeof(colorHDR) || type == typeof(int) || type == typeof(long)
            || type == typeof(int2) || type == typeof(int3) || type == typeof(int4);

    // A rotation made unit length, false for one of no length or not finite: an unwired one is all zeros, and
    // written as the end of the way it left a slot with no shape. Anything else is usable as it is. -xlinka
    public static bool Usable<T>(ref T value)
    {
        if (value is not floatQ rotation)
            return true;
        float lengthSquared = floatQ.Dot(rotation, rotation);
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
            return false;
        value = (T)(object)ScriptWrites.Unit(rotation);
        return true;
    }

    // How far apart two values are, for a duration given per unit: a length for numbers, vectors and colours,
    // degrees for rotations.
    public static float Distance<T>(T a, T b) => (a, b) switch
    {
        (float x, float y) => MathF.Abs(y - x),
        (double x, double y) => (float)System.Math.Abs(y - x),
        (float2 x, float2 y) => (y - x).Length,
        (float3 x, float3 y) => (y - x).Length,
        (float4 x, float4 y) => (y - x).Length,
        (floatQ x, floatQ y) => Degrees(x, y),
        (color x, color y) => new float4(y.r - x.r, y.g - x.g, y.b - x.b, y.a - x.a).Length,
        (colorHDR x, colorHDR y) => new float4(y.r - x.r, y.g - x.g, y.b - x.b, y.a - x.a).Length,
        (int x, int y) => (float)System.Math.Abs((double)y - x),
        (long x, long y) => (float)System.Math.Abs((double)y - x),
        (int2 x, int2 y) => new float2(y.x - x.x, y.y - x.y).Length,
        (int3 x, int3 y) => new float3(y.x - x.x, y.y - x.y, y.z - x.z).Length,
        (int4 x, int4 y) => new float4(y.x - x.x, y.y - x.y, y.z - x.z, y.w - x.w).Length,
        _ => 0f,
    };

    // Runs the first step at once, so the field is on its way in the same frame.
    public static void Start<T>(ScriptContext ctx, AwaitTicket<TweenEnded> ticket, IField<T> field, T from, T to, float seconds,
        EaseCurve curve, EaseDirection direction)
    {
        var running = _running.GetValue(ctx.World, static _ => new Dictionary<IField, Tween>());
        if (running.TryGetValue(field, out var earlier))
            earlier.Finish("stopped");
        var tween = new Tween<T>(ctx, ticket, field, from, to, float.IsFinite(seconds) && seconds > 0f ? seconds : 0f, curve, direction, running);
        running[field] = tween;
        tween.Step();
    }

    private static float Degrees(floatQ a, floatQ b)
    {
        float lengths = MathF.Sqrt(floatQ.Dot(a, a) * floatQ.Dot(b, b));
        if (!(lengths > 0f))
            return 0f;
        float dot = MathF.Min(1f, MathF.Abs(floatQ.Dot(a, b)) / lengths);
        return 2f * MathF.Acos(dot) * (180f / MathF.PI);
    }

    private static T At<T>(T a, T b, float s)
    {
        object value = (a, b) switch
        {
            (float x, float y) => x + (y - x) * s,
            (double x, double y) => x + (y - x) * s,
            (float2 x, float2 y) => float2.Lerp(x, y, s),
            (float3 x, float3 y) => float3.Lerp(x, y, s),
            (float4 x, float4 y) => float4.Lerp(x, y, s),
            (floatQ x, floatQ y) => floatQ.Slerp(x, y, s),
            (color x, color y) => color.Lerp(x, y, s),
            (colorHDR x, colorHDR y) => colorHDR.Lerp(x, y, s),
            (int x, int y) => Whole(x, y, s),
            (long x, long y) => WholeLong(x, y, s),
            (int2 x, int2 y) => new int2(Whole(x.x, y.x, s), Whole(x.y, y.y, s)),
            (int3 x, int3 y) => new int3(Whole(x.x, y.x, s), Whole(x.y, y.y, s), Whole(x.z, y.z, s)),
            (int4 x, int4 y) => new int4(Whole(x.x, y.x, s), Whole(x.y, y.y, s), Whole(x.z, y.z, s), Whole(x.w, y.w, s)),
            _ => b!,
        };
        return (T)value;
    }

    // Rounded to the nearest whole value, held inside the type when a curve overshoots past its range.
    private static int Whole(int a, int b, float s)
        => (int)System.Math.Clamp(System.Math.Round(a + ((double)b - a) * s), int.MinValue, int.MaxValue);

    private static long WholeLong(long a, long b, float s)
    {
        double value = System.Math.Round(a + ((double)b - a) * s);
        return value >= long.MaxValue ? long.MaxValue : value <= long.MinValue ? long.MinValue : (long)value;
    }

    private abstract class Tween
    {
        public abstract void Finish(string? exit);
    }

    private sealed class Tween<T> : Tween
    {
        private readonly ScriptContext _ctx;
        private readonly AwaitTicket<TweenEnded> _ticket;
        private readonly IField<T> _field;
        private readonly T _from;
        private readonly T _to;
        private readonly float _seconds;
        private readonly EaseCurve _curve;
        private readonly EaseDirection _direction;
        private readonly Dictionary<IField, Tween> _running;
        private readonly double _start;
        private readonly Action _step;
        private float _progress;
        private bool _finished;

        public Tween(ScriptContext ctx, AwaitTicket<TweenEnded> ticket, IField<T> field, T from, T to, float seconds, EaseCurve curve,
            EaseDirection direction, Dictionary<IField, Tween> running)
        {
            _ctx = ctx;
            _ticket = ticket;
            _field = field;
            _from = from;
            _to = to;
            _seconds = seconds;
            _curve = curve;
            _direction = direction;
            _running = running;
            _start = ctx.World.Time.TotalTime;
            _step = Step;
        }

        public void Step()
        {
            if (_finished)
                return;
            var world = _ctx.World;
            if (_ticket.Cancelled.IsCancellationRequested || world.IsDisposed || world.IsDestroyed || !_ctx.Principal.IsStillAuthorIn(world))
            {
                Finish(null);
                return;
            }
            if (_field.IsDestroyed)
            {
                Finish("stopped");
                return;
            }
            // Never write as the author inside a scope that opens the gate; the next frame will do.
            if (PermissionEngine.IsGateOpenOnThisThread || WorldConfigEdit.IsOpen)
            {
                world.RunOnNextUpdate(_step);
                return;
            }

            double elapsed = world.Time.TotalTime - _start;
            float t = _seconds <= 0f ? 1f : (float)System.Math.Clamp(elapsed / _seconds, 0.0, 1.0);
            var value = t >= 1f ? _to : At(_from, _to, (float)Ease.Run((double)t, _curve, _direction));
            try
            {
                using (_ctx.EnterWriter())
                    ScriptWrites.Write(_ctx, _field, value);
            }
            catch (UnauthorizedAccessException)
            {
                Finish("stopped");
                return;
            }
            _progress = t;
            if (t >= 1f)
            {
                Finish("done");
                return;
            }
            world.RunOnNextUpdate(_step);
        }

        public override void Finish(string? exit)
        {
            if (_finished)
                return;
            _finished = true;
            if (_running.TryGetValue(_field, out var current) && ReferenceEquals(current, this))
                _running.Remove(_field);
            if (exit == null)
                _ticket.Abandon();
            else
                _ticket.Answer(exit, new TweenEnded(_progress));
        }
    }
}
