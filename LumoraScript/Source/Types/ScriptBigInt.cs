// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Numerics;

namespace Lumora.Script;

// A whole number of any size, the script type "bigint". It travels as a handle the way text does: it never
// changes once made, and it is compared and hashed by value, so two equal numbers are one dictionary key. An
// unset one reads as 0.
//
// One number holds at most MaxBits bits, and a node whose answer would be longer stops the call before it starts
// working it out. That is a crash guard, not a limit: a node is one host call, which the per-call stop cannot
// interrupt, and a number a few megabytes long takes seconds to multiply or write out as text, or runs the
// machine out of memory on a Pow. 262,144 bits is about 79,000 digits. -xlinka
public sealed class ScriptBigInt : IEquatable<ScriptBigInt>, IComparable<ScriptBigInt>
{
    public const int MaxBits = 1 << 18;

    public static readonly ScriptBigInt Zero = new(BigInteger.Zero);
    public static readonly ScriptBigInt One = new(BigInteger.One);

    public BigInteger Value { get; }

    private ScriptBigInt(BigInteger value) => Value = value;

    public static ScriptBigInt Of(BigInteger value)
    {
        if (value.IsZero)
            return Zero;
        if (value.IsOne)
            return One;
        Fits(value.GetBitLength());
        return new ScriptBigInt(value);
    }

    // An unset handle is 0.
    public static BigInteger ValueOf(ScriptBigInt? number) => number?.Value ?? BigInteger.Zero;

    public static long Bits(ScriptBigInt? number) => number == null ? 0 : (long)number.Value.GetBitLength();

    // Stops the call when an answer of that many bits would not fit.
    public static void Fits(long bits)
    {
        if (bits > MaxBits)
            throw new OverflowException($"the answer would be a big integer of more than {MaxBits:N0} bits");
    }

    // Decimal, with a sign in front if any, or hexadecimal after 0x. Spaces round it and _ between digits are
    // let through. Text that is not one, or one too long to fit, is not taken.
    public static bool TryParse(string? text, out ScriptBigInt number)
    {
        number = Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        bool negative = false;
        if (trimmed.Length > 0 && (trimmed[0] == '-' || trimmed[0] == '+'))
        {
            negative = trimmed[0] == '-';
            trimmed = trimmed[1..];
        }
        if (trimmed.Length == 0)
            return false;
        BigInteger value;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var digits = trimmed[2..];
            if (digits.Length == 0 || (long)digits.Length * 4 > MaxBits + 4)
                return false;
            // A leading 0 keeps the top digit from reading as a sign.
            if (!BigInteger.TryParse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
                return false;
        }
        else
        {
            foreach (char c in trimmed)
            {
                if (c is < '0' or > '9')
                    return false;
            }
            if ((long)trimmed.Length * 3322 / 1000 > MaxBits + 4)
                return false;
            if (!BigInteger.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                return false;
        }
        if (value.GetBitLength() > MaxBits)
            return false;
        number = Of(negative ? -value : value);
        return true;
    }

    // What the math nodes do with them: an unset input is 0, division truncates toward zero and leaves the
    // remainder the dividend's sign as whole numbers do, and dividing by zero stops the call as it does for
    // an int.
    public static ScriptBigInt Add(ScriptBigInt? a, ScriptBigInt? b) => Of(ValueOf(a) + ValueOf(b));

    public static ScriptBigInt Subtract(ScriptBigInt? a, ScriptBigInt? b) => Of(ValueOf(a) - ValueOf(b));

    public static ScriptBigInt Multiply(ScriptBigInt? a, ScriptBigInt? b)
    {
        Fits(Bits(a) + Bits(b) - 1);
        return Of(ValueOf(a) * ValueOf(b));
    }

    public static ScriptBigInt Divide(ScriptBigInt? a, ScriptBigInt? b) => Of(BigInteger.Divide(ValueOf(a), ValueOf(b)));

    public static ScriptBigInt Remainder(ScriptBigInt? a, ScriptBigInt? b) => Of(BigInteger.Remainder(ValueOf(a), ValueOf(b)));

    public static ScriptBigInt Negate(ScriptBigInt? value) => Of(-ValueOf(value));

    public static ScriptBigInt Abs(ScriptBigInt? value) => Of(BigInteger.Abs(ValueOf(value)));

    public static ScriptBigInt Min(ScriptBigInt? a, ScriptBigInt? b) => Compare(a, b) <= 0 ? a ?? Zero : b ?? Zero;

    public static ScriptBigInt Max(ScriptBigInt? a, ScriptBigInt? b) => Compare(a, b) >= 0 ? a ?? Zero : b ?? Zero;

    public static int Compare(ScriptBigInt? a, ScriptBigInt? b) => ValueOf(a).CompareTo(ValueOf(b));

    public static int Sign(ScriptBigInt? value) => ValueOf(value).Sign;

    public static ScriptBigInt GreatestCommonDivisor(ScriptBigInt? a, ScriptBigInt? b)
        => Of(BigInteger.GreatestCommonDivisor(ValueOf(a), ValueOf(b)));

    // A power below zero is the whole part of one over it: 0 for anything past 1 in size, and 1 over 0 stops
    // the call.
    public static ScriptBigInt Pow(ScriptBigInt? value, int power)
    {
        var x = ValueOf(value);
        if (power < 0)
        {
            if (x.IsZero)
                throw new DivideByZeroException("0 to a power below zero");
            return BigInteger.Abs(x).IsOne ? Of(x.Sign < 0 && (power & 1) != 0 ? BigInteger.MinusOne : BigInteger.One) : Zero;
        }
        long bits = (long)BigInteger.Abs(x).GetBitLength();
        if (bits > 1)
            Fits((bits - 1) * power + 1);
        return Of(BigInteger.Pow(x, power));
    }

    // value to the power, all modulo modulus, worked out without the full power: what keys and checksums
    // need. A modulus of 0 stops the call, a power below zero is refused the same way. Each bit of the power
    // squares a number the modulus' size, so both are held to PowModBits, the size of the largest keys in use.
    public const int PowModBits = 4096;

    public static ScriptBigInt PowMod(ScriptBigInt? value, ScriptBigInt? power, ScriptBigInt? modulus)
    {
        if (ValueOf(power).Sign < 0)
            throw new ArgumentOutOfRangeException(nameof(power), "Power Mod takes a power of 0 or more");
        if (Bits(power) > PowModBits || Bits(modulus) > PowModBits)
            throw new OverflowException($"Power Mod takes a power and a modulus of up to {PowModBits:N0} bits");
        return Of(BigInteger.ModPow(ValueOf(value), ValueOf(power), ValueOf(modulus)));
    }

    // To a fixed-size number, held at the ends of its range when too big for it.
    public static long ToLong(ScriptBigInt? value)
    {
        var x = ValueOf(value);
        return x > long.MaxValue ? long.MaxValue : x < long.MinValue ? long.MinValue : (long)x;
    }

    public static int ToInt(ScriptBigInt? value)
    {
        var x = ValueOf(value);
        return x > int.MaxValue ? int.MaxValue : x < int.MinValue ? int.MinValue : (int)x;
    }

    // The nearest double, infinite past its range.
    public static double ToDouble(ScriptBigInt? value) => (double)ValueOf(value);

    // A double's whole part; one that is not a finite number is 0.
    public static ScriptBigInt FromDouble(double value) => double.IsFinite(value) ? Of(new BigInteger(Math.Truncate(value))) : Zero;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public string ToHex()
    {
        var magnitude = BigInteger.Abs(Value).ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
        return (Value.Sign < 0 ? "-0x" : "0x") + (magnitude.Length == 0 ? "0" : magnitude);
    }

    public bool Equals(ScriptBigInt? other) => other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) => obj is ScriptBigInt other && Value.Equals(other.Value);

    public override int GetHashCode() => Value.GetHashCode();

    public int CompareTo(ScriptBigInt? other) => Value.CompareTo(ValueOf(other));
}
