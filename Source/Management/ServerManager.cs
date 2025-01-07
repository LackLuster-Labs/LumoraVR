using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Networking;
using Godot;
using LiteNetLib;
using LiteNetLib.Utils;
using Bones.Core;
using System.Threading.Tasks;

namespace Aquamarine.Source.Management
{
    public partial class ServerManager : Node
    {
        [Export] public bool Advertise = true;
        [Export] private MultiplayerScene _multiplayerScene;

        public LiteNetLibMultiplayerPeer MultiplayerPeer;

        public NetManager SessionListManager;
        public EventBasedNetListener SessionListListener;
        public NetPeer MainServer;

        private string _sessionSecret;
        private bool _running = true;

        private const int Port = 7000;
        private const int SessionListPort = Port + 7001;
        private const int MaxConnections = 20;
        private const string SessionApiUrl = "https://api.xlinka.com/sessions";

        private string _publicIp;
        private string _worldName = "My World";
        private string _worldIdentifier = "placeholder";

        public enum ServerType
        {
            NotAServer,
            Standard,
            Local
        }

        //TODO: move this somewhere else?
        public static ServerType CurrentServerType
        {
            get
            {
                if (_serverType.HasValue) return _serverType.Value;
                
                var args = OS.GetCmdlineArgs();
                var isLocalHomeServer = args.Any(i => i.Equals("--run-home-server", StringComparison.CurrentCultureIgnoreCase));
                if (isLocalHomeServer)
                {
                    _serverType = ServerType.Local;
                    return ServerType.Local;
                }
                
                var isServer = args.Any(i => i.Equals("--run-server", System.StringComparison.CurrentCultureIgnoreCase));
                if (isServer)
                {
                    _serverType = ServerType.Standard;
                    return ServerType.Standard;
                }

                _serverType = ServerType.NotAServer;
                return ServerType.NotAServer;
            }
        }
        private static ServerType? _serverType;

        public override void _Process(double delta)
        {
            base._Process(delta);
            SessionListManager.PollEvents();
        }

        public override void _Ready()
        {
            try
            {
                var serverType = CurrentServerType;
                
                MultiplayerPeer = new LiteNetLibMultiplayerPeer();

                if (serverType is ServerType.Local)
                {
                    /*
                    var args = OS.GetCmdlineArgs().ToList();
                    var runHomeIndex = args.IndexOf("--run-home-server");

                    var port = int.Parse(args[runHomeIndex][2..]);
                    
                    Logger.Log(port.ToString());
                    */
                    MultiplayerPeer.CreateServer(6000, 1);
                    Logger.Log($"Local server started on port {6000}.");
                }
                else
                {
                    MultiplayerPeer.CreateServerNat(Port, MaxConnections);
                    Logger.Log($"Server started on port {Port} with a maximum of {MaxConnections} connections.");
                }
                
                Multiplayer.MultiplayerPeer = MultiplayerPeer;

                MultiplayerPeer.PeerConnected += OnPeerConnected;
                MultiplayerPeer.PeerDisconnected += OnPeerDisconnected;
                

                if (serverType is ServerType.Standard)
                {
                    // Initialize session list manager
                    SessionListListener = new EventBasedNetListener();
                    SessionListManager = new NetManager(SessionListListener)
                    {
                        IPv6Enabled = true,
                        PingInterval = 10000,
                    };

                    SessionListListener.NetworkReceiveEvent += SessionListListenerOnNetworkReceiveEvent;
                    SessionListListener.PeerDisconnectedEvent += SessionListListenerOnPeerDisconnectedEvent;

                    SessionListManager.Start(SessionListPort);
                    ConnectToSessionServer();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing ServerManager: {ex}");
            }
        }

        private void SessionListListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectinfo)
        {
            GD.Print($"Disconnected from the session server: {disconnectinfo.Reason}");
            if (_running)
            {
                ConnectToSessionServer();
            }
        }

        private void ConnectToSessionServer()
        {
            GD.Print("Attempting to connect to session server...");
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

            switch (opcode)
            {
                case 0x01:
                    _sessionSecret = reader.GetString();
                    GD.Print($"Set session secret to {_sessionSecret}");
                    var writer = new NetDataWriter();
                    writer.Put((byte)0x01);
                    writer.Put(_worldName);
                    writer.Put(_worldIdentifier);
                    MainServer.Send(writer, DeliveryMethod.ReliableOrdered);
                    break;

                case 0x02:
                    GD.Print("Sending NAT punchthrough message");
                    MultiplayerPeer.ServerSendNatPunchthrough(
                        SessionInfo.SessionServer.Address.ToString(),
                        SessionInfo.SessionServer.Port,
                        $"server:{_sessionSecret}"
                    );
                    break;
            }
        }


        private void OnPeerConnected(long id)
        {
            Logger.Log($"Peer connected with ID: {id}. Attempting to spawn player...");
            try
            {
                _multiplayerScene.SpawnPlayer((int)id); // Spawn the player
                //Logger.Log($"Player spawned successfully for ID: {id}.");

                _multiplayerScene.PlayerList.Add((int)id, new PlayerInfo()); // Add to player list
                //Logger.Log("Player added to the player list.");

                _multiplayerScene.SendUpdatedPlayerList(); // Notify others of the new player
                //Logger.Log("Updated player list sent.");

                _multiplayerScene.SendAllPrefabs((int)id); // Send prefabs to the new player
                //Logger.Log("Prefabs sent to the new player.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error during OnPeerConnected for ID {id}: {ex.Message}");
            }
        }



        private void OnPeerDisconnected(long id)
        {
            try
            {
                var idInt = (int)id;
                _multiplayerScene.RemovePlayer(idInt);
                _multiplayerScene.PlayerList.Remove(idInt);
                _multiplayerScene.SendUpdatedPlayerList();
                Logger.Log($"Peer disconnected with ID: {id}.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error handling peer disconnection (ID: {id}): {ex.Message}");
            }

            if (CurrentServerType is ServerType.Local)
            {
                Logger.Log("Quitting local server");
                MultiplayerPeer.Close();
                GetTree().Quit();
            }
        }
/*
        private async Task<string> GetPublicIP()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                var publicIp = await client.GetStringAsync("https://api.ipify.org");
                Logger.Log($"Retrieved public IP: {publicIp}");
                return publicIp.Trim();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error fetching public IP: {ex.Message}");
                return null;
            }
        }
*/
    }
}
