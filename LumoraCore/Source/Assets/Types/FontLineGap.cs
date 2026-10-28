// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;

namespace Lumora.Core.Assets;

// The extra space a font file asks for between lines (the line gap in its horizontal header), as a share of
// its em. The renderer's line height is ascent plus descent and leaves it out; text that measures lines by
// the font's full line height adds it back. Read once per file, straight from the table, so it costs nothing
// on the render side. A file that is not a plain TrueType or OpenType font has none. -xlinka
public static class FontLineGap
{
    private static readonly ConcurrentDictionary<string, float> Known = new(StringComparer.OrdinalIgnoreCase);

    public static float PerEm(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return 0f;
        return Known.GetOrAdd(path, Read);
    }

    private static readonly ConditionalWeakTable<Uri, StrongBox<float>> ByAddress = new();

    // The same answer kept against the font's address itself. Text asks for it per run of glyphs on every
    // line each time it measures, and writing the path out of the address was a new string every time. -xlinka
    public static float PerEm(Uri? address)
    {
        if (address == null)
            return 0f;
        bool local = address.IsAbsoluteUri && address.Scheme == "local";
        if (!address.IsFile && !local)
            return 0f;
        if (ByAddress.TryGetValue(address, out var known))
            return known.Value;
        // A font in the asset database is sealed on disk and read through it; a failed open is not cached,
        // since the file may only be on its way.
        float gap;
        if (local)
        {
            if (Engine.Current?.LocalDB?.OpenRead(address.OriginalString) is not { } stream)
                return 0f;
            gap = Known.GetOrAdd(address.OriginalString, _ => Read(stream));
            stream.Dispose();
        }
        else
        {
            gap = PerEm(address.LocalPath);
        }
        ByAddress.AddOrUpdate(address, new StrongBox<float>(gap));
        return gap;
    }

    private static float Read(string path)
    {
        try
        {
            return Read(File.OpenRead(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0f;
        }
    }

    private static float Read(Stream source)
    {
        try
        {
            using var file = source;
            Span<byte> header = stackalloc byte[12];
            if (file.Read(header) != 12)
                return 0f;
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(header);
            // TrueType outlines, the 'true' tag, or CFF ('OTTO').
            if (tag != 0x00010000 && tag != 0x74727565 && tag != 0x4F54544F)
                return 0f;
            int tables = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
            Span<byte> entry = stackalloc byte[16];
            long head = -1, hhea = -1;
            for (int i = 0; i < tables && (head < 0 || hhea < 0); i++)
            {
                if (file.Read(entry) != 16)
                    return 0f;
                uint name = BinaryPrimitives.ReadUInt32BigEndian(entry);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                if (name == 0x68656164)
                    head = offset;
                else if (name == 0x68686561)
                    hhea = offset;
            }
            if (head < 0 || hhea < 0)
                return 0f;
            Span<byte> field = stackalloc byte[2];
            file.Position = head + 18;
            if (file.Read(field) != 2)
                return 0f;
            int unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(field);
            file.Position = hhea + 8;
            if (file.Read(field) != 2)
                return 0f;
            int gap = BinaryPrimitives.ReadInt16BigEndian(field);
            return unitsPerEm > 0 && gap > 0 ? gap / (float)unitsPerEm : 0f;
        }
        catch (Exception)
        {
            return 0f;
        }
    }
}
