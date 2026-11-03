// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Lumora.Nexus.Storage;

// A sealed file written in any order, for bytes that arrive scattered (a peer sends a file over several lanes
// and a restart can repeat ranges). The length is known up front, so every chunk's place and whether it is the
// last is known too. A chunk is sealed and written the moment all of it is here; until then its bytes wait in
// memory, never on disk in the clear. The same offset always carries the same bytes, so a repeat is dropped.
// -xlinka
public sealed class SealedScatterWriter : IDisposable
{
    // Chunks waiting for the rest of their bytes. A sender only has a window's worth in flight, so this is a
    // ceiling against a peer that scatters slivers on purpose, not a working limit.
    public const int MaxPendingChunks = 1024;

    private readonly FileStream _file;
    private readonly SealedHeader _header;
    private readonly AesGcm _gcm;
    private readonly long _length;
    private readonly long _chunks;
    private readonly bool[] _done;
    private readonly Dictionary<long, Pending> _pending = new();
    private readonly byte[] _sealed = new byte[SealedFile.SealedChunkSize];
    private long _sealedCount;
    private bool _disposed;

    private sealed class Pending
    {
        public readonly byte[] Plain;
        public readonly List<(int Start, int End)> Runs = new();
        public int Covered;

        public Pending(int size) => Plain = new byte[size];
    }

    public SealedScatterWriter(string path, long length, byte[] key)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        _length = length;
        _chunks = length == 0 ? 1 : (length + SealedFile.ChunkSize - 1) / SealedFile.ChunkSize;
        if (_chunks > int.MaxValue)
            throw new InvalidDataException("Too long for a sealed file.");
        _done = new bool[_chunks];
        _header = SealedHeader.Create(key);
        _gcm = new AesGcm(_header.Key, SealedFile.TagSize);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.RandomAccess);
        try
        {
            _file.Write(_header.Bytes);
            _file.SetLength(SealedFile.SealedLength(length, carriesKey: false));
            if (length == 0)
                SealChunk(0, ReadOnlySpan<byte>.Empty);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public bool IsComplete => _sealedCount == _chunks;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset > _length - data.Length)
            throw new InvalidDataException($"Write at {offset} of {data.Length} bytes runs past {_length}.");
        while (data.Length > 0)
        {
            long index = offset / SealedFile.ChunkSize;
            int within = (int)(offset - index * SealedFile.ChunkSize);
            int size = ChunkLength(index);
            int take = Math.Min(data.Length, size - within);
            if (!_done[index])
            {
                if (within == 0 && take == size)
                {
                    if (_pending.Remove(index, out var partial))
                        Array.Clear(partial.Plain);
                    SealChunk(index, data[..take]);
                }
                else
                {
                    if (!_pending.TryGetValue(index, out var pending))
                    {
                        if (_pending.Count >= MaxPendingChunks)
                            throw new InvalidDataException($"More than {MaxPendingChunks} chunks are partly here at once.");
                        pending = new Pending(size);
                        _pending[index] = pending;
                    }
                    data[..take].CopyTo(pending.Plain.AsSpan(within));
                    Cover(pending, within, within + take);
                    if (pending.Covered == size)
                    {
                        _pending.Remove(index);
                        SealChunk(index, pending.Plain);
                    }
                }
            }
            data = data[take..];
            offset += take;
        }
    }

    // Flushes and closes; throws unless every chunk arrived.
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsComplete)
            throw new InvalidDataException($"{_chunks - _sealedCount} of {_chunks} chunks never arrived.");
        _file.Flush();
        Dispose();
    }

    private int ChunkLength(long index)
        => index == _chunks - 1 ? (int)(_length - index * SealedFile.ChunkSize) : SealedFile.ChunkSize;

    private void SealChunk(long index, ReadOnlySpan<byte> plain)
    {
        Span<byte> nonce = stackalloc byte[12];
        _header.Nonce(index, index == _chunks - 1, nonce);
        _gcm.Encrypt(nonce, plain, _sealed.AsSpan(0, plain.Length), _sealed.AsSpan(plain.Length, SealedFile.TagSize), _header.Bytes);
        _file.Position = _header.Length + index * SealedFile.SealedChunkSize;
        _file.Write(_sealed, 0, plain.Length + SealedFile.TagSize);
        _done[index] = true;
        _sealedCount++;
    }

    private static void Cover(Pending pending, int start, int end)
    {
        var runs = pending.Runs;
        int i = 0;
        while (i < runs.Count && runs[i].End < start)
            i++;
        int had = 0;
        int j = i;
        while (j < runs.Count && runs[j].Start <= end)
        {
            start = Math.Min(start, runs[j].Start);
            end = Math.Max(end, runs[j].End);
            had += runs[j].End - runs[j].Start;
            j++;
        }
        runs.RemoveRange(i, j - i);
        runs.Insert(i, (start, end));
        pending.Covered += end - start - had;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gcm.Dispose();
        _file.Dispose();
        foreach (var pending in _pending.Values)
            Array.Clear(pending.Plain);
        _pending.Clear();
    }
}
