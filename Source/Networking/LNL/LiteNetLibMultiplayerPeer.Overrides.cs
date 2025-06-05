using System;
using System.Collections.Generic;
using Godot;
using LiteNetLib;

namespace LumoraVR.Source.Networking
{
    public partial class LiteNetLibMultiplayerPeer
    {
        public override void _Close()
        {
            if (!NetManager.IsRunning) return;
            NetManager.Stop();
            _localNetworkId = -1;
        }

        public override void _DisconnectPeer(int pPeer, bool pForce)
        {
            _idToPeer[pPeer].Disconnect();
        }

        public override int _GetAvailablePacketCount() => IsServer || _clientConnectionStatus == ConnectionStatus.Connected ? _packetQueue.Count : 0;

        public override ConnectionStatus _GetConnectionStatus() => IsServer ? ConnectionStatus.Connected : _clientConnectionStatus;

        public override int _GetMaxPacketSize() => NetConstants.MaxPacketSize;

        public override int _GetPacketChannel() => _packetQueue.TryPeek(out var packet) ? packet.Channel : 0;

        public override TransferModeEnum _GetPacketMode() => (_packetQueue.TryPeek(out var packet) ? packet.Method : DeliveryMethod.Unreliable) switch
        {
            DeliveryMethod.Unreliable => TransferModeEnum.Unreliable,
            DeliveryMethod.Sequenced => TransferModeEnum.UnreliableOrdered,
            DeliveryMethod.ReliableOrdered => TransferModeEnum.Reliable,
            _ => TransferModeEnum.Unreliable,
        };

        public override int _GetPacketPeer()
        {
            if (_packetQueue.TryPeek(out var packet)) return _peerToId[packet.Peer];
            return -1;
        }

        public override byte[] _GetPacketScript()
        {
            _packetQueue.TryDequeue(out _currentPacket);
            if (_currentPacket is not null) return _currentPacket.Data;

            //GD.PrintErr("CurrentPacket is null");
            return [];
        }

        public override int _GetTransferChannel() => _currentChannel;

        public override TransferModeEnum _GetTransferMode() => _currentMethod switch
        {
            DeliveryMethod.Unreliable => TransferModeEnum.Unreliable,
            DeliveryMethod.Sequenced => TransferModeEnum.UnreliableOrdered,
            DeliveryMethod.ReliableOrdered => TransferModeEnum.Reliable,
            _ => TransferModeEnum.Unreliable,
        };

        public override Error _PutPacketScript(byte[] pBuffer)
        {
            //GD.Print("Sending packet");
            try
            {
                if (_currentPeerId == 0)
                {
                    NetManager.SendToAll(pBuffer, _currentChannel, _currentMethod);
                }
                else
                {
                    _currentPeer.Send(pBuffer, _currentChannel, _currentMethod);
                }
            }
            catch
            {
                return Error.Failed;
            }
            return Error.Ok;
        }

        public override bool _IsServer() => IsServer;

        public override bool _IsServerRelaySupported() => true; //TODO: does this matter if we disable it?

        public override void _SetTargetPeer(int pPeer)
        {
            _currentPeer = _idToPeer.GetValueOrDefault(pPeer);
            _currentPeerId = pPeer;
        }

        public override void _SetTransferChannel(int pChannel)
        {
            _currentChannel = (byte)Math.Clamp(pChannel, 0, ControlChannel - 1);
        }

        public override void _SetTransferMode(TransferModeEnum pMode) =>
            _currentMethod = pMode switch
            {
                TransferModeEnum.Unreliable => DeliveryMethod.Unreliable,
                TransferModeEnum.UnreliableOrdered => DeliveryMethod.Sequenced,
                TransferModeEnum.Reliable => DeliveryMethod.ReliableOrdered,
                _ => DeliveryMethod.Unreliable,
            };

        public override int _GetUniqueId()
        {
            //if (!NetManager.IsRunning || (!IsServer && _clientConnectionStatus != ConnectionStatus.Connected)) GD.PrintErr("Not Active (this is not an error, you got disconnected)");
            return _localNetworkId;
        }

        public override bool _IsRefusingNewConnections() => _refuseNewConnections;

        public override void _SetRefuseNewConnections(bool pEnable) => _refuseNewConnections = pEnable;

        public override void _Poll()
        {
            NetManager.PollEvents();
            if (NatPunch && _punchthroughTimer.IsRunning)
            {
                NetManager.NatPunchModule.PollEvents();
                if (_punchthroughTimer.Elapsed.TotalSeconds >= PunchthroughTimeout) _punchthroughTimer.Stop();
            }
        }
    }
}
