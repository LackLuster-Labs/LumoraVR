using System;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams;
public interface IRawStream
{
    public void EnqueueRawFrame(ushort sequence, ReadOnlyMemory<byte> payload);
    public uint PollingRate {get;}
    public Task Poll();
}