namespace Lumora.Core.Networking.Messages;

/// <summary>
/// Network message types.
/// </summary>
public enum MessageType : byte
{
    /// <summary>
    /// Control messages (join, leave, grant, etc.)
    /// </summary>
    Control = 0,

    /// <summary>
    /// Delta batch - incremental property changes only.
    /// 1-5KB typical size.
    /// </summary>
    Delta = 1,

    /// <summary>
    /// Full state batch - complete object state.
    /// 10KB-1MB typical size.
    /// Sent to new users or for conflict resolution.
    /// </summary>
    Full = 2,

    /// <summary>
    /// Stream data - high-frequency continuous data.
    /// Transforms, audio, etc. at 60+ Hz.
    /// </summary>
    Stream = 3,

    /// <summary>
    /// Confirmation from authority.
    /// </summary>
    Confirmation = 4
}
