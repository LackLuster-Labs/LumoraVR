using System;
using System.Runtime.CompilerServices;

namespace Lumora.Core;

/// <summary>
/// Strongly-typed reference identifier for world elements.
/// </summary>
public readonly struct RefID : IEquatable<RefID>, IComparable<RefID>
{
    public static readonly RefID Null = default;

    private readonly ulong _value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefID(ulong value) => _value = value;

    /// <summary>
    /// Whether this is a null/unset reference.
    /// </summary>
    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value == 0;
    }

    /// <summary>
    /// Whether this RefID belongs to the local allocation space (not networked).
    /// </summary>
    public bool IsLocalID
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => GetUserByte() == RefIDConstants.LOCAL_BYTE;
    }

    /// <summary>
    /// Whether this RefID belongs to the authority/host.
    /// </summary>
    public bool IsAuthorityID
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => GetUserByte() == RefIDConstants.AUTHORITY_BYTE;
    }

    /// <summary>
    /// Extract the user allocation byte (MSB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetUserByte() => (byte)(_value >> 56);

    /// <summary>
    /// Extract the position counter (lower 56 bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetPosition() => _value & 0x00FFFFFFFFFFFFFFUL;

    /// <summary>
    /// Check if this RefID belongs to a specific user byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool BelongsToUser(byte userByte) => GetUserByte() == userByte;

    /// <summary>
    /// Check if this RefID is within a given range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInRange(RefID start, RefID end) => _value >= start._value && _value < end._value;

    /// <summary>
    /// Construct a RefID from user byte and position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID Construct(byte userByte, ulong position)
    {
        return new RefID(((ulong)userByte << 56) | (position & 0x00FFFFFFFFFFFFFFUL));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ulong(RefID id) => id._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator RefID(ulong value) => new RefID(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(RefID other) => _value == other._value;

    public override bool Equals(object obj) => obj is RefID other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _value.GetHashCode();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(RefID left, RefID right) => left._value == right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(RefID left, RefID right) => left._value != right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(RefID other) => _value.CompareTo(other._value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(RefID left, RefID right) => left._value < right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(RefID left, RefID right) => left._value > right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(RefID left, RefID right) => left._value <= right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(RefID left, RefID right) => left._value >= right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID operator +(RefID id, ulong offset) => new RefID(id._value + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID operator -(RefID id, ulong offset) => new RefID(id._value - offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong operator -(RefID left, RefID right) => left._value - right._value;

    /// <summary>
    /// Format as human-readable string showing structure.
    /// </summary>
    public override string ToString()
    {
        if (IsNull) return "RefID.Null";

        byte userByte = GetUserByte();
        ulong position = GetPosition();

        string userLabel = userByte switch
        {
            RefIDConstants.AUTHORITY_BYTE => "Auth",
            RefIDConstants.LOCAL_BYTE => "Local",
            RefIDConstants.RESERVED_BYTE => "Reserved",
            _ => $"User[{userByte:D3}]"
        };

        return $"{userLabel}:{position:X14}";
    }
}

/// <summary>
/// Constants for RefID allocation bytes.
/// </summary>
public static class RefIDConstants
{
    public const byte AUTHORITY_BYTE = 0;
    public const byte LOCAL_BYTE = 254;
    public const byte RESERVED_BYTE = 255;
    public const byte MAX_USER_BYTE = 253;
    public const byte FIRST_USER_BYTE = 1;
}
