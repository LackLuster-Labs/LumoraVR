// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Networking.Sync;
using Lumora.Script;

namespace Lumora.Core.Components.Scripting;

// A node constant as a card shows it and as someone types it back: numbers as numbers, text without
// quotes, vectors as "1, 2, 3", dates and spans and quantities as they are written, a bound object by its
// name. Parsing is by the port's type, and anything that does not fit is refused rather than guessed at.
// -xlinka
public static class ScriptValueText
{
    public static string Display(ScriptGraphValue? entry)
    {
        if (entry == null)
            return string.Empty;
        if (!entry.Reference.Value.IsNull)
            return entry.Reference.Target is { } target ? NameOf(target) : "(gone)";
        if (string.IsNullOrEmpty(entry.Json.Value))
            return string.Empty;
        try
        {
            return Display(GraphValue.ParseJson(entry.Json.Value));
        }
        catch (GraphFormatException)
        {
            return entry.Json.Value;
        }
    }

    public static string Display(GraphValue value) => value.Kind switch
    {
        GraphValueKind.Bool => value.Bool ? "true" : "false",
        GraphValueKind.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
        GraphValueKind.String => value.String ?? string.Empty,
        GraphValueKind.Numbers => string.Join(", ", Array.ConvertAll(value.Numbers, n => n.ToString("R", CultureInfo.InvariantCulture))),
        GraphValueKind.Binding => $"binding {value.Binding}",
        _ => string.Empty,
    };

    public static string NameOf(IWorldElement element) => element switch
    {
        Slot slot => slot.Name.Value ?? "slot",
        User user => user.UserName.Value ?? "user",
        Component component => $"{component.Slot?.Name.Value}.{component.GetType().Name}",
        Assets.ShaderUniformParam property when property.Parent is SyncElement { Parent: Component material }
            => $"{material.Slot?.Name.Value}.{property.Name.Value}",
        SyncElement member when member.Parent is Slot owner => $"{owner.Name.Value}.{((ISyncMember)member).Name}",
        SyncElement member when member.Parent is Component owner => $"{owner.Slot?.Name.Value}.{((ISyncMember)member).Name}",
        _ => element.GetType().Name,
    };

    // Whether a port of this type takes typed text at all; objects arrive by drop instead. A big integer is
    // typed as its digits.
    public static bool IsTyped(ScriptType type)
        => type.Kind != ScriptTypeKind.Handle || type == ScriptTypes.String || type.ClrType == typeof(ScriptBigInt);

    public static bool TryParse(string text, ScriptType type, out GraphValue value, out string? error)
        => TryParse(text, type, 0d, out value, out error);

    public static bool IsNumber(ScriptType type)
        => type.Kind is ScriptTypeKind.Int or ScriptTypeKind.Long or ScriptTypeKind.Float or ScriptTypeKind.Double;

    // current is what a number port holds now: a number takes math against it (FieldMath, "*2" doubles it),
    // and an Int or Long rounds half away from zero into its range.
    public static bool TryParse(string text, ScriptType type, double current, out GraphValue value, out string? error)
    {
        value = GraphValue.Null;
        error = null;
        // Text is taken exactly; every other type ignores spaces around what is typed.
        string raw = text ?? string.Empty;
        text = raw.Trim();
        switch (type.Kind)
        {
            case ScriptTypeKind.Bool:
                if (text is "true" or "1" or "yes" or "on")
                    value = GraphValue.Of(true);
                else if (text is "false" or "0" or "no" or "off")
                    value = GraphValue.Of(false);
                else
                    error = "true or false";
                return error == null;

            case ScriptTypeKind.Int:
            case ScriptTypeKind.Long:
            case ScriptTypeKind.Float:
            case ScriptTypeKind.Double:
            {
                var result = Helio.UI.FieldMath.Evaluate(text, current);
                if (!result.Ok)
                {
                    error = "a number";
                    return false;
                }
                double number = result.Value;
                if (type.Kind == ScriptTypeKind.Int)
                    number = System.Math.Clamp(System.Math.Round(number, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
                else if (type.Kind == ScriptTypeKind.Long)
                    number = System.Math.Clamp(System.Math.Round(number, MidpointRounding.AwayFromZero), long.MinValue, long.MaxValue);
                value = GraphValue.Of(number);
                return true;
            }

            case ScriptTypeKind.Vector:
            {
                if (!ScriptTextForm.TryParse(type, text, out var boxed))
                {
                    error = VectorHint(type);
                    return false;
                }
                var numbers = ScriptTextForm.Numbers(type, boxed);
                if (Array.Exists(numbers, n => !double.IsFinite(n)))
                {
                    error = VectorHint(type);
                    return false;
                }
                value = GraphValue.Of(numbers);
                return true;
            }

            case ScriptTypeKind.Coded:
                if (!type.Codec!.TryParse(text, out var decoded) || decoded == null)
                {
                    error = CodedHint(type);
                    return false;
                }
                value = type.Codec.ToGraph(decoded);
                return true;
        }

        if (type == ScriptTypes.String)
        {
            value = GraphValue.Of(raw);
            return true;
        }
        if (type.ClrType == typeof(ScriptBigInt))
        {
            if (ScriptBigInt.TryParse(text, out var number))
            {
                value = GraphValue.Of(number.ToString());
                return true;
            }
            error = "a whole number, or hex after 0x";
            return false;
        }
        error = "drop an object here";
        return false;
    }

    private static string VectorHint(ScriptType type)
    {
        if (type.Name.StartsWith("bool", StringComparison.Ordinal))
            return $"{type.LaneCount} of true or false";
        if (type.Name.StartsWith("int", StringComparison.Ordinal))
            return $"{type.LaneCount} whole numbers";
        return $"{type.LaneCount} numbers";
    }

    private static string CodedHint(ScriptType type)
    {
        if (type.ClrType.IsEnum)
        {
            var names = Enum.GetNames(type.ClrType);
            return names.Length <= 6 ? "one of " + string.Join(", ", names) : $"one of {string.Join(", ", names[..6])}, ...";
        }
        return type.Name switch
        {
            "datetime" => "a date like 2026-09-28 or 2026-09-28T14:30:00Z",
            "timespan" => "a time like 1:30:00, 90s or 2h",
            "guid" => "a guid like 0f8fad5b-d9cb-469f-a165-70867728950e",
            "char" => "one character, or space, tab, \\n, U+00E9",
            "half" => "a number",
            "byte" or "sbyte" or "short" or "ushort" or "uint" or "ulong" => $"a whole number that fits a {type.Name}",
            _ => $"an amount like 1.5 {ScriptQuantities.FindUnit(QuantityUnits(type), null)?.Symbol}",
        };
    }

    private static ScriptUnit[] QuantityUnits(ScriptType type)
        => type.ClrType.GetProperty("Units")?.GetValue(null) as ScriptUnit[] ?? new[] { new ScriptUnit("", 1d) };
}
