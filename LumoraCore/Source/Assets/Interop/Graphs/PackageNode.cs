// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;
using Lumora.Core.Persistence;
using Lumora.Script;

namespace Lumora.Core.Assets.Interop;

// One node of a package's node graph as the package stored it: the component's id, its class with any nested
// part ("FieldDriveBase+Proxy") and no namespace, its generic arguments by their short names, its members,
// and the slot it sat on. -xlinka
internal sealed class PackageNode
{
    public string Id { get; }
    public string Class { get; }
    public IReadOnlyList<string> Args { get; }
    public DataTreeDictionary Data { get; }
    public string SlotId { get; }

    public PackageNode(string id, string encoded, DataTreeDictionary data, string slotId)
    {
        Id = id;
        Data = data;
        SlotId = slotId;
        (Class, Args) = Parse(encoded);
    }

    public override string ToString() => $"{Class}{(Args.Count > 0 ? "<" + string.Join(",", Args) + ">" : "")} {Id}";

    public bool Has(string member) => Data.TryGetNode(member) is DataTreeDictionary;

    // What a member stores under Data.
    public DataTreeNode? Stored(string member) => (Data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data");

    public object? Value(string member) => (Stored(member) as DataTreeValue)?.Value;

    // The id a reference member holds, or null when it holds nothing.
    public string? Ref(string member) => Value(member) as string is { Length: > 0 } id ? id : null;

    // The ids a list member holds, one per entry, null for an empty entry.
    public IReadOnlyList<string?> Refs(string member)
    {
        var ids = new List<string?>();
        if (Stored(member) is DataTreeList list)
        {
            foreach (var item in list)
                ids.Add(((item as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value as string is { Length: > 0 } id ? id : null);
        }
        return ids;
    }

    // Our script type for generic argument i, or null when there is none or it has no counterpart.
    public string? Arg(int i) => i < Args.Count ? OurType(Args[i]) : null;

    // "[Asm]Name.Space.Class<[Asm]A,[Asm]B<C>>+Nested" -> ("Class+Nested", ["A", "B<C>"]).
    public static (string Class, string[] Args) Parse(string encoded)
    {
        string text = StripAssemblies(encoded);
        int open = text.IndexOf('<');
        int plus = text.IndexOf('+');
        int nameEnd = open >= 0 && (plus < 0 || open < plus) ? open : plus >= 0 ? plus : text.Length;
        string name = text[..nameEnd];
        var args = new List<string>();
        int rest = nameEnd;
        if (open == nameEnd && open >= 0)
        {
            int close = Matching(text, open);
            foreach (var arg in SplitTop(text[(open + 1)..close]))
                args.Add(ShortType(arg));
            rest = close + 1;
        }
        string nested = rest < text.Length ? text[rest..] : string.Empty;
        int dot = name.LastIndexOf('.');
        return ((dot >= 0 ? name[(dot + 1)..] : name) + nested, args.ToArray());
    }

    // A type argument by its short name, keeping its own arguments: "Name.Space.IValue<Name.Space.float>" -> "IValue<float>".
    public static string ShortType(string encoded)
    {
        string text = StripAssemblies(encoded).Trim();
        int open = text.IndexOf('<');
        string name = open >= 0 ? text[..open] : text;
        int dot = name.LastIndexOf('.');
        name = dot >= 0 ? name[(dot + 1)..] : name;
        if (open < 0)
            return name;
        int close = Matching(text, open);
        var inner = new List<string>();
        foreach (var arg in SplitTop(text[(open + 1)..close]))
            inner.Add(ShortType(arg));
        return name + "<" + string.Join(",", inner) + ">";
    }

    // Our script type for one of the package's types.
    public static string? OurType(string their)
    {
        string? ours = their switch
        {
            "float" or "double" or "int" or "long" or "bool" or "string" or "byte" or "sbyte" or "short" or "ushort" or "uint"
                or "ulong" or "char" or "float2" or "float3" or "float4" or "int2" or "int3" or "int4" or "bool2" or "bool3"
                or "bool4" or "floatQ" or "float2x2" or "float3x3" or "float4x4" => their,
            "colorX" or "color" => "colorHDR",
            "Slot" => "slot",
            "User" => "user",
            "Uri" => "uri",
            "DateTime" => "datetime",
            "TimeSpan" => "timespan",
            "Chirality" => "chirality",
            // Same names and numbers as ours, NONE through END.
            "BodyNode" => "bodyNode",
            "HeadOutputDevice" => "headOutputDevice",
            "ColorProfile" => "colorProfile",
            "AssetLoadState" => "assetLoadState",
            "JoinRequestHandle" => "joinrequest",
            "ISyncRef" => "reference",
            "AudioClip" or "IAssetProvider<AudioClip>" => "audioclip",
            "ITexture2D" or "IAssetProvider<ITexture2D>" or "Texture2D" or "IAssetProvider<Texture2D>" => "texture",
            "Mesh" or "IAssetProvider<Mesh>" => "mesh",
            "Material" or "IAssetProvider<Material>" => "material",
            _ => null,
        };
        if (ours == null && (their.StartsWith("IValue<", StringComparison.Ordinal) || their.StartsWith("IField<", StringComparison.Ordinal))
            && OurType(their[(their.IndexOf('<') + 1)..^1]) is { } inner)
            ours = $"field<{inner}>";
        // A plain array of values is a list here.
        if (ours == null && their.StartsWith("RawArray<", StringComparison.Ordinal) && OurType(their[9..^1]) is { } item)
            ours = $"list<{item}>";
        return ours != null && ScriptTypes.TryGet(ours, out _) ? ours : null;
    }

    private static string StripAssemblies(string text)
    {
        var sb = new StringBuilder(text.Length);
        int depth = 0;
        foreach (char c in text)
        {
            if (c == '[')
                depth++;
            else if (c == ']')
                depth--;
            else if (depth == 0)
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static int Matching(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '<')
                depth++;
            else if (text[i] == '>' && --depth == 0)
                return i;
        }
        return text.Length - 1;
    }

    private static IEnumerable<string> SplitTop(string text)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '<')
                depth++;
            else if (text[i] == '>')
                depth--;
            else if (text[i] == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }
}
