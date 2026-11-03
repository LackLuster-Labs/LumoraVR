// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Security.Cryptography;

namespace Lumora.Nexus.Storage;

// A sealed file read as its plaintext, seekable. One chunk is decrypted at a time and kept until a read leaves
// it, so a decoder walking a file costs one decrypt per chunk and a seek costs one chunk. One reader at a time.
public sealed class SealedReadStream : Stream
{
    private readonly Stream _file;
    private readonly SealedHeader _header;
    private readonly bool _leaveOpen;
    private readonly AesGcm _gcm;
    private readonly long _length;
    private readonly long _chunks;
    private readonly byte[] _sealed = new byte[SealedFile.SealedChunkSize];
    private readonly byte[] _plain = new byte[SealedFile.ChunkSize];
    private long _loaded = -1;
    private int _loadedLength;
    private long _position;
    private bool _disposed;

    internal SealedReadStream(Stream file, SealedHeader header, bool leaveOpen)
    {
        _file = file;
        _header = header;
        _leaveOpen = leaveOpen;
        _gcm = new AesGcm(header.Key, SealedFile.TagSize);
        long fileLength = file.Length;
        _length = SealedFile.PlainLength(fileLength, header.Length);
        _chunks = SealedFile.ChunkCount(fileLength, header);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            long index = _position / SealedFile.ChunkSize;
            if (index != _loaded)
                Load(index);
            int within = (int)(_position - index * SealedFile.ChunkSize);
            int take = Math.Min(buffer.Length, _loadedLength - within);
            if (take <= 0)
                break;
            _plain.AsSpan(within, take).CopyTo(buffer);
            buffer = buffer[take..];
            _position += take;
            total += take;
        }
        return total;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    private void Load(long index)
    {
        long at = _header.Length + index * SealedFile.SealedChunkSize;
        int sealedLength = (int)Math.Min(SealedFile.SealedChunkSize, _file.Length - at);
        _file.Position = at;
        _file.ReadExactly(_sealed, 0, sealedLength);
        int plainLength = sealedLength - SealedFile.TagSize;
        Span<byte> nonce = stackalloc byte[12];
        _header.Nonce(index, index == _chunks - 1, nonce);
        _loaded = -1;
        _gcm.Decrypt(nonce, _sealed.AsSpan(0, plainLength), _sealed.AsSpan(plainLength, SealedFile.TagSize),
            _plain.AsSpan(0, plainLength), _header.Bytes);
        _loaded = index;
        _loadedLength = plainLength;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _gcm.Dispose();
            if (!_leaveOpen)
                _file.Dispose();
            Array.Clear(_plain);
        }
        base.Dispose(disposing);
    }
}
