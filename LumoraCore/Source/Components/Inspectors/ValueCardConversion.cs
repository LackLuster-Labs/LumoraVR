// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components;

// What a value card can carry, how it reads, and which fields take it. A field of the card's own type
// takes it as is. Past that only the moves with one obvious answer: number to number (rounded to the
// nearest whole, halves away from zero, when the field counts whole numbers; refused when it will not fit),
// float2/float3/float4 into each other (missing components 0, extra ones dropped), color and colorHDR into
// each other (clamped to 0..1 on the way down), and anything into a string as the text the card shows.
// Text never turns back into a number, and nothing else guesses. -xlinka
public static class ValueCardConversion
{
    public static readonly LocaleText NoFit = LocaleText.Keyed("Inspector.ValueCard.NoFit", "a {0} won't fit {1}");
    public static readonly LocaleText OutOfRange = LocaleText.Keyed("Inspector.ValueCard.OutOfRange", "too big for {0}");
    public static readonly LocaleText NotFinite = LocaleText.Keyed("Inspector.ValueCard.NotFinite", "not a finite number");
    public static readonly LocaleText Driven = LocaleText.Keyed("Inspector.ValueCard.Driven", "driven, can't set");
    public static readonly LocaleText Locked = LocaleText.Keyed("Inspector.ValueCard.Locked", "locked, can't set");
    public static readonly LocaleText ReadOnly = LocaleText.Keyed("Inspector.ValueCard.ReadOnly", "read only");
    public static readonly LocaleText NoPermission = LocaleText.Keyed("Inspector.ValueCard.NoPermission", "no permission");
    public static readonly LocaleText Refused = LocaleText.Keyed("Inspector.ValueCard.Refused", "not taken");
    public static readonly LocaleText Empty = LocaleText.Keyed("Inspector.ValueCard.Empty", "empty card");
    public static readonly LocaleText Gone = LocaleText.Keyed("Inspector.ValueCard.Gone", "field is gone");
    public static readonly LocaleText NotReference = LocaleText.Keyed("Inspector.ValueCard.NotReference", "it holds a value, not a reference");

    private const int MaxShown = 60;

    // A value a card can hold has to cross the wire and fit a Sync<T>, which is the set the save coder takes.
    // References have their own card.
    public static bool CanCarry(Type? type)
        => type != null
        && type != typeof(RefID)
        && Nullable.GetUnderlyingType(type) == null
        && DataTreeCoder.IsSupported(type);

    public static string TypeName(Type type)
        => type == typeof(Uri) ? "URL" : SyncMemberEditorBuilder.NiceTypeName(type);

    // The value as the inspector shows it: numbers to the same places the fields round to, a rotation as the
    // yaw, pitch and roll degrees its boxes hold, a color as its channel fields. A string into a string
    // field gets exactly this.
    public static string Display(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case string text:
                return text;
            case bool flag:
                return flag ? "True" : "False";
            case float f:
                return Number(f);
            case double d:
                return d.ToString("0.####", CultureInfo.InvariantCulture);
            case float2 v:
                return $"({Number(v.x)}, {Number(v.y)})";
            case float3 v:
                return $"({Number(v.x)}, {Number(v.y)}, {Number(v.z)})";
            case float4 v:
                return $"({Number(v.x)}, {Number(v.y)}, {Number(v.z)}, {Number(v.w)})";
            case int4 v:
                return $"({v.x}, {v.y}, {v.z}, {v.w})";
            case color c:
                return $"({Number(c.r)}, {Number(c.g)}, {Number(c.b)}, {Number(c.a)})";
            case colorHDR c:
                return $"({Number(c.r)}, {Number(c.g)}, {Number(c.b)}, {Number(c.a)})";
            case floatQ q:
            {
                var euler = q.ToEuler();
                return $"({Degrees(euler.y)}°, {Degrees(euler.x)}°, {Degrees(euler.z)}°)";
            }
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString() ?? "";
        }
    }

    // The line on the card: a string reads in quotes so an empty one still shows, and a long one is cut.
    public static string CardText(object? value, Type type)
    {
        string text = Display(value).Replace('\r', ' ').Replace('\n', ' ');
        if (type == typeof(string))
            text = $"\"{text}\"";
        if (text.Length > MaxShown)
            text = text[..(MaxShown - 1)] + "…";
        return text;
    }

    // The swatch color for a color card, or null for anything else.
    public static color? Swatch(object? value) => value switch
    {
        color c => c,
        colorHDR hdr => hdr.ToLDR(),
        _ => null,
    };

    // reason is empty when nothing about the pair lines up; set when the pair belongs together and this value
    // still does not go (too big for the field, not a finite number).
    public static bool TryConvert(object? value, Type from, Type to, out object? result, out LocaleText reason)
    {
        result = null;
        reason = LocaleText.Empty;
        if (from == to)
        {
            result = value;
            return true;
        }
        if (to == typeof(string))
        {
            result = Display(value);
            return true;
        }
        if (value == null)
            return false;
        if (IsNumber(from) && IsNumber(to))
            return TryNumber(value, from, to, out result, out reason);
        if (TryComponents(value, out var components) && TryVector(to, components, out result))
            return true;
        if (value is color ldr && to == typeof(colorHDR))
        {
            result = colorHDR.FromLDR(ldr);
            return true;
        }
        if (value is colorHDR hdr && to == typeof(color))
        {
            var clamped = hdr.ToLDR();
            result = new color(clamped.r, clamped.g, clamped.b, System.Math.Clamp(clamped.a, 0f, 1f));
            return true;
        }
        return false;
    }

    private static bool TryNumber(object value, Type from, Type to, out object? result, out LocaleText reason)
    {
        result = null;
        reason = LocaleText.Empty;
        try
        {
            if (IsWhole(to) && !IsWhole(from))
            {
                double source = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(source))
                {
                    reason = NotFinite;
                    return false;
                }
                result = Convert.ChangeType(System.Math.Round(source, MidpointRounding.AwayFromZero), to, CultureInfo.InvariantCulture);
                return true;
            }
            result = Convert.ChangeType(value, to, CultureInfo.InvariantCulture);
            // A double past float's range comes back infinite rather than throwing.
            if (result is float f && !float.IsFinite(f) && double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)))
            {
                result = null;
                reason = OutOfRange.WithArgs(TypeName(to));
                return false;
            }
            return true;
        }
        catch (OverflowException)
        {
            reason = OutOfRange.WithArgs(TypeName(to));
            return false;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException)
        {
            return false;
        }
    }

    private static bool TryComponents(object value, out float[] components)
    {
        switch (value)
        {
            case float2 v: components = new[] { v.x, v.y }; return true;
            case float3 v: components = new[] { v.x, v.y, v.z }; return true;
            case float4 v: components = new[] { v.x, v.y, v.z, v.w }; return true;
            default: components = Array.Empty<float>(); return false;
        }
    }

    private static bool TryVector(Type to, float[] c, out object? result)
    {
        float At(int i) => i < c.Length ? c[i] : 0f;
        result = to == typeof(float2) ? new float2(At(0), At(1))
            : to == typeof(float3) ? new float3(At(0), At(1), At(2))
            : to == typeof(float4) ? new float4(At(0), At(1), At(2), At(3))
            : null;
        return result != null;
    }

    private static bool IsNumber(Type type)
        => type == typeof(float) || type == typeof(double) || type == typeof(decimal) || IsWhole(type);

    private static bool IsWhole(Type type)
        => type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
        || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte);

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Degrees(float radians) => (radians * (180f / MathF.PI)).ToString("0.##", CultureInfo.InvariantCulture);
}
