using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lumora.Core;

/// <summary>
/// Strongly-typed reference identifier for world elements.
///
/// RefID Structure (64 bits):
/// - Bits 56-63 (8 bits): User byte - identifies allocation domain
/// - Bits 0-55 (56 bits): Position - sequential counter within domain
///
/// User Byte Allocation:
/// - 0: Authority/Host allocated IDs
/// - 1-253: Per-user allocated IDs (supports 253 concurrent users)
/// - 254: Local-only IDs (not networked)
/// - 255: Reserved for future use
///
/// Each user has 2^56 (~72 quadrillion) IDs available in their domain.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RefID : IEquatable<RefID>, IComparable<RefID>
{
    /// <summary>
    /// The null/unset reference ID.
    /// </summary>
    public static readonly RefID Null = default;

    /// <summary>
    /// Maximum position value (56 bits).
    /// </summary>
    public const ulong MaxPosition = 0x00FFFFFFFFFFFFFFUL;

    /// <summary>
    /// Mask for extracting position from raw value.
    /// </summary>
    private const ulong PositionMask = 0x00FFFFFFFFFFFFFFUL;

    /// <summary>
    /// Number of bits used for position.
    /// </summary>
    private const int PositionBits = 56;

    private readonly ulong _value;

    /// <summary>
    /// Create a RefID from a raw 64-bit value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefID(ulong value) => _value = value;

    /// <summary>
    /// The raw 64-bit value of this RefID.
    /// </summary>
    public ulong RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value;
    }

    /// <summary>
    /// Whether this is a null/unset reference.
    /// </summary>
    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value == 0;
    }

    /// <summary>
    /// Whether this is a valid (non-null) reference.
    /// </summary>
    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _value != 0;
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
    /// Whether this RefID belongs to a user (not authority or local).
    /// </summary>
    public bool IsUserID
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            byte userByte = GetUserByte();
            return userByte >= RefIDConstants.FIRST_USER_BYTE && userByte <= RefIDConstants.MAX_USER_BYTE;
        }
    }

    /// <summary>
    /// Whether this RefID is in the reserved space.
    /// </summary>
    public bool IsReserved
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => GetUserByte() == RefIDConstants.RESERVED_BYTE;
    }

    /// <summary>
    /// Whether this RefID should be networked (not local).
    /// </summary>
    public bool IsNetworked
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsLocalID && !IsNull;
    }

    /// <summary>
    /// Extract the user allocation byte (MSB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetUserByte() => (byte)(_value >> PositionBits);

    /// <summary>
    /// Extract the position counter (lower 56 bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetPosition() => _value & PositionMask;

    /// <summary>
    /// Check if this RefID belongs to a specific user byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool BelongsToUser(byte userByte) => GetUserByte() == userByte;

    /// <summary>
    /// Check if this RefID is within a given range [start, end).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInRange(RefID start, RefID end) => _value >= start._value && _value < end._value;

    /// <summary>
    /// Check if this RefID is within a given range [start, end].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInRangeInclusive(RefID start, RefID end) => _value >= start._value && _value <= end._value;

    /// <summary>
    /// Get the distance between this RefID and another.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long DistanceTo(RefID other) => (long)(other._value - _value);

    /// <summary>
    /// Create a new RefID offset from this one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefID Offset(long amount) => new RefID((ulong)((long)_value + amount));

    /// <summary>
    /// Get the next RefID in sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefID Next() => new RefID(_value + 1);

    /// <summary>
    /// Get the previous RefID in sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefID Previous() => new RefID(_value - 1);

    /// <summary>
    /// Create a RefID with the same user byte but different position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefID WithPosition(ulong position) => Construct(GetUserByte(), position);

    /// <summary>
    /// Create a RefID with the same position but different user byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefID WithUserByte(byte userByte) => Construct(userByte, GetPosition());

    #region Static Construction

    /// <summary>
    /// Construct a RefID from user byte and position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID Construct(byte userByte, ulong position)
    {
        return new RefID(((ulong)userByte << PositionBits) | (position & PositionMask));
    }

    /// <summary>
    /// Get the first RefID for a given user byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID GetRangeStart(byte userByte) => Construct(userByte, 1);

    /// <summary>
    /// Get the last valid RefID for a given user byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID GetRangeEnd(byte userByte) => Construct(userByte, MaxPosition);

    /// <summary>
    /// Get the exclusive end of a user byte range (first ID of next byte).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID GetRangeEndExclusive(byte userByte)
    {
        if (userByte == 255) return new RefID(ulong.MaxValue);
        return Construct((byte)(userByte + 1), 0);
    }

    /// <summary>
    /// Create a RefID for the authority domain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID CreateAuthority(ulong position) => Construct(RefIDConstants.AUTHORITY_BYTE, position);

    /// <summary>
    /// Create a RefID for the local (non-networked) domain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID CreateLocal(ulong position) => Construct(RefIDConstants.LOCAL_BYTE, position);

    /// <summary>
    /// Create a RefID for a specific user domain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID CreateUser(byte userByte, ulong position)
    {
        if (userByte < RefIDConstants.FIRST_USER_BYTE || userByte > RefIDConstants.MAX_USER_BYTE)
            throw new ArgumentOutOfRangeException(nameof(userByte),
                $"User byte must be between {RefIDConstants.FIRST_USER_BYTE} and {RefIDConstants.MAX_USER_BYTE}");
        return Construct(userByte, position);
    }

    /// <summary>
    /// Try to parse a RefID from a string.
    /// </summary>
    public static bool TryParse(string str, out RefID refId)
    {
        refId = Null;
        if (string.IsNullOrEmpty(str)) return false;

        if (str == "RefID.Null" || str == "Null" || str == "0")
        {
            refId = Null;
            return true;
        }

        // Try parsing as raw hex
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(str.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ulong val))
            {
                refId = new RefID(val);
                return true;
            }
        }

        // Try parsing as decimal
        if (ulong.TryParse(str, out ulong rawValue))
        {
            refId = new RefID(rawValue);
            return true;
        }

        // Try parsing structured format "Domain:Position"
        int colonIndex = str.IndexOf(':');
        if (colonIndex > 0)
        {
            string domainPart = str.Substring(0, colonIndex);
            string positionPart = str.Substring(colonIndex + 1);

            byte userByte;
            if (domainPart.Equals("Auth", StringComparison.OrdinalIgnoreCase))
                userByte = RefIDConstants.AUTHORITY_BYTE;
            else if (domainPart.Equals("Local", StringComparison.OrdinalIgnoreCase))
                userByte = RefIDConstants.LOCAL_BYTE;
            else if (domainPart.Equals("Reserved", StringComparison.OrdinalIgnoreCase))
                userByte = RefIDConstants.RESERVED_BYTE;
            else if (domainPart.StartsWith("User[") && domainPart.EndsWith("]"))
            {
                if (!byte.TryParse(domainPart.AsSpan(5, domainPart.Length - 6), out userByte))
                    return false;
            }
            else
                return false;

            if (ulong.TryParse(positionPart, System.Globalization.NumberStyles.HexNumber, null, out ulong position))
            {
                refId = Construct(userByte, position);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parse a RefID from a string, throwing on failure.
    /// </summary>
    public static RefID Parse(string str)
    {
        if (!TryParse(str, out RefID refId))
            throw new FormatException($"Cannot parse '{str}' as RefID");
        return refId;
    }

    #endregion

    #region Operators

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ulong(RefID id) => id._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator RefID(ulong value) => new RefID(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(RefID left, RefID right) => left._value == right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(RefID left, RefID right) => left._value != right._value;

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
    public static RefID operator +(RefID id, int offset) => new RefID((ulong)((long)id._value + offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID operator -(RefID id, ulong offset) => new RefID(id._value - offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID operator -(RefID id, int offset) => new RefID((ulong)((long)id._value - offset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong operator -(RefID left, RefID right) => left._value - right._value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID operator ++(RefID id) => new RefID(id._value + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID operator --(RefID id) => new RefID(id._value - 1);

    #endregion

    #region Equality & Comparison

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(RefID other) => _value == other._value;

    public override bool Equals(object obj) => obj is RefID other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _value.GetHashCode();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(RefID other) => _value.CompareTo(other._value);

    #endregion

    #region String Formatting

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

    /// <summary>
    /// Format as compact hex string.
    /// </summary>
    public string ToHexString() => $"0x{_value:X16}";

    /// <summary>
    /// Format as decimal string.
    /// </summary>
    public string ToDecimalString() => _value.ToString();

    #endregion
}

/// <summary>
/// Constants for RefID allocation bytes.
/// </summary>
public static class RefIDConstants
{
    /// <summary>
    /// Authority/host allocation byte (0).
    /// </summary>
    public const byte AUTHORITY_BYTE = 0;

    /// <summary>
    /// Local-only allocation byte (254) - not networked.
    /// </summary>
    public const byte LOCAL_BYTE = 254;

    /// <summary>
    /// Reserved byte (255) - for future use.
    /// </summary>
    public const byte RESERVED_BYTE = 255;

    /// <summary>
    /// Maximum user byte for normal users (253).
    /// </summary>
    public const byte MAX_USER_BYTE = 253;

    /// <summary>
    /// First user byte for normal users (1).
    /// </summary>
    public const byte FIRST_USER_BYTE = 1;

    /// <summary>
    /// Maximum number of concurrent users (253).
    /// </summary>
    public const int MAX_USERS = MAX_USER_BYTE;

    /// <summary>
    /// Number of IDs available per user domain (2^56).
    /// </summary>
    public const ulong IDS_PER_DOMAIN = RefID.MaxPosition;

    /// <summary>
    /// Check if a user byte is valid for user allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidUserByte(byte userByte) =>
        userByte >= FIRST_USER_BYTE && userByte <= MAX_USER_BYTE;

    /// <summary>
    /// Check if a user byte is valid for any allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidAllocationByte(byte userByte) =>
        userByte != RESERVED_BYTE;
}

/// <summary>
/// Represents a range of RefIDs.
/// </summary>
public readonly struct RefIDRange
{
    /// <summary>
    /// Start of the range (inclusive).
    /// </summary>
    public readonly RefID Start;

    /// <summary>
    /// End of the range (exclusive).
    /// </summary>
    public readonly RefID End;

    /// <summary>
    /// The user byte this range belongs to.
    /// </summary>
    public byte UserByte => Start.GetUserByte();

    /// <summary>
    /// Number of IDs in this range.
    /// </summary>
    public ulong Count => End - Start;

    /// <summary>
    /// Whether this range is empty.
    /// </summary>
    public bool IsEmpty => Start >= End;

    /// <summary>
    /// Whether this range is valid.
    /// </summary>
    public bool IsValid => Start.IsValid && End.IsValid && Start < End;

    public RefIDRange(RefID start, RefID end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Create a range for a specific user byte.
    /// </summary>
    public static RefIDRange ForUserByte(byte userByte)
    {
        return new RefIDRange(
            RefID.GetRangeStart(userByte),
            RefID.GetRangeEndExclusive(userByte)
        );
    }

    /// <summary>
    /// Create the authority range.
    /// </summary>
    public static RefIDRange Authority => ForUserByte(RefIDConstants.AUTHORITY_BYTE);

    /// <summary>
    /// Create the local range.
    /// </summary>
    public static RefIDRange Local => ForUserByte(RefIDConstants.LOCAL_BYTE);

    /// <summary>
    /// Check if a RefID is within this range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(RefID id) => id.IsInRange(Start, End);

    /// <summary>
    /// Deconstruct for tuple-style usage: var (start, end) = range;
    /// </summary>
    public void Deconstruct(out RefID start, out RefID end)
    {
        start = Start;
        end = End;
    }

    /// <summary>
    /// Check if another range overlaps with this one.
    /// </summary>
    public bool Overlaps(RefIDRange other) => Start < other.End && End > other.Start;

    /// <summary>
    /// Get the intersection of two ranges.
    /// </summary>
    public RefIDRange Intersect(RefIDRange other)
    {
        RefID newStart = Start > other.Start ? Start : other.Start;
        RefID newEnd = End < other.End ? End : other.End;
        return new RefIDRange(newStart, newEnd);
    }

    public override string ToString() => $"[{Start} - {End}) ({Count} IDs)";
}
