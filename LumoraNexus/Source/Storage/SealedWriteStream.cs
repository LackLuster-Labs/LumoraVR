// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Storage;

// Plaintext in, a sealed file out, front to back. A full chunk is held back until one more byte arrives, so
// the chunk that ends the file is always the one sealed as last, however the length falls. Disposing seals
// the last chunk; a writer that failed halfway leaves a file its caller deletes anyway. -xlinka
public sealed class SealedWriteStream : Stream
{
    private readonly Stream _file;
    private readonly SealedHeader _header;
    private readonly bool _leaveOpen;
    private readonly AesGcm _gcm;
    private readonly byte[] _plain = new byte[SealedFile.ChunkSize];
    private readonly byte[] _sealed = new byte[SealedFile.SealedChunkSize];
    private int _filled;
    private long _index;
    private long _written;
    private bool _headerOut;
    private bool _completed;
    private bool _disposed;

    // A null key makes a random one and wraps it into the header.
    public SealedWriteStream(Stream file, byte[]? key, bool leaveOpen)
    {
        _file = file;
        _header = SealedHeader.Create(key);
        _leaveOpen = leaveOpen;
        _gcm = new AesGcm(_header.Key, SealedFile.TagSize);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_completed;
    public override long Length => _written;
    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDone();
        if (!_headerOut)
        {
            _file.Write(_header.Bytes);
            _headerOut = true;
        }
        while (buffer.Length > 0)
        {
            if (_filled == SealedFile.ChunkSize)
                _file.Write(SealPending(final: false));
            int take = Math.Min(buffer.Length, SealedFile.ChunkSize - _filled);
            buffer[..take].CopyTo(_plain.AsSpan(_filled));
            _filled += take;
            _written += take;
            buffer = buffer[take..];
        }
    }

    public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDone();
        if (!_headerOut)
        {
            await _file.WriteAsync(_header.Bytes, cancellationToken).ConfigureAwait(false);
            _headerOut = true;
        }
        while (buffer.Length > 0)
        {
            if (_filled == SealedFile.ChunkSize)
                await _file.WriteAsync(SealPendingMemory(final: false), cancellationToken).ConfigureAwait(false);
            int take = Math.Min(buffer.Length, SealedFile.ChunkSize - _filled);
            buffer.Span[..take].CopyTo(_plain.AsSpan(_filled));
            _filled += take;
            _written += take;
            buffer = buffer[take..];
        }
    }

    // Seals the last chunk. Nothing can be written after.
    public void Complete()
    {
        if (_completed)
            return;
        if (!_headerOut)
        {
            _file.Write(_header.Bytes);
            _headerOut = true;
        }
        _file.Write(SealPending(final: true));
        _completed = true;
        _file.Flush();
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
            return;
        if (!_headerOut)
        {
            await _file.WriteAsync(_header.Bytes, cancellationToken).ConfigureAwait(false);
            _headerOut = true;
        }
        await _file.WriteAsync(SealPendingMemory(final: true), cancellationToken).ConfigureAwait(false);
        _completed = true;
        await _file.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private ReadOnlySpan<byte> SealPending(bool final) => SealPendingMemory(final).Span;

    private ReadOnlyMemory<byte> SealPendingMemory(bool final)
    {
        Span<byte> nonce = stackalloc byte[12];
        _header.Nonce(_index, final, nonce);
        _gcm.Encrypt(nonce, _plain.AsSpan(0, _filled), _sealed.AsSpan(0, _filled), _sealed.AsSpan(_filled, SealedFile.TagSize), _header.Bytes);
        int length = _filled + SealedFile.TagSize;
        _index++;
        _filled = 0;
        return _sealed.AsMemory(0, length);
    }

    private void ThrowIfDone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("The sealed file is already complete.");
    }

    public override void Flush()
    {
        if (!_disposed)
            _file.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _disposed ? Task.CompletedTask : _file.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            try
            {
                Complete();
            }
            finally
            {
                _disposed = true;
                _gcm.Dispose();
                Array.Clear(_plain);
                if (!_leaveOpen)
                    _file.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            try
            {
                await CompleteAsync().ConfigureAwait(false);
            }
            finally
            {
                _disposed = true;
                _gcm.Dispose();
                Array.Clear(_plain);
                if (!_leaveOpen)
                    await _file.DisposeAsync().ConfigureAwait(false);
            }
        }
        GC.SuppressFinalize(this);
    }
}
