using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using LiteNetLib;

namespace LumoraVR.Source.Networking
{
    public partial class LiteNetLibMultiplayerPeer : MultiplayerPeerExtension
    {
        public readonly NetManager NetManager;
        public readonly EventBasedNetListener Listener;
        public readonly EventBasedNatPunchListener NatPunchListener;

        [Signal]
        public delegate void ClientConnectionSuccessEventHandler();
        [Signal]
        public delegate void ClientConnectionFailEventHandler();

        private readonly ConcurrentQueue<Packet> _packetQueue = new();

        public int MaxClients { get; private set; } = 32;
        private readonly Dictionary<NetPeer, int> _peerToId = new();
        private Dictionary<int, NetPeer> _idToPeer = new();

        private int _localNetworkId = -1;

        private Packet _currentPacket;

        private NetPeer _currentPeer;
        private int _currentPeerId;
        private byte _currentChannel;
        private DeliveryMethod _currentMethod;

        private bool IsServer => _localNetworkId == 1;

        private ConnectionStatus _clientConnectionStatus;

        private bool _refuseNewConnections;

        private bool NatPunch
        {
            get => NetManager.NatPunchEnabled;
            set => NetManager.NatPunchEnabled = value;
        }

        public float PunchthroughTimeout = 10;

        private Stopwatch _punchthroughTimer = new();

        public LiteNetLibMultiplayerPeer()
        {
            Listener = new EventBasedNetListener();
            NetManager = new NetManager(Listener)
            {
                ChannelsCount = 64,
                IPv6Enabled = true,
                AutoRecycle = true,
            };

            NatPunchListener = new EventBasedNatPunchListener();
            NetManager.NatPunchModule.Init(NatPunchListener);
            NetManager.NatPunchModule.UnsyncedEvents = true;

            //NatPunchListener.NatIntroductionRequest += NatPunchListenerOnNatIntroductionRequest;
            NatPunchListener.NatIntroductionSuccess += NatPunchListenerOnNatIntroductionSuccess;

            Listener.NetworkReceiveEvent += ListenerOnNetworkReceiveEvent;
            Listener.PeerConnectedEvent += ListenerOnPeerConnectedEvent;
            Listener.PeerDisconnectedEvent += ListenerOnPeerDisconnectedEvent;
            Listener.ConnectionRequestEvent += ListenerOnConnectionRequestEvent;
            Listener.NetworkErrorEvent += ListenerOnNetworkErrorEvent;
        }
    }
}
