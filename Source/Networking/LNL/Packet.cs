using LiteNetLib;
using LiteNetLib.Utils;

namespace LumoraVR.Source.Networking
{
    public partial class LiteNetLibMultiplayerPeer
    {
        //LIMITATIONS
        // 1. There are only 64 channels total, but only 63 are usable (0-62), the last channel is reserved as a control channel, godot normally supports the full Int32 range iirc.
        //    We shouldn't need to worry about it for this project, but it's something to keep in mind.
        internal class Packet
        {
            public NetPeer Peer;
            public byte[] Data;
            public byte Channel;
            public DeliveryMethod Method;

            public Packet(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
            {
                Peer = peer;
                Data = reader.GetRemainingBytes();
                Channel = channel;
                Method = method;
            }
        }
    }
}
