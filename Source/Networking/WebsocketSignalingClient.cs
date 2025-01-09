using Godot;
// using Godot.Collections;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Aquamarine.Source.Networking;

public partial class WebsocketSignalingClient : Node
{

    public enum Message {
        JOIN,
        ID,
        PEER_CONNECT,
        PEER_DISCONNECT,
        OFFER,
        ANSWER,
        CANDIDATE,
        SEAL
    }

    public bool autoJoin = true;
    public string lobby = "";
    public bool mesh = true;

    private WebSocketPeer ws = new WebSocketPeer();
    private int code = 1000;
    private string reason = "Unknown";
    private WebSocketPeer.State oldState = WebSocketPeer.State.Closed;

    [Signal]
    public delegate void LobbyJoinedEventHandler(string lobby);
    
    [Signal]
    public delegate void ConnectedEventHandler(int id, bool usedMesh);
    
    [Signal]
    public delegate void DisconnectedEventHandler();

    [Signal]
    public delegate void PeerConnectedEventHandler(int id);

    [Signal]
    public delegate void PeerDisconnectedEventHandler(int id);

    [Signal]
    public delegate void OfferReceivedEventHandler(int id, string offer);

    [Signal]
    public delegate void AnswerRecivedEventHandler(int id, string answer);

    [Signal]
    // TODO: Fix Types
    public delegate void CandidateRecivedEventHandler(int id, int mid, int index, int sdp);
    
    [Signal]
    public delegate void LobbySealedEventHandler();

    public void ConnectToUrl(string url) {
        Close();
        code = 1000;
        reason = "Unknown";
        ws.ConnectToUrl(url);
    }

    public void Close() {
        ws.Close();
    }

    public override void _Process(double delta) {
        ws.Poll();

        WebSocketPeer.State state = ws.GetReadyState();
        if (state != oldState && state == WebSocketPeer.State.Open && autoJoin) {
            JoinLobby(lobby);
        }

        while (state == WebSocketPeer.State.Open && ws.GetAvailablePacketCount() > 0) {
            if (!ParseMessage()) {
                GD.PushError("Error parsing malformed message form server.");
            }
        }

        if (state != oldState && state == WebSocketPeer.State.Closed) {
            code = ws.GetCloseCode();
            reason = ws.GetCloseReason();
            EmitSignal(SignalName.Disconnected);
        }

        oldState = state;
    }

    private bool ParseMessage() {
        Variant parsedVariant = Json.ParseString(ws.GetPacket().GetStringFromUtf8());

        // Make sure godot is not being special
        if (parsedVariant.GetType() != typeof(Godot.Collections.Dictionary)) {
            return false;
        }

        Godot.Collections.Dictionary parsed = (Godot.Collections.Dictionary)parsedVariant;

        // Check packet structure
        if (!parsed.ContainsKey("type") || !parsed.ContainsKey("id") || !parsed.ContainsKey("data")) {
            return false;
        }

        // Make sure there is data
        Variant dataVariant;
        if (parsed.TryGetValue("data", out dataVariant) || dataVariant.GetType() != typeof(string)) {
            return false;
        }

        int type;
        int sourceId;

        if (!int.TryParse((string)parsed["type"], out type) || !int.TryParse((string)parsed["id"], out sourceId)) {
            return false;
        }

        switch ((Message)type)
        {
            case Message.ID:
                EmitSignal(SignalName.Connected, sourceId, (string)parsed["data"] == "true");
                break;

            case Message.JOIN:
                EmitSignal(SignalName.LobbyJoined, (string)parsed["data"]);
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
                EmitSignal(SignalName.OfferReceived, sourceId, (string)parsed["data"]);
                break;

            case Message.ANSWER:
                EmitSignal(SignalName.AnswerRecived, sourceId, (string)parsed["data"]);
                break;
            
            case Message.CANDIDATE:
                string[] candidate = ((string)parsed["data"]).Split(new char[] {'\n'}, StringSplitOptions.None);
                
                // Verify candidate
                if (candidate.Length != 3) {
                    return false;
                }
                if (!int.TryParse(candidate[1], out _)) {
                    return false;
                }

                EmitSignal(SignalName.CandidateRecived, sourceId, candidate[0], candidate[1].ToInt(), candidate[2]);
                break;

            default:
                return false;
        }

        return true;
    }

    public int JoinLobby(string lobby) {
        return SendMessage(Message.JOIN, mesh ? 0 : 1, lobby);
    }

    public int SendCandidate(int id, string mid, int index, string sdp) {
        return SendMessage(Message.CANDIDATE, id, $"\n{mid}\n{index}\n{sdp}");
    }

    public int SendOffer(int id, string offer) {
        return SendMessage(Message.OFFER, id, offer);
    }

    public int SendAnswer(int id, string answer) {
        return SendMessage(Message.ANSWER, id, answer);
    }

    private int SendMessage(Message type, int id, string data="") {
        Variant message = new Godot.Collections.Dictionary<string, Variant>
        {
            {"type", (int)type},
            {"id", id},
            {"data", data}
        };

        return (int)ws.SendText(Json.Stringify(message));
    }
}