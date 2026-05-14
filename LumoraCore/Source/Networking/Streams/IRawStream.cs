using System;
using System.IO;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams;
public interface IRawStream
{
    public void EnqueueRawFrame(ushort sequence, ReadOnlyMemory<byte> payload);
}