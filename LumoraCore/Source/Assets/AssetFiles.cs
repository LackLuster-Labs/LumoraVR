// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Persistence;
using Lumora.Nexus.Storage;

namespace Lumora.Core.Assets;

// Opening what the caches keep, for code that has a path rather than an address. A file the asset database
// wrote opens with the key in its record; a file a self-indexing cache wrote carries its own key; anything
// else (a file the user picked, one from before sealing) comes back exactly as it is on disk. No path here
// ever leads to a plaintext copy on disk. -xlinka
public static class AssetFiles
{
    public static Stream Open(string path, byte[]? key = null)
    {
        LocalEncryption.WireSealedStorage();
        key ??= Engine.Current?.LocalDB?.KeyForPath(path);
        var stream = SealedFile.OpenAny(path, key);
        if (stream is SealedReadStream || !IsWholeBlob(stream))
            return stream;
        using (stream)
        {
            var all = new byte[stream.Length];
            stream.ReadExactly(all);
            return new MemoryStream(LocalEncryption.Decrypt(all), writable: false);
        }
    }

    public static async Task<byte[]> ReadAllBytesAsync(string path, byte[]? key = null, CancellationToken ct = default)
    {
        LocalEncryption.WireSealedStorage();
        key ??= Engine.Current?.LocalDB?.KeyForPath(path);
        if (key != null)
        {
            try
            {
                return await SealedFile.ReadAllBytesAsync(path, key, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException) when (!SealedFile.IsSealed(path))
            {
            }
        }
        else if (SealedFile.IsSealed(path))
        {
            return await SealedFile.ReadAllBytesAsync(path, null, ct).ConfigureAwait(false);
        }
        return LocalEncryption.Decrypt(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
    }

    public static byte[] ReadAllBytes(string path, byte[]? key = null)
    {
        LocalEncryption.WireSealedStorage();
        key ??= Engine.Current?.LocalDB?.KeyForPath(path);
        if (SealedFile.IsSealed(path))
            return SealedFile.ReadAllBytes(path, key);
        return LocalEncryption.Decrypt(File.ReadAllBytes(path));
    }

    // A whole-file blob from the earlier at-rest format (one GCM seal over the file under the master key).
    private static bool IsWholeBlob(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 4)
            return false;
        Span<byte> head = stackalloc byte[4];
        long at = stream.Position;
        int got = stream.Read(head);
        stream.Position = at;
        return got == 4 && LocalEncryption.IsEncrypted(head.ToArray());
    }
}
