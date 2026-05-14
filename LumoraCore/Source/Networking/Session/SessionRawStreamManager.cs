using System;
using Lumora.Core.Networking.Streams;
namespace Lumora.Core.Networking.Session;
public class SessionRawStreamManager
{
    private readonly Session _session;
    public SessionRawStreamManager(Session parent)
    {
        _session = parent;
        parent.RawFrameReceived += OnMessage;

    }

    private void OnMessage(User sender, RefID streamRefID, ushort sequence, ReadOnlyMemory<byte> payload)
    {
        if(!_session.World.ReferenceController.TryGetObject<Stream>(streamRefID,out var stream) 
        || stream.Owner != sender || stream is not IRawStream raw)
        return;
        raw.EnqueueRawFrame(sequence,payload);
    }
}