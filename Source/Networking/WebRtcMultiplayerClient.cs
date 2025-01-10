using Godot;
using Godot.Collections;
using Aquamarine.Source.Logging;

namespace Aquamarine.Source.Networking;

public partial class WebRtcMultiplayerClient : WebsocketSignalingClient
{
	private WebRtcMultiplayerPeer rtcPeer = new WebRtcMultiplayerPeer();
	private bool rtcSealed = false;

	public WebRtcMultiplayerClient()
	{
		Connected += OnConnected;
		Disconnected += OnDisconnected;

		OfferReceived += OnOfferReceived;
		AnswerReceived += OnAnswerRecived;
		CandidateReceived += OnCandidateRecived;

		LobbyJoined += OnLobbyJoined;
		LobbySealed += OnLobbySealed;
		PeerConnected += OnPeerConnected;
		PeerDisconnected += OnPeerDisconnected;
	}

	public void Start(string url, string _lobby = "", bool _mesh = true)
	{
		Stop();
		rtcSealed = false;
		mesh = _mesh;
		lobby = _lobby;
		ConnectToUrl(url);
	}

	public void Stop()
	{
		Multiplayer.MultiplayerPeer = null;
		rtcPeer.Close();
		Close();
	}

	private WebRtcPeerConnection CreatePeer(int id)
	{
		WebRtcPeerConnection peer = new WebRtcPeerConnection();

		Dictionary peerConfiguration = new Dictionary
		{
			{ "iceServers", new Dictionary
				{
					{ "urls", new Array
						{
							"stun:stun.l.google.com:19302",
							"stun:stun.2.google.com:19302",
							"stun:stun.3.google.com:19302",
							"stun:stun.4.google.com:19302"
						}
					}
				}
			}
		};

		peer.Initialize(peerConfiguration);
		peer.SessionDescriptionCreated += (type, data) => OfferCreated(type, data, id);
		peer.IceCandidateCreated += (mid, index, name) => NewIceCandadte(mid, index, name, id); // NOTE: Keep and eye on this skechy type cast :3c

		rtcPeer.AddPeer(peer, id);
		if (id < rtcPeer.GetUniqueId())
		{
			peer.CreateOffer();
		}

		return peer;
	}

	private void NewIceCandadte(string midName, long indexName, string sdpName, int id)
	{
		SendCandidate(id, midName, indexName, sdpName);
	}

	private void OfferCreated(string type, string data, int id)
	{
		if (!rtcPeer.HasPeer(id))
		{
			return;
		}

		rtcPeer.GetPeer(id)["connection"].As<WebRtcPeerConnection>().SetLocalDescription(type, data);

		if (type == "offer")
		{
			SendOffer(id, data);
		}
		else
		{
			SendAnswer(id, data);
		}
	}

	private void OnConnected(int id, bool useMesh)
	{
		Logger.Log($"Connected {id}; Using mesh:{mesh}");

		if (useMesh)
		{
			rtcPeer.CreateMesh(id);
		}
		else if (id == 1)
		{
			rtcPeer.CreateServer();
		}
		else
		{
			rtcPeer.CreateClient(id);
		}

		Multiplayer.MultiplayerPeer = rtcPeer;
	}

	private void OnLobbyJoined(string Onlobby)
	{
		lobby = Onlobby;
	}

	private void OnLobbySealed()
	{
		rtcSealed = true;
	}

	private void OnDisconnected()
	{
		Logger.Log($"Disconnected with code: {code}: \"{reason}\"");

		if (!rtcSealed)
		{
			Logger.Log("A non-gracefull disconnect occured, Cleaning up.");
			Stop();
		}
	}

	private void OnPeerConnected(int id)
	{
		Logger.Log($"Peer connected: {id}");

		CreatePeer(id);
	}

	private void OnPeerDisconnected(int id)
	{
		Logger.Log($"Peer disconnected: {id}");

		if (rtcPeer.HasPeer(id))
		{
			rtcPeer.RemovePeer(id);
		}
	}

	private void OnOfferReceived(int id, string offer)
	{
		Logger.Log($"Got offer: {id}");
		if (rtcPeer.HasPeer(id))
		{
			rtcPeer.GetPeer(id)["connection"].As<WebRtcPeerConnection>().SetRemoteDescription("offer", offer);
		}
	}

	private void OnAnswerRecived(int id, string answer)
	{
		Logger.Log($"Got answer: {id}");
		if (rtcPeer.HasPeer(id))
		{
			rtcPeer.GetPeer(id)["connection"].As<WebRtcPeerConnection>().SetRemoteDescription("answer", answer);
		}
	}

	private void OnCandidateRecived(int id, string mid, int index, string sdp)
	{
		if (rtcPeer.HasPeer(id))
		{
			Logger.Log($"Got candidate: mid: {mid}, index: {index}, sdp: {sdp}");
			rtcPeer.GetPeer(id)["connection"].As<WebRtcPeerConnection>().AddIceCandidate(mid, index, sdp);
		}
	}
}
