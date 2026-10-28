// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Transport.Framed;

// One byte stream of a link, each way: its writer drains the outbox, its reader cuts what arrives into frames.
// A TCP link is one leg carrying every lane; a QUIC link has a leg per lane group, each on streams of its own,
// so a loss on one never holds the others. -xlinka
internal sealed class FramedLeg
{
    private const int BatchBytes = 64 * 1024;
    private const int ReadBufferBytes = 2 * (FramedWire.HeaderBytes + FramedWire.MaxFramePayload) + 4096;

    private readonly FramedLink _link;
    private readonly TaskCompletionSource _writerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FramedLeg(FramedLink link, int index, IReadOnlyList<byte> lanes)
    {
        _link = link;
        Index = index;
        Lanes = lanes;
        Outbox = new FramedOutbox(lanes);
    }

    public int Index { get; }

    public IReadOnlyList<byte> Lanes { get; }

    public FramedOutbox Outbox { get; }

    public Task WriterDone => _writerDone.Task;

    public void StartWriter(Stream output, CancellationToken cancellationToken)
        => _ = Task.Run(() => WriteLoopAsync(output, cancellationToken));

    public void StartReader(Stream input, CancellationToken cancellationToken)
        => _ = Task.Run(() => ReadLoopAsync(input, cancellationToken));

    private async Task WriteLoopAsync(Stream output, CancellationToken cancellationToken)
    {
        var batch = new byte[BatchBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int count = _link.TakeFor(this, batch);
                if (count == 0)
                {
                    if (Outbox.Drained)
                        break;
                    await Outbox.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await output.WriteAsync(batch.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested)
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _link.LegFailed(this, "sending failed: " + ex.Message);
        }
        finally
        {
            _writerDone.TrySetResult();
        }
    }

    private async Task ReadLoopAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferBytes];
        int start = 0, end = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (end - start >= FramedWire.HeaderBytes)
                {
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(start + 2, 2));
                    if (end - start < FramedWire.HeaderBytes + length)
                        break;
                    if (!_link.OnFrame(this, buffer[start], buffer[start + 1], buffer, start + FramedWire.HeaderBytes, length))
                        return;
                    start += FramedWire.HeaderBytes + length;
                }
                if (start == end)
                {
                    start = end = 0;
                }
                else if (buffer.Length - end < FramedWire.HeaderBytes + FramedWire.MaxFramePayload)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }

                int read = await input.ReadAsync(buffer.AsMemory(end), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    _link.LegEnded(this);
                    return;
                }
                end += read;
                _link.CountReceived(read);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _link.LegFailed(this, "receiving failed: " + ex.Message);
        }
    }
}
