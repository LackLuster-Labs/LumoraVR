// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Script;

// A value as text, the same on every machine: numbers invariant and exact, a struct's numbers joined by
// commas in lane order, a coded value by its codec, text as itself. Handles other than text have no text
// form. -xlinka
public static class ScriptTextForm
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static bool Has(ScriptType type) => !type.IsHandle || type == ScriptTypes.String;

    public static string Format(ScriptType type, object? value)
    {
        value ??= type.DefaultValue;
        switch (type.Kind)
        {
            case ScriptTypeKind.Bool: return value is true ? "true" : "false";
            case ScriptTypeKind.Int: return ((int)(value ?? 0)).ToString(Invariant);
            case ScriptTypeKind.Long: return ((long)(value ?? 0L)).ToString(Invariant);
            case ScriptTypeKind.Float: return ((float)(value ?? 0f)).ToString("R", Invariant);
            case ScriptTypeKind.Double: return ((double)(value ?? 0d)).ToString("R", Invariant);
            case ScriptTypeKind.Vector:
            {
                var parts = new string[type.Leaves.Length];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = Leaf(type.Leaves[i].Get(value!));
                return string.Join(",", parts);
            }
            case ScriptTypeKind.Coded:
                return type.Codec!.Format(value!);
            default:
                return value is ScriptBigInt big ? big.ToString() : value as string ?? string.Empty;
        }
    }

    public static bool TryParse(ScriptType type, string? text, out object? value)
    {
        value = null;
        text ??= string.Empty;
        switch (type.Kind)
        {
            case ScriptTypeKind.Bool:
                if (!TryBool(text.Trim(), out bool flag))
                    return false;
                value = flag;
                return true;
            case ScriptTypeKind.Int:
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, Invariant, out int whole))
                    return false;
                value = whole;
                return true;
            case ScriptTypeKind.Long:
                if (!long.TryParse(text.Trim(), NumberStyles.Integer, Invariant, out long wide))
                    return false;
                value = wide;
                return true;
            case ScriptTypeKind.Float:
                if (!float.TryParse(text.Trim(), NumberStyles.Float, Invariant, out float single))
                    return false;
                value = single;
                return true;
            case ScriptTypeKind.Double:
                if (!double.TryParse(text.Trim(), NumberStyles.Float, Invariant, out double precise))
                    return false;
                value = precise;
                return true;
            case ScriptTypeKind.Vector:
            {
                // "[1; 2]" and "(1, 2)" read as "1,2".
                string inner = text.Trim();
                if (inner.Length >= 2 && ((inner[0] == '[' && inner[^1] == ']') || (inner[0] == '(' && inner[^1] == ')')))
                    inner = inner[1..^1];
                var parts = inner.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != type.Leaves.Length)
                    return false;
                var boxed = Activator.CreateInstance(type.ClrType)!;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!TryLeaf(type.Leaves[i].ClrType, parts[i], out var lane))
                        return false;
                    type.Leaves[i].Set(boxed, lane);
                }
                value = boxed;
                return true;
            }
            case ScriptTypeKind.Coded:
                return type.Codec!.TryParse(text, out value);
            default:
                if (type != ScriptTypes.String)
                    return false;
                value = text;
                return true;
        }
    }

    // A struct's numbers in lane order, the form a document keeps it in: flags as 0 and 1.
    public static double[] Numbers(ScriptType type, object? value)
    {
        if (type.Kind != ScriptTypeKind.Vector)
            throw new ArgumentException($"{type.Name} is not a struct of numbers", nameof(type));
        value ??= type.DefaultValue!;
        var numbers = new double[type.Leaves.Length];
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = type.Leaves[i].Get(value) switch
            {
                float single => single,
                double precise => precise,
                int whole => whole,
                long wide => wide,
                bool flag => flag ? 1d : 0d,
                _ => 0d,
            };
        }
        return numbers;
    }

    public static bool TryBool(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "on":
                value = true;
                return true;
            case "false" or "0" or "no" or "off":
                value = false;
                return true;
        }
        value = false;
        return false;
    }

    private static string Leaf(object lane) => lane switch
    {
        float single => single.ToString("R", Invariant),
        double precise => precise.ToString("R", Invariant),
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, Invariant),
        _ => lane.ToString() ?? string.Empty,
    };

    private static bool TryLeaf(Type type, string text, out object lane)
    {
        lane = 0;
        if (type == typeof(float) && float.TryParse(text, NumberStyles.Float, Invariant, out float single))
            lane = single;
        else if (type == typeof(double) && double.TryParse(text, NumberStyles.Float, Invariant, out double precise))
            lane = precise;
        else if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, Invariant, out int whole))
            lane = whole;
        else if (type == typeof(long) && long.TryParse(text, NumberStyles.Integer, Invariant, out long wide))
            lane = wide;
        else if (type == typeof(bool) && TryBool(text, out bool flag))
            lane = flag;
        else
            return false;
        return true;
    }
}
