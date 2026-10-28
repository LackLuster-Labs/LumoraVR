// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Script;

// A constant already converted to the port's type. Strings and addresses become handle globals set by
// the start function; a binding is the fixed handle of its slot.
internal sealed class ConstantValue
{
    public ScriptType Type { get; }
    public object? Value { get; }
    public int Binding { get; }

    // For an address, the global holding the handle the start function made of it.
    public int Global { get; set; } = -1;

    private ulong[]? _raw;

    private ConstantValue(ScriptType type, object? value, int binding)
    {
        Type = type;
        Value = value;
        Binding = binding;
    }

    public static ConstantValue Default(ScriptType type) => new(type, type.DefaultValue, -1);

    public static ConstantValue OfBinding(ScriptType type, int binding) => new(type, null, binding);

    public static ConstantValue OfBoxed(ScriptType type, object? value) => new(type, value, -1);

    public bool IsString => Type.Kind == ScriptTypeKind.Handle && Value is string;

    // A value type's lanes as raw bits, for the instructions that put it on the stack.
    public ulong[] RawLanes
    {
        get
        {
            if (_raw == null)
            {
                var raw = new ulong[Type.LaneCount];
                if (!Type.IsHandle)
                    LaneCodec.Write(Type, Value, raw, null);
                _raw = raw;
            }
            return _raw;
        }
    }

    // A document value on a port of this type, or an error saying why it does not fit.
    public static bool TryConvert(GraphValue value, ScriptType type, out ConstantValue constant, out string error)
    {
        constant = null!;
        error = string.Empty;

        if (value.Kind == GraphValueKind.Null)
        {
            constant = Default(type);
            return true;
        }

        switch (type.Kind)
        {
            case ScriptTypeKind.Bool when value.Kind == GraphValueKind.Bool:
                constant = new(type, value.Bool, -1);
                return true;
            case ScriptTypeKind.Int when value.Kind == GraphValueKind.Number:
                if (value.Number != Math.Floor(value.Number) || value.Number < int.MinValue || value.Number > int.MaxValue)
                {
                    error = $"{value} is not an int";
                    return false;
                }
                constant = new(type, (int)value.Number, -1);
                return true;
            case ScriptTypeKind.Long when value.Kind == GraphValueKind.Number:
                if (value.Number != Math.Floor(value.Number) || Math.Abs(value.Number) > 9007199254740992d)
                {
                    error = $"{value} is not a long a document can state exactly";
                    return false;
                }
                constant = new(type, (long)value.Number, -1);
                return true;
            case ScriptTypeKind.Float when value.Kind == GraphValueKind.Number:
                constant = new(type, (float)value.Number, -1);
                return true;
            case ScriptTypeKind.Double when value.Kind == GraphValueKind.Number:
                constant = new(type, value.Number, -1);
                return true;
            case ScriptTypeKind.Vector when value.Kind == GraphValueKind.Numbers:
            {
                if (value.Numbers.Length != type.LaneCount)
                {
                    error = $"{type.Name} takes {type.LaneCount} numbers, not {value.Numbers.Length}";
                    return false;
                }
                var boxed = Activator.CreateInstance(type.ClrType)!;
                for (int i = 0; i < type.Leaves.Length; i++)
                {
                    var leaf = type.Leaves[i];
                    double number = value.Numbers[i];
                    object lane;
                    if (leaf.ClrType == typeof(float))
                    {
                        lane = (float)number;
                    }
                    else if (leaf.ClrType == typeof(double))
                    {
                        lane = number;
                    }
                    else if (leaf.ClrType == typeof(bool))
                    {
                        if (number is not (0d or 1d))
                        {
                            error = $"{type.Name} takes 0 or 1 for each of its flags";
                            return false;
                        }
                        lane = number == 1d;
                    }
                    else if (number != Math.Floor(number) || (leaf.ClrType == typeof(int) && (number < int.MinValue || number > int.MaxValue))
                             || Math.Abs(number) > 9007199254740992d)
                    {
                        error = $"{type.Name} takes whole numbers";
                        return false;
                    }
                    else if (leaf.ClrType == typeof(int))
                    {
                        lane = (int)number;
                    }
                    else
                    {
                        lane = (long)number;
                    }
                    leaf.Set(boxed, lane);
                }
                constant = new(type, boxed, -1);
                return true;
            }
            case ScriptTypeKind.Coded:
                if (value.Kind is GraphValueKind.String or GraphValueKind.Number && type.Codec!.TryFromGraph(value, out var decoded))
                {
                    constant = new(type, decoded, -1);
                    return true;
                }
                break;
            case ScriptTypeKind.Handle when value.Kind == GraphValueKind.String && type == ScriptTypes.String:
                constant = new(type, value.String, -1);
                return true;
            case ScriptTypeKind.Handle when value.Kind == GraphValueKind.String && type.ClrType == typeof(Uri):
                // An address is written as its text, by the same rule Text To URL reads text by.
                if (TextToUrl.Run(value.String, out _) is { } url)
                {
                    constant = new(type, url, -1);
                    return true;
                }
                error = $"'{value.String}' is not a whole address";
                return false;
            case ScriptTypeKind.Handle when type.ClrType == typeof(ScriptBigInt) && value.Kind is GraphValueKind.String or GraphValueKind.Number:
            {
                // A big integer is written as its digits, or as a whole number a document states exactly.
                string? digits = value.Kind == GraphValueKind.String ? value.String
                    : value.Number == Math.Floor(value.Number) && Math.Abs(value.Number) <= 9007199254740992d
                        ? ((long)value.Number).ToString(CultureInfo.InvariantCulture) : null;
                if (digits != null && ScriptBigInt.TryParse(digits, out var number))
                {
                    constant = new(type, number, -1);
                    return true;
                }
                error = $"{value} is not a whole number a big integer holds";
                return false;
            }
            case ScriptTypeKind.Handle when value.Kind == GraphValueKind.Binding:
                constant = new(type, null, value.Binding);
                return true;
        }

        error = $"{value} does not fit a {type.Name} port";
        return false;
    }

    public override string ToString()
        => Binding >= 0 ? $"binding {Binding}" : Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "null";
}
