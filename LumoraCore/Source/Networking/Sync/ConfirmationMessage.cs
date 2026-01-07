using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Confirmation message - sent by authority to confirm/correct client changes.
/// Contains full state for conflicting elements.
/// </summary>
public class ConfirmationMessage : BinaryMessageBatch
{
    public override MessageType MessageType => MessageType.Confirmation;
    public override bool Reliable => true;

    /// <summary>
    /// The sync tick being confirmed.
    /// </summary>
    public ulong ConfirmTime { get; set; }

    public ConfirmationMessage(ulong confirmTime, ulong stateVersion, ulong syncTick, IConnection sender = null)
        : base(stateVersion, syncTick, sender)
    {
        ConfirmTime = confirmTime;
    }
}
