using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Delta batch - contains only changed state.
/// Sent every sync tick to synchronize changes.
/// </summary>
public class DeltaBatch : BinaryMessageBatch
{
    public override MessageType MessageType => MessageType.Delta;
    public override bool Reliable => true;

    public DeltaBatch(ulong stateVersion, ulong syncTick, IConnection sender = null)
        : base(stateVersion, syncTick, sender)
    {
    }
}
