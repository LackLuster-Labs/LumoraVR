namespace Lumora.Core.Helpers;

public interface IDirtyFlags
{
    public bool AnySet { get; }
    public void Clear();
    public bool IsSet(byte position);
    public void SetFlag(byte position);
    public void UnsetFlag(byte position);
    public void ValueFlag(byte position, bool value);
}
public struct DirtyFlags8 : IDirtyFlags
{
    public byte Value { get; private set; } = 0;

    private const byte One = 1;
    public static readonly DirtyFlags8 None = new();
    public static readonly DirtyFlags8 All = new(byte.MaxValue);

    public DirtyFlags8()
    {
    }
    public DirtyFlags8(byte value) => Value = value;

    public bool AnySet => Value != 0;
    public void Clear() => Value = 0;
    public bool IsSet(byte position) => (Value & (One << position)) != 0;
    public void SetFlag(byte position) => Value |= (byte)(One << position);
    public void UnsetFlag(byte position) => Value = (byte)(Value & ~(One << position));
    public void ValueFlag(byte position, bool value) =>
        Value = (byte)(value ?
                Value | (One << position) :
                Value & ~(One << position)
            );
}
public struct DirtyFlags16 : IDirtyFlags
{
    public ushort Value { get; private set; } = 0;

    private const ushort One = 1;
    public static readonly DirtyFlags16 None = new();
    public static readonly DirtyFlags16 All = new(ushort.MaxValue);

    public DirtyFlags16()
    {
    }
    public DirtyFlags16(ushort value) => Value = value;

    public bool AnySet => Value != 0;
    public void Clear() => Value = 0;
    public bool IsSet(byte position) => (Value & (One << position)) != 0;
    public void SetFlag(byte position) => Value |= (ushort)(One << position);
    public void UnsetFlag(byte position) => Value = (ushort)(Value & ~(One << position));
    public void ValueFlag(byte position, bool value) =>
        Value = (ushort)(value ?
                Value | (One << position) :
                Value & ~(One << position)
            );
}
public struct DirtyFlags32 : IDirtyFlags
{
    public uint Value { get; private set; } = 0;

    private const uint One = 1;
    public static readonly DirtyFlags32 None = new();
    public static readonly DirtyFlags32 All = new(uint.MaxValue);

    public DirtyFlags32()
    {
    }
    public DirtyFlags32(uint value) => Value = value;

    public bool AnySet => Value != 0;
    public void Clear() => Value = 0;
    public bool IsSet(byte position) => (Value & (One << position)) != 0;
    public void SetFlag(byte position) => Value |= (One << position);
    public void UnsetFlag(byte position) => Value = (Value & ~(One << position));
    public void ValueFlag(byte position, bool value) =>
        Value = (value ?
                Value | (One << position) :
                Value & ~(One << position)
            );
}
public struct DirtyFlags64 : IDirtyFlags
{
    public ulong Value { get; private set; } = 0;

    private const ulong One = 1;
    public static readonly DirtyFlags64 None = new();
    public static readonly DirtyFlags64 All = new(ulong.MaxValue);

    public DirtyFlags64()
    {
    }
    public DirtyFlags64(ulong value) => Value = value;

    public bool AnySet => Value != 0;
    public void Clear() => Value = 0;
    public bool IsSet(byte position) => (Value & (One << position)) != 0;
    public void SetFlag(byte position) => Value |= (One << position);
    public void UnsetFlag(byte position) => Value = (Value & ~(One << position));
    public void ValueFlag(byte position, bool value) =>
        Value = (value ?
                Value | (One << position) :
                Value & ~(One << position)
            );
}
