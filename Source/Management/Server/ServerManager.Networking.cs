using Godot;
using LiteNetLib.Utils;
using LiteNetLib;
using LumoraVR.Source.Logging;

namespace LumoraVR.Source.Management
{
    public partial class ServerManager
    {
        private void SessionListListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectinfo)
        {
            GD.Print($"Disconnected from the session server: {disconnectinfo.Reason}");
            Logger.Log($"Disconnected from the session server: {disconnectinfo.Reason}");
            if (_running)
            {
                ConnectToSessionServer();
            }
        }

        private void ConnectToSessionServer()
        {
            GD.Print("Attempting to connect to session server...");
            Logger.Log("Attempting to connect to session server...");
            MainServer = SessionListManager.Connect(SessionInfo.SessionServer, "Private");

            /*
            this.CreateTimer(10, () =>
            {
                GD.Print($"{MainServer.ConnectionState}");
            });
            */
        }

        private void SessionListListenerOnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliverymethod)
        {
            var opcode = reader.GetByte();

            GD.Print($"Received session list message, opcode: {opcode}");
            Logger.Log($"Received session list message, opcode: {opcode}");

            switch (opcode)
            {
                case 0x01:
                    _sessionSecret = reader.GetString();
                    GD.Print($"Set session secret to {_sessionSecret}");
                    Logger.Log($"Set session secret to {_sessionSecret}");
                    var writer = new NetDataWriter();
                    writer.Put((byte)0x01);
                    writer.Put(_worldName);
                    writer.Put(_worldIdentifier);
                    MainServer.Send(writer, DeliveryMethod.ReliableOrdered);
                    break;

                case 0x02:
                    GD.Print("Sending NAT punchthrough message");
                    Logger.Log("Sending NAT punchthrough message");
                    MultiplayerPeer.ServerSendNatPunchthrough(
                        SessionInfo.SessionServer.Address.ToString(),
                        SessionInfo.SessionServer.Port,
                        $"server:{_sessionSecret}"
                    );
                    break;
            }
        }
    }
}
