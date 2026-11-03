// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Lumora.Nexus.Storage;

// The front of a sealed file: magic, version, flags, chunk shift, a random nonce prefix and, for a file that
// carries its own key, that key wrapped under the master. The whole header is the associated data of every
// chunk, so no field of it can be swapped without every chunk failing. -xlinka
internal sealed class SealedHeader
{
    public const int BaseLength = 16;
    public const int WrapLength = 12 + SealedFile.KeySize + SealedFile.TagSize;
    public const byte Version = 1;
    public const byte FlagWrappedKey = 1;
    public const byte ChunkShift = 16;

    public readonly byte[] Bytes;
    public readonly byte[] Key;

    public int Length => Bytes.Length;

    private SealedHeader(byte[] bytes, byte[] key)
    {
        Bytes = bytes;
        Key = key;
    }

    // A fresh header. With no key given a random one is made and wrapped into the header.
    public static SealedHeader Create(byte[]? key)
    {
        bool wrap = key == null;
        key ??= SealedKeys.NewKey();
        if (key.Length != SealedFile.KeySize)
            throw new ArgumentException("A sealed file key is 32 bytes.", nameof(key));
        var bytes = new byte[wrap ? BaseLength + WrapLength : BaseLength];
        SealedFile.Magic.CopyTo(bytes);
        bytes[4] = Version;
        bytes[5] = wrap ? FlagWrappedKey : (byte)0;
        bytes[6] = ChunkShift;
        RandomNumberGenerator.Fill(bytes.AsSpan(8, 7));
        if (wrap)
        {
            var nonce = bytes.AsSpan(BaseLength, 12);
            RandomNumberGenerator.Fill(nonce);
            using var gcm = new AesGcm(SealedKeys.WrapKey, SealedFile.TagSize);
            gcm.Encrypt(nonce, key, bytes.AsSpan(BaseLength + 12, SealedFile.KeySize),
                bytes.AsSpan(BaseLength + 12 + SealedFile.KeySize, SealedFile.TagSize), bytes.AsSpan(0, BaseLength));
        }
        return new SealedHeader(bytes, key);
    }

    public const int MaxLength = BaseLength + WrapLength;

    // Reads and checks the header at the stream's start and leaves the stream at the first chunk. The given key
    // wins over a wrapped one; a file with neither cannot be opened.
    public static SealedHeader Read(Stream stream, byte[]? key)
    {
        var head = new byte[MaxLength];
        int got = stream.ReadAtLeast(head, BaseLength, throwOnEndOfStream: false);
        var header = Parse(head, got, key);
        stream.Position = header.Length;
        return header;
    }

    public static async System.Threading.Tasks.Task<SealedHeader> ReadAsync(Stream stream, byte[]? key, System.Threading.CancellationToken ct)
    {
        var head = new byte[MaxLength];
        int got = await stream.ReadAtLeastAsync(head, BaseLength, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var header = Parse(head, got, key);
        stream.Position = header.Length;
        return header;
    }

    public static SealedHeader Parse(byte[] head, int count, byte[]? key)
    {
        if (count < BaseLength || !SealedFile.HasMagic(head))
            throw new InvalidDataException("Not a sealed file.");
        if (head[4] != Version || head[6] != ChunkShift)
            throw new InvalidDataException($"Sealed file version {head[4]} / chunk shift {head[6]} is not one this build reads.");
        bool wrapped = (head[5] & FlagWrappedKey) != 0;
        if (!wrapped)
        {
            if (key == null)
                throw new CryptographicException("Sealed file needs its key and none was given.");
            return new SealedHeader(head.AsSpan(0, BaseLength).ToArray(), key);
        }

        if (count < MaxLength)
            throw new InvalidDataException("Sealed file header is cut short.");
        var bytes = head.AsSpan(0, MaxLength).ToArray();
        if (key == null)
        {
            key = new byte[SealedFile.KeySize];
            using var gcm = new AesGcm(SealedKeys.WrapKey, SealedFile.TagSize);
            gcm.Decrypt(bytes.AsSpan(BaseLength, 12), bytes.AsSpan(BaseLength + 12, SealedFile.KeySize),
                bytes.AsSpan(BaseLength + 12 + SealedFile.KeySize, SealedFile.TagSize), key, bytes.AsSpan(0, BaseLength));
        }
        return new SealedHeader(bytes, key);
    }

    // Nonce of a chunk: the header's seven random bytes, the chunk's index, and whether it is the last. The last
    // flag is what makes a file cut short at a chunk boundary fail instead of reading as a shorter file.
    public void Nonce(long index, bool final, Span<byte> nonce)
    {
        if ((ulong)index > uint.MaxValue)
            throw new InvalidDataException("Sealed file is too long.");
        Bytes.AsSpan(8, 7).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(7, 4), (uint)index);
        nonce[11] = final ? (byte)1 : (byte)0;
    }
}
