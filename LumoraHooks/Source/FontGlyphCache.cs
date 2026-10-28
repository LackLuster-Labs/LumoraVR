// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Lumora.Godot.Hooks;

// GLYPHS ARE MADE ONCE PER FONT.
//
// Turning a glyph into a distance field is the slow part of a font, and every font hook made its own: a world's
// panels drew again the letters the dash had already drawn, and the next session drew them all once more. Most of
// a big world's first panel layout went on it, a single frame of four hundred milliseconds. Each glyph a font makes
// is kept here under the font and its settings, pixels and metrics both, for every hook of that font this session,
// and written to the cache folder so the next session starts with it. A file that does not read back exactly is
// thrown away and the glyphs are made again. Main thread, apart from the write. -xlinka
internal static class FontGlyphCache
{
    internal const byte Drawn = 0;
    internal const byte Blank = 1;
    internal const byte Missing = 2;

    internal sealed class Glyph
    {
        public byte Kind;
        public float Advance;
        public float OffsetX;
        public float OffsetY;
        // The box the glyph takes in an atlas, and where inside it the font's pixels sit.
        public int Width;
        public int Height;
        public int RegionX;
        public int RegionY;
        public int RegionWidth;
        public int RegionHeight;
        public byte[]? Pixels;
    }

    private sealed class FontGlyphs
    {
        public readonly string Key;
        public readonly string File;
        public readonly Dictionary<int, Glyph> Glyphs = new();
        public bool Dirty;
        public bool Writing;
        // A file from before sealing that was read in place of this one, deleted once the sealed copy is down.
        public string? Legacy;

        public FontGlyphs(string key, string file)
        {
            Key = key;
            File = file;
        }
    }

    private const int FileVersion = 1;
    private const int SaveDelayMs = 3000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, FontGlyphs> Fonts = new();

    // What a font's glyphs depend on: where it came from (and, for a file, which version of it), the size and
    // field settings it is drawn at, the renderer that draws it and the build, which carries the bundled fonts.
    public static string KeyFor(string source, int rasterSize, int pixelRange, bool distanceField)
    {
        string identity = source;
        try
        {
            if (System.IO.File.Exists(source))
            {
                var info = new FileInfo(source);
                identity = $"{source}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
        }
        catch (Exception)
        {
        }
        var version = global::Godot.Engine.GetVersionInfo();
        return $"{identity}|{rasterSize}|{pixelRange}|{distanceField}|{version["string"]}|{Lumora.Core.EngineVersion.VersionString}";
    }

    public static bool TryGet(string key, int codepoint, out Glyph glyph)
    {
        lock (Gate)
        {
            return Open(key).Glyphs.TryGetValue(codepoint, out glyph!);
        }
    }

    public static void Put(string key, int codepoint, Glyph glyph)
    {
        FontGlyphs font;
        lock (Gate)
        {
            font = Open(key);
            if (font.Glyphs.ContainsKey(codepoint))
                return;
            font.Glyphs[codepoint] = glyph;
            font.Dirty = true;
            if (font.Writing)
                return;
            font.Writing = true;
        }
        _ = WriteLater(font);
    }

    private static FontGlyphs Open(string key)
    {
        if (Fonts.TryGetValue(key, out var font))
            return font;
        Lumora.Core.Persistence.LocalEncryption.WireSealedStorage();
        string folder = Folder;
        string file = Path.Combine(folder, Lumora.Nexus.Storage.SealedKeys.NameFor(NameScope, key));
        font = new FontGlyphs(key, file);
        Fonts[key] = font;
        string from = file;
        try
        {
            if (!System.IO.File.Exists(from))
            {
                from = Path.Combine(folder, Hash(key) + LegacyExtension);
                if (!System.IO.File.Exists(from))
                    return font;
                font.Legacy = from;
                font.Dirty = true;
            }
            Read(font, from);
            if (font.Legacy != null && !font.Writing)
            {
                font.Writing = true;
                _ = WriteLater(font);
            }
        }
        catch (Exception ex)
        {
            font.Glyphs.Clear();
            font.Legacy = null;
            Lumora.Core.Logging.Logger.Warn($"FontGlyphCache: dropped an unreadable cache file ({ex.Message})");
            try { System.IO.File.Delete(from); } catch (Exception) { }
        }
        return font;
    }

    private const string NameScope = "glyphs";
    private const string LegacyExtension = ".glyphs";

    private static string Folder => Lumora.Core.Persistence.PathResolver.Resolve("Cache", "fonts");

    // The font hook is up before any world loads, so this is in place long before the sealing pass runs.
    static FontGlyphCache() => RegisterSealing();

    // Older glyph files carry a hash of their key in the name; the key itself is the first thing inside.
    private static void RegisterSealing()
    {
        Lumora.Core.Assets.CacheSealing.Add("glyph", async (pace, ct) =>
        {
            int count = 0;
            long bytes = 0;
            string folder = Folder;
            if (!Directory.Exists(folder))
                return (0, 0);
            foreach (var legacy in Directory.GetFiles(folder, "*" + LegacyExtension))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var raw = await System.IO.File.ReadAllBytesAsync(legacy, ct).ConfigureAwait(false);
                    string key;
                    using (var unzip = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress))
                    using (var reader = new BinaryReader(unzip))
                    {
                        if (reader.ReadInt32() != FileVersion)
                        {
                            System.IO.File.Delete(legacy);
                            continue;
                        }
                        key = reader.ReadString();
                    }
                    string target = Path.Combine(folder, Lumora.Nexus.Storage.SealedKeys.NameFor(NameScope, key));
                    if (!System.IO.File.Exists(target))
                    {
                        string temp = target + ".tmp";
                        await Lumora.Nexus.Storage.SealedFile.WriteAllBytesAsync(temp, raw, null, ct).ConfigureAwait(false);
                        System.IO.File.Move(temp, target, overwrite: true);
                    }
                    System.IO.File.Delete(legacy);
                    count++;
                    bytes += raw.LongLength;
                    await pace(raw.LongLength).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                }
            }
            return (count, bytes);
        });
    }

    private static string Hash(string key)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }

    private static void Read(FontGlyphs font, string path)
    {
        using var stream = Lumora.Nexus.Storage.SealedFile.OpenAny(path);
        using var unzip = new DeflateStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(unzip);
        if (reader.ReadInt32() != FileVersion || reader.ReadString() != font.Key)
            throw new InvalidDataException("another font or version");
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            int codepoint = reader.ReadInt32();
            var glyph = new Glyph
            {
                Kind = reader.ReadByte(),
                Advance = reader.ReadSingle(),
                OffsetX = reader.ReadSingle(),
                OffsetY = reader.ReadSingle(),
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32(),
                RegionX = reader.ReadInt32(),
                RegionY = reader.ReadInt32(),
                RegionWidth = reader.ReadInt32(),
                RegionHeight = reader.ReadInt32(),
            };
            int length = reader.ReadInt32();
            if (length > 0)
            {
                if (length != glyph.RegionWidth * glyph.RegionHeight * 4)
                    throw new InvalidDataException("glyph size");
                glyph.Pixels = reader.ReadBytes(length);
                if (glyph.Pixels.Length != length)
                    throw new EndOfStreamException();
            }
            font.Glyphs[codepoint] = glyph;
        }
    }

    // Written a few seconds after the last new glyph, from a copy, off the main thread.
    private static async Task WriteLater(FontGlyphs font)
    {
        try
        {
            await Task.Delay(SaveDelayMs).ConfigureAwait(false);
            KeyValuePair<int, Glyph>[] glyphs;
            lock (Gate)
            {
                font.Dirty = false;
                glyphs = new KeyValuePair<int, Glyph>[font.Glyphs.Count];
                ((ICollection<KeyValuePair<int, Glyph>>)font.Glyphs).CopyTo(glyphs, 0);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(font.File)!);
            string temp = font.File + ".tmp";
            using (var stream = Lumora.Nexus.Storage.SealedFile.Create(temp))
            using (var zip = new DeflateStream(stream, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(zip))
            {
                writer.Write(FileVersion);
                writer.Write(font.Key);
                writer.Write(glyphs.Length);
                foreach (var (codepoint, glyph) in glyphs)
                {
                    writer.Write(codepoint);
                    writer.Write(glyph.Kind);
                    writer.Write(glyph.Advance);
                    writer.Write(glyph.OffsetX);
                    writer.Write(glyph.OffsetY);
                    writer.Write(glyph.Width);
                    writer.Write(glyph.Height);
                    writer.Write(glyph.RegionX);
                    writer.Write(glyph.RegionY);
                    writer.Write(glyph.RegionWidth);
                    writer.Write(glyph.RegionHeight);
                    writer.Write(glyph.Pixels?.Length ?? 0);
                    if (glyph.Pixels != null)
                        writer.Write(glyph.Pixels);
                }
            }
            System.IO.File.Move(temp, font.File, overwrite: true);
            string? legacy;
            lock (Gate)
            {
                legacy = font.Legacy;
                font.Legacy = null;
            }
            if (legacy != null)
            {
                try { System.IO.File.Delete(legacy); } catch (Exception) { }
            }
        }
        catch (Exception ex)
        {
            Lumora.Core.Logging.Logger.Warn($"FontGlyphCache: could not write the cache ({ex.Message})");
        }
        finally
        {
            bool again;
            lock (Gate)
            {
                again = font.Dirty;
                font.Writing = again;
            }
            if (again)
                _ = WriteLater(font);
        }
    }
}
