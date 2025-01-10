using System;
using Godot;
using Godot.Collections;
using Aquamarine.Source.Logging;


namespace Aquamarine.Source.Networking;

public partial class WebsocketSignalingClient : Node
{
    public enum Message
    {
        JOIN,
        ID,
        PEER_CONNECT,
        PEER_DISCONNECT,
        OFFER,
        ANSWER,
        CANDIDATE,
        SEAL
    }

    [Export] public bool autoJoin = true;
    [Export] public string lobby = "";
    [Export] public bool mesh = true;

    public int code = 1000;
    public string reason = "Unknown";

    [Signal] public delegate void LobbyJoinedEventHandler(string lobby);
    [Signal] public delegate void ConnectedEventHandler(int id, bool usedMesh);
    [Signal] public delegate void DisconnectedEventHandler();
    [Signal] public delegate void PeerConnectedEventHandler(int id);
    [Signal] public delegate void PeerDisconnectedEventHandler(int id);
    [Signal] public delegate void OfferReceivedEventHandler(int id, string offer);
    [Signal] public delegate void AnswerReceivedEventHandler(int id, string answer);
    [Signal] public delegate void CandidateReceivedEventHandler(int id, string mid, int index, string sdp);
    [Signal] public delegate void LobbySealedEventHandler();

    public WebSocketPeer websocket = new WebSocketPeer();
    private WebSocketPeer.State oldState = WebSocketPeer.State.Closed;

    public void ConnectToUrl(string url)
    {
        Close();
        code = 1000;
        reason = "Unknown";
        websocket.ConnectToUrl(url);
    }

    public void Close()
    {
        websocket.Close();
    }

    public override void _Process(double delta)
    {
        websocket.Poll();

        WebSocketPeer.State state = websocket.GetReadyState();
        if (state != oldState && state == WebSocketPeer.State.Open && autoJoin)
        {
            JoinLobby(lobby);
        }

        while (state == WebSocketPeer.State.Open && websocket.GetAvailablePacketCount() > 0)
        {
            if (!ParseMessage())
            {
                Logger.Error("Error parsing malformed message form server.");
            }
        }

        if (state != oldState && state == WebSocketPeer.State.Closed)
        {
            code = websocket.GetCloseCode();
            reason = websocket.GetCloseReason();
            EmitSignal(SignalName.Disconnected);
        }

        oldState = state;
    }

    private bool ParseMessage()
    {
        string rawPacket = websocket.GetPacket().GetStringFromUtf8();
        var parsed = Json.ParseString(rawPacket);

        // Make sure godot and/or other clients are not being special
        if (parsed.Obj is not Dictionary message || !message.ContainsKey("type") || !message.ContainsKey("id") || message["data"].Obj is not string data)
        {
            return false;
        }

        string rawType = message["type"].AsString();
        string rawSourceId = message["id"].AsString();

        if (!rawType.IsValidInt() && !rawSourceId.IsValidInt())
        {
            return false;
        }

        int type = int.Parse(rawType);
        int sourceId = int.Parse(rawSourceId);

        switch ((Message)type)
        {
            case Message.ID:
                EmitSignal(SignalName.Connected, sourceId, data == "true");
                break;

            case Message.JOIN:
                EmitSignal(SignalName.LobbyJoined, data);
                break;

            case Message.SEAL:
                EmitSignal(SignalName.LobbySealed);
                break;

            case Message.PEER_CONNECT:
                EmitSignal(SignalName.PeerConnected, sourceId);
                break;

            case Message.PEER_DISCONNECT:
                EmitSignal(SignalName.PeerDisconnected, sourceId);
                break;

            case Message.OFFER:
                EmitSignal(SignalName.OfferReceived, sourceId, data);
                break;

            case Message.ANSWER:
                EmitSignal(SignalName.AnswerReceived, sourceId, data);
                break;

            case Message.CANDIDATE:
                string[] candidate = data.Split("\n", false);

                // Verify data
                if (candidate.Length != 3)
                {
                    return false;
                }

                if (!candidate[1].IsValidInt())
                {
                    return false;
                }

                EmitSignal(SignalName.CandidateReceived, sourceId, candidate[0], int.Parse(candidate[1]), candidate[2]);
                break;

            default:
                return false;
        }

        return true;
    }

    public Error JoinLobby(string lobby)
    {
        return SendMessage(Message.JOIN, mesh ? 0 : 1, lobby);
    }

    public Error SendCandidate(int id, string mid, long index, string sdp)
    {
        return SendMessage(Message.CANDIDATE, id, $"\n{mid}\n{index}\n{sdp}");
    }

    public Error SendOffer(int id, string offer)
    {
        return SendMessage(Message.OFFER, id, offer);
    }

    public Error SendAnswer(int id, string answer)
    {
        return SendMessage(Message.ANSWER, id, answer);
    }

    private Error SendMessage(Message type, int id, string data = "")
    {
        Dictionary message = new Dictionary
        {
            { "type", (int)type },
            { "id", id },
            { "data", data },
        };

        return websocket.SendText(Json.Stringify(message));
    }
}
