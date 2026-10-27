// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumora.Core.Math;

namespace Lumora.Script;

// The closed set of types a graph may name. Scalars, the math structs, the coded values and the
// collections are built in; the datamodel types arrive as handles LumoraCore registers at startup, so
// this assembly never has to know what a Slot is. A name that is not in here does not compile, which is
// what keeps a hostile graph from naming its way to an arbitrary CLR type. -xlinka
public static class ScriptTypes
{
    public const int MaxNameLength = 128;
    private const int MaxStructDepth = 4;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, ScriptType> _byName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, ScriptType> _byClr = new();
    private static readonly Dictionary<string, Type> _genericByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, string> _genericByClr = new();

    public static readonly ScriptType Bool;
    public static readonly ScriptType Int;
    public static readonly ScriptType Long;
    public static readonly ScriptType Float;
    public static readonly ScriptType Double;
    public static readonly ScriptType String;

    static ScriptTypes()
    {
        Bool = Add(new ScriptType("bool", typeof(bool), ScriptTypeKind.Bool));
        Int = Add(new ScriptType("int", typeof(int), ScriptTypeKind.Int));
        Long = Add(new ScriptType("long", typeof(long), ScriptTypeKind.Long));
        Float = Add(new ScriptType("float", typeof(float), ScriptTypeKind.Float));
        Double = Add(new ScriptType("double", typeof(double), ScriptTypeKind.Double));
        AddStruct("float2", typeof(float2));
        AddStruct("float3", typeof(float3));
        AddStruct("float4", typeof(float4));
        AddStruct("floatQ", typeof(floatQ));
        AddStruct("color", typeof(color));
        AddStruct("colorHDR", typeof(colorHDR));
        String = Add(new ScriptType("string", typeof(string), ScriptTypeKind.Handle));

        AddStruct("int2", typeof(int2));
        AddStruct("int3", typeof(int3));
        AddStruct("int4", typeof(int4));
        AddStruct("bool2", typeof(bool2));
        AddStruct("bool3", typeof(bool3));
        AddStruct("bool4", typeof(bool4));
        AddStruct("float2x2", typeof(float2x2));
        AddStruct("float3x3", typeof(float3x3));
        AddStruct("float4x4", typeof(float4x4));
        AddStruct("rect", typeof(Rect));
        AddStruct("bounds", typeof(BoundingBox));
        AddStruct("sh1", typeof(SphericalHarmonicsL1));
        AddStruct("sh2", typeof(SphericalHarmonicsL2));
        AddStruct("sh3", typeof(SphericalHarmonicsL3));
        AddStruct("sh4", typeof(SphericalHarmonicsL4));
        // Harmonics of one number and of a color with its alpha, beside the RGB ones above.
        AddStruct("sh1f", typeof(SphericalHarmonicsL1<float>));
        AddStruct("sh2f", typeof(SphericalHarmonicsL2<float>));
        AddStruct("sh3f", typeof(SphericalHarmonicsL3<float>));
        AddStruct("sh4f", typeof(SphericalHarmonicsL4<float>));
        AddStruct("sh1c", typeof(SphericalHarmonicsL1<color>));
        AddStruct("sh2c", typeof(SphericalHarmonicsL2<color>));
        AddStruct("sh3c", typeof(SphericalHarmonicsL3<color>));
        AddStruct("sh4c", typeof(SphericalHarmonicsL4<color>));

        AddCoded("byte", ScriptCodecs.Byte);
        AddCoded("sbyte", ScriptCodecs.SByte);
        AddCoded("short", ScriptCodecs.Short);
        AddCoded("ushort", ScriptCodecs.UShort);
        AddCoded("uint", ScriptCodecs.UInt);
        AddCoded("ulong", ScriptCodecs.ULong);
        AddCoded("half", ScriptCodecs.HalfFloat);
        AddCoded("char", ScriptCodecs.Char);
        AddCoded("datetime", ScriptCodecs.Date);
        AddCoded("timespan", ScriptCodecs.Duration);
        AddCoded("guid", ScriptCodecs.Guid);
        foreach (var (name, codec) in ScriptQuantities.Codecs)
            AddCoded(name, codec);

        Add(new ScriptType("uri", typeof(Uri), ScriptTypeKind.Handle));
        Add(new ScriptType("bigint", typeof(ScriptBigInt), ScriptTypeKind.Handle));
        RegisterGenericHandle("list", typeof(ScriptList<>));
        RegisterGenericHandle("dict", typeof(ScriptDictionary<,>));
        Add(new ScriptType("jsontoken", typeof(ScriptJson), ScriptTypeKind.Handle));
        Add(new ScriptType("jsonobject", typeof(ScriptJsonObject), ScriptTypeKind.Handle));
        Add(new ScriptType("jsonarray", typeof(ScriptJsonArray), ScriptTypeKind.Handle));
        AddCoded("jsonkind", ScriptCodecs.Enum<ScriptJsonKind>());
        AddCoded("colorchannel", ScriptCodecs.Enum<ColorChannel>());
        AddCoded("colorblend", ScriptCodecs.Enum<ColorBlendMode>());
        AddCoded("easecurve", ScriptCodecs.Enum<EaseCurve>());
        AddCoded("easedirection", ScriptCodecs.Enum<EaseDirection>());
    }

    public static IReadOnlyCollection<ScriptType> Known
    {
        get
        {
            lock (_lock)
                return _byName.Values.ToArray();
        }
    }

    // The generic families a name may open, such as "list" and "dict".
    public static IReadOnlyCollection<string> Families
    {
        get
        {
            lock (_lock)
                return _genericByName.Keys.ToArray();
        }
    }

    public static int FamilyArity(string family)
    {
        lock (_lock)
            return _genericByName.TryGetValue(family, out var open) ? open.GetGenericArguments().Length : 0;
    }

    // A reference type the datamodel hands scripts as an opaque handle, such as "slot" or "user".
    public static void RegisterHandle(string name, Type clrType)
    {
        if (!IsPlainName(name))
            throw new ArgumentException($"'{name}' is not a usable script type name", nameof(name));
        if (clrType.IsValueType || clrType.ContainsGenericParameters)
            throw new ArgumentException($"{clrType} cannot travel as a handle", nameof(clrType));
        Register(name, clrType, () => new ScriptType(name, clrType, ScriptTypeKind.Handle));
    }

    // A struct of numbers the datamodel wants scripts to carry by value.
    public static void RegisterStruct(string name, Type clrType)
    {
        if (!IsPlainName(name))
            throw new ArgumentException($"'{name}' is not a usable script type name", nameof(name));
        var leaves = LeavesOf(clrType);
        Register(name, clrType, () => new ScriptType(name, clrType, ScriptTypeKind.Vector, leaves));
    }

    public static void RegisterCoded(string name, ScriptValueCodec codec)
    {
        if (!IsPlainName(name))
            throw new ArgumentException($"'{name}' is not a usable script type name", nameof(name));
        Register(name, codec.ClrType, () => new ScriptType(name, codec.ClrType, ScriptTypeKind.Coded, codec: codec));
    }

    // An enum scripts may name, carried as one of its names.
    public static void RegisterEnum(string name, Type enumType)
    {
        if (!enumType.IsEnum)
            throw new ArgumentException($"{enumType} is not an enum", nameof(enumType));
        var codec = (ScriptValueCodec)typeof(ScriptCodecs).GetMethod(nameof(ScriptCodecs.Enum))!
            .MakeGenericMethod(enumType).Invoke(null, null)!;
        RegisterCoded(name, codec);
    }

    // A CLR type a library below LumoraCore names in its node signatures for a script type it cannot see,
    // like a shader node's texture port for the texture handle. Only the port type comes from it: a
    // stand-in never carries a value, so it maps one way, onto a type already registered.
    public static void RegisterStandIn(Type standIn, string name)
    {
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var type))
                throw new InvalidOperationException($"script type '{name}' is not registered");
            if (_byClr.TryGetValue(standIn, out var existing))
            {
                if (existing == type)
                    return;
                throw new InvalidOperationException($"{standIn} is already registered as '{existing.Name}'");
            }
            _byClr.Add(standIn, type);
        }
    }

    // A family of handle types, such as "field" for IField<T> or "dict" for a dictionary. Closed on
    // demand with arguments that are themselves script types, so "field<string>" works and
    // "field<Process>" can not be spelled.
    public static void RegisterGenericHandle(string name, Type openGeneric)
    {
        if (!IsPlainName(name))
            throw new ArgumentException($"'{name}' is not a usable script type name", nameof(name));
        if (!openGeneric.IsGenericTypeDefinition || openGeneric.IsValueType)
            throw new ArgumentException($"{openGeneric} is not a generic reference type", nameof(openGeneric));

        lock (_lock)
        {
            if (_genericByName.TryGetValue(name, out var existing))
            {
                if (existing == openGeneric)
                    return;
                throw new InvalidOperationException($"script type family '{name}' is already {existing}");
            }
            _genericByName.Add(name, openGeneric);
            _genericByClr.Add(openGeneric, name);
        }
    }

    public static bool TryGet(string name, out ScriptType type) => TryParse(name, out type);

    // Whether a port of type wanted takes what a port of type given carries: the same type, or a handle
    // whose object is one of wanted's, such as a JSON object where any JSON value goes. A handle crosses as
    // its number in the handle table either way, so nothing is converted. -xlinka
    public static bool Accepts(ScriptType wanted, ScriptType given)
        => wanted == given || (wanted.IsHandle && given.IsHandle && wanted.ClrType.IsAssignableFrom(given.ClrType));

    public static ScriptType Get(string name)
        => TryGet(name, out var type) ? type : throw new ArgumentException($"unknown script type '{name}'");

    public static bool TryGet(Type clrType, out ScriptType type)
    {
        lock (_lock)
        {
            if (_byClr.TryGetValue(clrType, out type!))
                return true;
        }

        if (clrType.IsGenericType && !clrType.ContainsGenericParameters)
        {
            string? family;
            lock (_lock)
                _genericByClr.TryGetValue(clrType.GetGenericTypeDefinition(), out family);
            if (family != null)
            {
                var arguments = clrType.GetGenericArguments();
                var names = new string[arguments.Length];
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (!TryGet(arguments[i], out var inner))
                    {
                        type = null!;
                        return false;
                    }
                    names[i] = inner.Name;
                }
                return TryParse($"{family}<{string.Join(",", names)}>", out type);
            }
        }

        type = null!;
        return false;
    }

    public static ScriptType Get(Type clrType)
        => TryGet(clrType, out var type) ? type : throw new ArgumentException($"{clrType} is not a script type");

    // Splits a list of type names at its top-level commas, so "dict<string,int>,float" is two names.
    // Null when the brackets do not balance.
    public static string[]? SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                if (--depth < 0)
                    return null;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        if (depth != 0)
            return null;
        parts.Add(text[start..].Trim());
        return parts.ToArray();
    }

    // A name no longer than MaxNameLength is what bounds how deep the parse goes, however deep it nests.
    private static bool TryParse(string name, out ScriptType type)
    {
        type = null!;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return false;

        lock (_lock)
        {
            if (_byName.TryGetValue(name, out type!))
                return true;
        }

        int open = name.IndexOf('<');
        if (open <= 0 || name[^1] != '>')
            return false;

        var family = name[..open];
        Type? openGeneric;
        lock (_lock)
            _genericByName.TryGetValue(family, out openGeneric);
        if (openGeneric == null)
            return false;

        var argumentNames = SplitTopLevel(name[(open + 1)..^1]);
        if (argumentNames == null || argumentNames.Length != openGeneric.GetGenericArguments().Length)
            return false;
        var arguments = new ScriptType[argumentNames.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!TryParse(argumentNames[i], out arguments[i]))
                return false;
        }

        Type closed;
        try
        {
            closed = openGeneric.MakeGenericType(arguments.Select(a => a.ClrType).ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }

        var canonical = $"{family}<{string.Join(",", arguments.Select(a => a.Name))}>";
        if (canonical.Length > MaxNameLength)
            return false;
        lock (_lock)
        {
            if (!_byName.TryGetValue(canonical, out type!))
            {
                type = new ScriptType(canonical, closed, ScriptTypeKind.Handle, family: family, arguments: arguments);
                _byName.Add(canonical, type);
                _byClr.TryAdd(closed, type);
            }
        }
        return true;
    }

    private static void Register(string name, Type clrType, Func<ScriptType> make)
    {
        lock (_lock)
        {
            if (_byName.TryGetValue(name, out var existing))
            {
                if (existing.ClrType == clrType)
                    return;
                throw new InvalidOperationException($"script type '{name}' is already {existing.ClrType}");
            }
            if (_byClr.TryGetValue(clrType, out var other))
                throw new InvalidOperationException($"{clrType} is already registered as '{other.Name}'");
            Add(make());
        }
    }

    private static ScriptType Add(ScriptType type)
    {
        _byName.Add(type.Name, type);
        _byClr.Add(type.ClrType, type);
        return type;
    }

    private static void AddStruct(string name, Type clrType)
        => Add(new ScriptType(name, clrType, ScriptTypeKind.Vector, LeavesOf(clrType)));

    private static void AddCoded(string name, ScriptValueCodec codec)
        => Add(new ScriptType(name, codec.ClrType, ScriptTypeKind.Coded, codec: codec));

    // Every number in a struct, nested structs walked in declaration order.
    private static ScriptLeaf[] LeavesOf(Type clrType)
    {
        if (!clrType.IsValueType || clrType.IsPrimitive || clrType.IsEnum)
            throw new InvalidOperationException($"{clrType} is not a struct");
        var leaves = new List<ScriptLeaf>();
        Walk(clrType, new List<FieldInfo>(), 0);
        if (leaves.Count == 0)
            throw new InvalidOperationException($"{clrType} holds no numbers");
        return leaves.ToArray();

        void Walk(Type type, List<FieldInfo> path, int depth)
        {
            if (depth > MaxStructDepth)
                throw new InvalidOperationException($"{clrType} nests too deep");
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(f => f.MetadataToken)
                .ToArray();
            foreach (var field in fields)
            {
                if (!field.IsPublic)
                    throw new InvalidOperationException($"{type}.{field.Name} is not public, so a script cannot carry {clrType}");
                path.Add(field);
                if (ScriptLeaf.LaneOf(field.FieldType) != null)
                    leaves.Add(new ScriptLeaf(path.ToArray()));
                else if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
                    Walk(field.FieldType, path, depth + 1);
                else
                    throw new InvalidOperationException($"{type}.{field.Name} is {field.FieldType}, which a lane cannot hold");
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private static bool IsPlainName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 32 || !char.IsAsciiLetterLower(name[0]))
            return false;
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }
        return true;
    }
}
