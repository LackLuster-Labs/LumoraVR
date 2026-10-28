// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

// The names a model file gives its materials, read without importing it, so the import dialog can offer a
// material kind per material before anything is parsed. The names are the ones the importer later sees.
//
// A full parse of a big FBX is seconds and gigabytes, the exact cost the import works hard to pay only once, so
// this reads only where each format keeps its material list: a glTF's JSON, the FBX object table, an OBJ's
// usemtl lines. Any other format answers null and the one choice covers every material. -xlinka
public static class ModelMaterialNames
{
    // A cap on what is read looking for names, far above any real material table.
    private const long MaxBytes = 512L * 1024 * 1024;

    public static bool CanRead(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".gltf" or ".glb" or ".vrm" or ".fbx" or ".obj";

    // Distinct names in file order, or null when the format keeps them nowhere cheap to read.
    public static List<string>? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes)
                return null;
            var names = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".gltf" => FromGltfJson(File.ReadAllBytes(path)),
                ".glb" or ".vrm" => FromGlb(path),
                ".fbx" => FromFbx(File.ReadAllBytes(path)),
                ".obj" => FromObj(path),
                _ => null,
            };
            if (names == null)
                return null;
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    distinct.Add(name);
            }
            return distinct;
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModelMaterialNames: could not list the materials of '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    private static List<string> FromGltfJson(ReadOnlySpan<byte> json)
    {
        var names = new List<string>();
        using var doc = JsonDocument.Parse(json.ToArray());
        if (doc.RootElement.TryGetProperty("materials", out var materials) && materials.ValueKind == JsonValueKind.Array)
        {
            foreach (var material in materials.EnumerateArray())
            {
                if (material.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    names.Add(name.GetString() ?? "");
            }
        }
        return names;
    }

    // Header, then the JSON chunk first: nothing past it is read.
    private static List<string>? FromGlb(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[20];
        if (stream.Read(header) != 20 || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x46546C67)
            return null;
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16)) != 0x4E4F534A || length <= 0 || length > stream.Length)
            return null;
        var json = new byte[length];
        stream.ReadExactly(json);
        return FromGltfJson(json);
    }

    private static List<string> FromObj(string path)
    {
        var names = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.StartsWith("usemtl ", StringComparison.Ordinal))
                names.Add(trimmed.Slice(7).Trim().ToString());
        }
        return names;
    }

    private static readonly Regex AsciiFbxMaterial = new("^\\s*Material:\\s*[^,]*,\\s*\"Material::([^\"]*)\"", RegexOptions.Multiline);

    private static List<string>? FromFbx(byte[] data)
    {
        if (data.Length >= 27 && data.AsSpan(0, 23).SequenceEqual("Kaydara FBX Binary  \0\x1a\0"u8))
            return FromBinaryFbx(data);
        var names = new List<string>();
        foreach (Match match in AsciiFbxMaterial.Matches(Encoding.UTF8.GetString(data)))
            names.Add(match.Groups[1].Value);
        return names;
    }

    // Objects > Material nodes; the second property is "Name\0\x01Material".
    private static List<string>? FromBinaryFbx(byte[] data)
    {
        bool wide = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(23)) >= 7500;
        var names = new List<string>();
        foreach (var top in Nodes(data, 27, data.Length, wide))
        {
            if (!top.Is(data, "Objects"))
                continue;
            foreach (var node in Nodes(data, top.Children, top.End, wide))
            {
                if (!node.Is(data, "Material"))
                    continue;
                var name = SecondString(data, node);
                if (name != null)
                    names.Add(name);
            }
        }
        return names;
    }

    private readonly record struct FbxNode(int End, int Properties, int PropertyCount, int Children, int Name, int NameLength)
    {
        public bool Is(byte[] data, string name)
            => NameLength == name.Length && Encoding.ASCII.GetString(data, Name, NameLength) == name;
    }

    private static IEnumerable<FbxNode> Nodes(byte[] data, int start, int end, bool wide)
    {
        int at = start;
        int headerSize = wide ? 25 : 13;
        while (at + headerSize <= end)
        {
            long nodeEnd = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            if (nodeEnd == 0)
                yield break;
            long count = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at + 8)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            long listLength = wide ? (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at + 16)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8));
            int nameLength = data[at + headerSize - 1];
            int name = at + headerSize;
            int properties = name + nameLength;
            if (nodeEnd > end || nodeEnd <= at || properties + listLength > nodeEnd)
                yield break;
            yield return new FbxNode((int)nodeEnd, properties, (int)count, (int)(properties + listLength), name, nameLength);
            at = (int)nodeEnd;
        }
    }

    private static string? SecondString(byte[] data, FbxNode node)
    {
        int at = node.Properties;
        for (int i = 0; i < node.PropertyCount && at < node.Children; i++)
        {
            char type = (char)data[at++];
            switch (type)
            {
                case 'S':
                case 'R':
                    int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    at += 4;
                    if (type == 'S' && i == 1)
                    {
                        var raw = Encoding.UTF8.GetString(data, at, length);
                        int cut = raw.IndexOf('\0');
                        return cut >= 0 ? raw.Substring(0, cut) : raw;
                    }
                    at += length;
                    break;
                case 'Y': at += 2; break;
                case 'C': at += 1; break;
                case 'I': case 'F': at += 4; break;
                case 'D': case 'L': at += 8; break;
                case 'f': case 'd': case 'l': case 'i': case 'b':
                    at += 8;
                    at += 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    break;
                default:
                    return null;
            }
        }
        return null;
    }
}
