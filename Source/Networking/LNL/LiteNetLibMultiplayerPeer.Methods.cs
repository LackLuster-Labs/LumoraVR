using System;
using Godot;
using LiteNetLib;

namespace LumoraVR.Source.Networking
{
    public partial class LiteNetLibMultiplayerPeer
    {
        /// <summary>
        /// Instructs the server to send a NAT Punchthrough request
        /// </summary>
        /// <param name="address">The address of the punchthrough server</param>
        /// <param name="port">The port of the punchthrough server</param>
        /// <param name="roomKey">The token to send to the punchthrough server, should be some sort of identifier that this is the server for a session</param>
        public void ServerSendNatPunchthrough(string address, int port, string roomKey)
        {
            if (!NatPunch) return;
            _punchthroughTimer.Start();
            NetManager.NatPunchModule.SendNatIntroduceRequest(address, port, roomKey);
        }

        public Error CreateServer(int port, int maxClients = 32)
        {
            if (NetManager.IsRunning) return Error.AlreadyInUse;

            //GD.Print($"Creating server on port {port}");

            NatPunch = false;

            MaxClients = maxClients;
            NetManager.NatPunchEnabled = NatPunch;
            _localNetworkId = 1;

            NetManager.Start(port);

            return Error.Ok;
        }

        public Error CreateServerNat(int port, int maxClients = 32)
        {
            if (NetManager.IsRunning) return Error.AlreadyInUse;

            //GD.Print($"Creating server on port {port}");

            NatPunch = true;

            MaxClients = maxClients;
            NetManager.NatPunchEnabled = NatPunch;
            _localNetworkId = 1;

            NetManager.Start(port);

            return Error.Ok;
        }

        /// <summary>
        /// Create a client that directly connects to a server
        /// </summary>
        /// <param name="address">The address of the target server</param>
        /// <param name="port">The port of the target server</param>
        /// <param name="roomKey">The token to send to the target server, should be something like a password</param>
        /// <returns></returns>
        public Error CreateClient(string address, int port, string roomKey = RoomKey)
        {
            if (NetManager.IsRunning) return Error.AlreadyInUse;

            //GD.Print($"Connecting to server {address} on port {port}");

            NatPunch = false;

            NetManager.Start();
            NetManager.Connect(address, port, roomKey);

            _clientConnectionStatus = ConnectionStatus.Connecting;

            return Error.Ok;
        }

        /// <summary>
        /// Create a client that performs NAT Punchthrough to arrive at a destination server
        /// </summary>
        /// <param name="address">The address of the punchthrough server</param>
        /// <param name="port">The port of the punchthrough server</param>
        /// <param name="roomKey">The token to send to the punchthrough server, should be some sort of session ID</param>
        /// <returns></returns>
        public Error CreateClientNat(string address, int port, string roomKey = RoomKey)
        {
            if (NetManager.IsRunning) return Error.AlreadyInUse;

            //GD.Print($"Connecting to server {address} on port {port}");

            NatPunch = true;

            NetManager.Start();

            NetManager.NatPunchModule.SendNatIntroduceRequest(address, port, roomKey);
            _punchthroughTimer.Start();

            _clientConnectionStatus = ConnectionStatus.Connecting;

            return Error.Ok;
        }
    }
}
