using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Full batch - contains complete state.
/// Sent to new users or for conflict resolution.
/// </summary>
public class FullBatch : BinaryMessageBatch
{
    public override MessageType MessageType => MessageType.Full;
    public override bool Reliable => true;
    public bool UseBackgroundQueue { get; set; }
    public override bool Background => UseBackgroundQueue;

    public FullBatch(ulong stateVersion, ulong syncTick, IConnection sender = null)
        : base(stateVersion, syncTick, sender)
    {
    }
}
