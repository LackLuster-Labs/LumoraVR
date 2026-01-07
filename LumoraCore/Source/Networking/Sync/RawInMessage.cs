namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Raw incoming network message.
/// </summary>
public class RawInMessage
{
    public byte[] Data { get; set; }
    public int Offset { get; set; }
    public int Length { get; set; }
    public IConnection Sender { get; set; }
}
