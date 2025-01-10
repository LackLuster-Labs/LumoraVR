using Godot;
using Godot.Collections;
using System;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Input;
using Aquamarine.Source.Networking;

namespace Aquamarine.Source.Management;

public partial class ClientManager : Node
{

    [Export] private Node3D _inputRoot;
    [Export] private MultiplayerScene _multiplayerScene;
    [Export] public string signalingServerUrl = "ws://localhost:9080"; //"wss://lumora-signaling.wattlefoxxo.au";

    private WebRtcMultiplayerClient clientPeer;
    private XRInterface _xrInterface;
    private IInputProvider _input;

    public static ClientManager Instance;
    public static bool ShowDebug = true;

    public override void _Ready()
    {
        Instance = this;

        clientPeer = GetNode<WebRtcMultiplayerClient>("Client");

        clientPeer.LobbyJoined += OnLobbyJoined;

        InitializeInput();
        // SpawnLocalHome();
        JoinSession("djt6O4fFEjzABMzJ");
    }

    private void SpawnLocalHome()
    {
        CreateSession();
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

    public void JoinSession(string secret = "")
    {
        clientPeer.Start(signalingServerUrl, secret);
    }

    // TODO: Implement this.
    public void CreateSession()
    {
        JoinSession("");
    }

    public void LeaveSession()
    {
        clientPeer.Stop();
    }

    public void JoinNatServer(string identifier)
    {
        Logger.Error("JoinNatServer(string identifier) is depricated! Remove this function later.");

    }

    public void JoinServer(string address, int port)
    {
        Logger.Error("JoinServer(string address, int port) is depricated! Remove this function later.");
    }

    private void OnLobbyJoined(string lobby)
    {
        Logger.Log($"Joined lobby with id: {lobby}");
    }
}
