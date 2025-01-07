using System;
using System.Net.Http;
using System.Text.Json;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Input;
using Aquamarine.Source.Networking;
using Bones.Core;
using Godot;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Aquamarine.Source.Management
{
    public partial class ClientManager : Node
    {
        public static ClientManager Instance;
        public static bool ShowDebug = true;

        private XRInterface _xrInterface;
        private IInputProvider _input;
        private LiteNetLibMultiplayerPeer _peer;
        [Export] private Node3D _inputRoot;
        [Export] private MultiplayerScene _multiplayerScene;

        private const int Port = 7000; // Default port for LiteNetLib
        private const string RelayApiUrl = "https://api.xlinka.com/sessions";

        private bool _isDirectConnection = false;

        public override void _Ready()
        {
            Instance = this;
            
            try
            {
                InitializeInput();
                
                SpawnLocalHome();
                this.CreateTimer(5, () =>
                {
                    var info = FetchServerInfo();

                    /*
                    this.CreateTimer(2, () =>
                    {
                        if (info.IsCompletedSuccessfully && info.Result is not null && !string.IsNullOrEmpty(info.Result.SessionIdentifier))
                        {
                            GD.Print($"Fetched session identifier: {info.Result.SessionIdentifier}");
                            JoinNatServer(info.Result.SessionIdentifier);
                        }
                        else
                        {
                            GD.Print("Failed to fetch a valid session identifier in time.");
                        }
                        GD.Print(Multiplayer.GetUniqueId());
                    });
                    */
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Error initializing ClientManager: {ex.Message}");
            }
        }

        public override void _Input(InputEvent @event)
        {
            base._Input(@event);

            if (@event.IsActionPressed("ToggleDebug"))
            {
                ShowDebug = !ShowDebug;
            }
        }

        private void SpawnLocalHome()
        {
            OS.CreateProcess(OS.GetExecutablePath(), ["--run-home-server", "--xr-mode", "off", "--headless"]);
            
            this.CreateTimer(0.5f, () =>
            {
                JoinServer("localhost", 6000);
            });
        }

        private void InitializeInput()
        {
            _xrInterface = XRServer.FindInterface("OpenXR");

            if (IsInstanceValid(_xrInterface) && _xrInterface.IsInitialized())
            {
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
                GetViewport().UseXR = true;

                var vrInput = VRInput.PackedScene.Instantiate<VRInput>();
                _input = vrInput;
                _inputRoot.AddChild(vrInput);
                Logger.Log("XR interface initialized successfully.");
            }
            else
            {
                var desktopInput = DesktopInput.PackedScene.Instantiate<DesktopInput>();
                _input = desktopInput;
                _inputRoot.AddChild(desktopInput);
                Logger.Log("Desktop interface initialized successfully.");
            }
        }

        private void DisconnectFromCurrentServer()
        {
            if (_peer?.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected) _multiplayerScene.Rpc(MultiplayerScene.MethodName.DisconnectPlayer);
            _peer?.Close();
            Multiplayer.MultiplayerPeer = null;
        }

        public void JoinNatServer(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                Logger.Error("Session identifier is null or empty. Cannot join NAT server.");
                return;
            }

            DisconnectFromCurrentServer();
            
            var token = $"client:{identifier}";
            GD.Print($"Attempting NAT punchthrough with token: {token}");

            _peer = new LiteNetLibMultiplayerPeer();
            _peer.CreateClientNat(SessionInfo.SessionServer.Address.ToString(), SessionInfo.SessionServer.Port, token);
            Multiplayer.MultiplayerPeer = _peer;
            _isDirectConnection = true;
            
            _peer.PeerDisconnected += PeerOnPeerDisconnected;
            _peer.ClientConnectionSuccess += PeerOnClientConnectionSuccess;
            _peer.ClientConnectionFail += PeerOnClientConnectionFail;
        }
        private void PeerOnClientConnectionFail()
        {
            _peer.ClientConnectionFail -= PeerOnClientConnectionFail;
            _peer.ClientConnectionSuccess -= PeerOnClientConnectionSuccess;
            SpawnLocalHome();
        }
        private void PeerOnClientConnectionSuccess()
        {
            MultiplayerScene.Instance.Rpc(MultiplayerScene.MethodName.SetPlayerName, System.Environment.MachineName);
            _peer.ClientConnectionSuccess -= PeerOnClientConnectionSuccess;
            _peer.ClientConnectionFail -= PeerOnClientConnectionFail;
        }
        private void PeerOnPeerDisconnected(long id)
        {
            GD.Print($"{id} disconnected");
            if (id == 1) SpawnLocalHome();
        }


        public void JoinServer(string address, int port)
        {
            DisconnectFromCurrentServer();
            
            _peer = new LiteNetLibMultiplayerPeer();
            _peer.CreateClient(address, port);
            Multiplayer.MultiplayerPeer = _peer;
            _isDirectConnection = true;
            
            _peer.ClientConnectionSuccess += PeerOnClientConnectionSuccess;
            _peer.ClientConnectionFail += PeerOnClientConnectionFail;
        }

        public void JoinNatServerRelay(string identifier)
        {
            //TODO
            
            _peer = new LiteNetLibMultiplayerPeer();
            _peer.CreateClient(SessionInfo.RelayServer.Address.ToString(), SessionInfo.RelayServer.Port, identifier);
            Multiplayer.MultiplayerPeer = _peer;
            _isDirectConnection = false;
            
            _peer.PeerDisconnected += PeerOnPeerDisconnected;
            _peer.ClientConnectionSuccess += PeerOnClientConnectionSuccess;
            _peer.ClientConnectionFail += PeerOnClientConnectionFail;
        }

        private async Task<SessionInfo> FetchServerInfo()
        {
            try
            {
                GD.Print("Trying to get session list");

                using var client = new System.Net.Http.HttpClient();
                var response = await client.GetStringAsync(SessionInfo.SessionList);

                GD.Print("Got the session list");
                GD.Print(response);

                var sessions = JsonSerializer.Deserialize<List<SessionInfo>>(response, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

                if (sessions != null && sessions.Count != 0)
                {
                    foreach (var session in sessions)
                    {
                        GD.Print($"Session: {session.Name}, Identifier: {session.SessionIdentifier}");

                        if (string.IsNullOrEmpty(session.SessionIdentifier))
                        {
                            Logger.Error($"Session {session.Name} is missing an identifier. Skipping.");
                            continue;
                        }

                        return session; // Return the first valid session
                    }
                }

                Logger.Error("No valid sessions available in the API response.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error fetching server info: {ex.Message}");
                return null;
            }
        }

    }
}
