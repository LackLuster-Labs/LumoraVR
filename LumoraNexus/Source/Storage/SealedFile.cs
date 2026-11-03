// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Storage;

// SEALED FILES
//
// Cache files on this machine are kept as [header][chunk 0]...[chunk n-1], every chunk 64 KiB of plaintext
// (the last one shorter) sealed with AES-256-GCM under a key of its own and followed by its 16-byte tag. A
// chunk can be opened on its own, so a video or a big mesh is read and seeked a chunk at a time and never has
// to be decrypted whole. The plaintext length is not stored: it follows from the file length, and the last
// chunk's nonce carries a flag, so a file cut short or padded fails instead of reading as different content.
//
// The key is either kept by the caller (the asset database files it in its own encrypted index) or wrapped
// into the header under the master key, for the caches that index themselves from the folder. -xlinka
public static class SealedFile
{
    public const int ChunkSize = 1 << SealedHeader.ChunkShift;
    public const int TagSize = 16;
    public const int KeySize = 32;
    public const int SealedChunkSize = ChunkSize + TagSize;
    // Header of a file whose key is kept elsewhere, and of one that carries its key wrapped.
    public const int HeaderLength = SealedHeader.BaseLength;
    public const int KeyedHeaderLength = SealedHeader.MaxLength;

    // Chunks read per IO when a whole file is read: big enough that the disk sees few requests, small enough
    // to stay a pooled buffer.
    private const int ChunksPerRead = 16;

    internal static ReadOnlySpan<byte> Magic => "LVC1"u8;

    public static bool HasMagic(ReadOnlySpan<byte> head)
        => head.Length >= 4 && head[0] == 'L' && head[1] == 'V' && head[2] == 'C' && head[3] == '1';

    public static bool IsSealed(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            Span<byte> head = stackalloc byte[4];
            return file.Read(head) == 4 && HasMagic(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static long PlainLength(long fileLength, int headerLength)
    {
        long body = fileLength - headerLength;
        if (body < TagSize)
            throw new InvalidDataException("Sealed file is shorter than one chunk.");
        long chunks = (body + SealedChunkSize - 1) / SealedChunkSize;
        long last = body - (chunks - 1) * SealedChunkSize;
        if (last < TagSize)
            throw new InvalidDataException("Sealed file ends inside a chunk tag.");
        return (chunks - 1) * ChunkSize + (last - TagSize);
    }

    // What a plaintext of this length takes on disk, header included.
    public static long SealedLength(long plainLength, bool carriesKey)
    {
        long chunks = plainLength == 0 ? 1 : (plainLength + ChunkSize - 1) / ChunkSize;
        return (carriesKey ? SealedHeader.BaseLength + SealedHeader.WrapLength : SealedHeader.BaseLength) + plainLength + chunks * TagSize;
    }

    // Opens a sealed file for reading. A null key means the header carries one.
    public static SealedReadStream OpenRead(string path, byte[]? key = null)
    {
        var file = OpenFile(path, FileOptions.RandomAccess);
        try
        {
            return new SealedReadStream(file, SealedHeader.Read(file, key), leaveOpen: false);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // A sealed file opened decrypting, anything else opened as it is. For readers that take what the caches
    // hold as well as plain files the user picked.
    public static Stream OpenAny(string path, byte[]? key = null)
    {
        var file = OpenFile(path, FileOptions.RandomAccess);
        try
        {
            var head = new byte[SealedHeader.MaxLength];
            int got = file.ReadAtLeast(head, SealedHeader.BaseLength, throwOnEndOfStream: false);
            if (got < SealedHeader.BaseLength || !HasMagic(head))
            {
                file.Position = 0;
                return file;
            }
            var header = SealedHeader.Parse(head, got, key);
            file.Position = header.Length;
            return new SealedReadStream(file, header, leaveOpen: false);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // Creates (or replaces) a sealed file. A null key makes a random one and wraps it into the header.
    public static SealedWriteStream Create(string path, byte[]? key = null)
    {
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.None);
        try
        {
            return new SealedWriteStream(file, key, leaveOpen: false);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data, byte[]? key = null)
    {
        using var stream = Create(path, key);
        stream.Write(data);
    }

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, byte[]? key = null, CancellationToken ct = default)
    {
        await using var stream = new SealedWriteStream(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous), key, leaveOpen: false);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.CompleteAsync(ct).ConfigureAwait(false);
    }

    public static byte[] ReadAllBytes(string path, byte[]? key = null)
    {
        using var file = OpenFile(path, FileOptions.SequentialScan);
        var header = SealedHeader.Read(file, key);
        var plain = AllocatePlain(file.Length, header);
        using var gcm = new AesGcm(header.Key, TagSize);
        var buffer = ArrayPool<byte>.Shared.Rent(SealedChunkSize * ChunksPerRead);
        try
        {
            long chunks = ChunkCount(file.Length, header);
            long index = 0;
            while (index < chunks)
            {
                int batch = (int)Math.Min(ChunksPerRead, chunks - index);
                int want = BatchBytes(file.Length, header, index, batch);
                file.ReadExactly(buffer, 0, want);
                OpenBatch(gcm, header, buffer, want, index, chunks, plain);
                index += batch;
            }
            return plain;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // The whole plaintext, read with async IO so no pool thread sits on the disk. Decrypts as each batch lands,
    // straight into the array that is returned.
    public static async Task<byte[]> ReadAllBytesAsync(string path, byte[]? key = null, CancellationToken ct = default)
    {
        await using var file = OpenFile(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = await SealedHeader.ReadAsync(file, key, ct).ConfigureAwait(false);
        var plain = AllocatePlain(file.Length, header);
        using var gcm = new AesGcm(header.Key, TagSize);
        var buffer = ArrayPool<byte>.Shared.Rent(SealedChunkSize * ChunksPerRead);
        try
        {
            long chunks = ChunkCount(file.Length, header);
            long index = 0;
            while (index < chunks)
            {
                int batch = (int)Math.Min(ChunksPerRead, chunks - index);
                int want = BatchBytes(file.Length, header, index, batch);
                await file.ReadExactlyAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                OpenBatch(gcm, header, buffer, want, index, chunks, plain);
                index += batch;
            }
            return plain;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Bytes on disk, sealed or not: a plain file comes back as it is.
    public static async Task<byte[]> ReadAnyAsync(string path, byte[]? key = null, CancellationToken ct = default)
    {
        if (!IsSealed(path))
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return await ReadAllBytesAsync(path, key, ct).ConfigureAwait(false);
    }

    internal static FileStream OpenFile(string path, FileOptions options)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, options);

    internal static long ChunkCount(long fileLength, SealedHeader header)
    {
        long body = fileLength - header.Length;
        if (body < TagSize)
            throw new InvalidDataException("Sealed file is shorter than one chunk.");
        return (body + SealedChunkSize - 1) / SealedChunkSize;
    }

    private static byte[] AllocatePlain(long fileLength, SealedHeader header)
    {
        long length = PlainLength(fileLength, header.Length);
        if (length > Array.MaxLength)
            throw new InvalidDataException("Sealed file is too big to read whole.");
        return length == 0 ? Array.Empty<byte>() : new byte[length];
    }

    private static int BatchBytes(long fileLength, SealedHeader header, long index, int batch)
    {
        long start = header.Length + index * SealedChunkSize;
        return (int)Math.Min((long)batch * SealedChunkSize, fileLength - start);
    }

    private static void OpenBatch(AesGcm gcm, SealedHeader header, byte[] buffer, int length, long first, long chunks, byte[] plain)
    {
        Span<byte> nonce = stackalloc byte[12];
        int at = 0;
        long index = first;
        while (at < length)
        {
            int sealedLength = Math.Min(SealedChunkSize, length - at);
            int plainLength = sealedLength - TagSize;
            header.Nonce(index, index == chunks - 1, nonce);
            gcm.Decrypt(nonce, buffer.AsSpan(at, plainLength), buffer.AsSpan(at + plainLength, TagSize),
                plain.AsSpan((int)(index * ChunkSize), plainLength), header.Bytes);
            at += sealedLength;
            index++;
        }
    }
}
