using LumoraVR.Source.Logging;
using LumoraVR.Source.Management;
using LumoraVR.Source.Management.Data;
using Godot;
using Godot.NativeInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
namespace LumoraVR.Source.Management.Client.UI;
public partial class WorldsTab : Control
{
    /* this api is stupid and we need to do a "big rewrite".. Mabye
       we may need a redis for sessions (or somting similer) may try realm or dragonfly
    */
    private readonly Dictionary<string, Data.WorldEntry> _sessions = new();
    public IReadOnlyDictionary<string, Data.WorldEntry> Sessions { get; private set; }
    private Node _holdernode;
    private Node _worldnameText;
    private Node _activeSessionsList;
    private Control _contextContainer;
    private readonly System.Net.Http.HttpClient client = new();
    private readonly PeriodicTimer timer = new(new TimeSpan(0, 0, 20));
    private PackedScene _worldEntry;
    private PackedScene _sessionEntry;
    public delegate void SessionListUpdate();
    public event SessionListUpdate SessionListUpdated;
    public override void _Ready()
    {
        base._Ready();
        Sessions = _sessions.AsReadOnly();
        _worldEntry = ResourceLoader.Load<PackedScene>("res://Scenes/UI/Manu/WorldTab/WorldEntry.tscn");
        _sessionEntry = ResourceLoader.Load<PackedScene>("res://Scenes/UI/Manu/WorldTab/session_instance.tscn");
        _holdernode = GetNode("%WorldHolder");
        _worldnameText = GetNode("%WorldName");
        _activeSessionsList = GetNode("%SeessionList");
        _contextContainer = GetNode<Control>("%ContextContainer");
        Task.Run(async () => { while (true) { await GetSessions(); await timer.WaitForNextTickAsync(); } });
    }
    public override void _EnterTree()
    {
        base._EnterTree();
        if (_contextContainer is Control ctrl)
        {
            ctrl.Hide();
        }
        if (SessionListUpdated is not null)
            SessionListUpdated();
    }
    private async Task GetSessions()
    {
        CancellationTokenSource can = new();
        Task<string> result = client.GetStringAsync(SessionInfo.SessionList, can.Token);
        Task delay = Task.Delay(8000);
        Task done = await Task.WhenAny(result, delay);
        if (done is not Task<string> jsontask)
        {
            can.Cancel();
            Logger.Error("Failed to get session list");
            return;
        }
        string json = jsontask.Result;
        Dictionary<string, List<SessionInfo>> infos = new();
        Dictionary<string, string> worldnames = new();
        var list = System.Text.Json.JsonSerializer.Deserialize<List<SessionInfo>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        list.ForEach(sessionInfo =>
        {
            if (sessionInfo.WorldIdentifier == "") return;
            if (!infos.TryGetValue(sessionInfo.WorldIdentifier, out List<SessionInfo> sessionsForWorld))
            {
                sessionsForWorld = new();
                infos.Add(sessionInfo.WorldIdentifier, sessionsForWorld);
                worldnames.Add(sessionInfo.WorldIdentifier, sessionInfo.Name);
            }
            sessionsForWorld.Add(sessionInfo);
        });
        HashSet<string> keys = new HashSet<string>(_sessions.Keys);
        _sessions.Clear();
        foreach (var item in infos)
        {
            if (!worldnames.TryGetValue(item.Key, out string name))
                continue;
            _sessions.Add(item.Key, new Data.WorldEntry(item.Key, name, item.Value.ToArray()));
            if (_worldEntry is not null && !keys.Contains(item.Key))
            {
                WorldEntry entry = (WorldEntry)_worldEntry.Instantiate();
                entry.assignEvent(this, item.Key);
                SemaphoreSlim sem = new(0, 1);
                Action add = () =>
                {
                    _holdernode.AddChild(entry);
                    sem.Release();
                };
                var tree = GetTree();
                tree.ProcessFrame += add;
                await sem.WaitAsync();
                tree.ProcessFrame -= add;

            }
        }
        if (SessionListUpdated is not null)
            SessionListUpdated();
    }
    /// <summary>
    /// Loads the world info into the details modal
    /// 
    /// Called by the details button on the world entry
    /// </summary>
    /// <param name="worldid"></param>
    internal void LoadWorldInfo(string worldid)
    {
        if (_contextContainer is Control con)
            con.Show();
        if (!_sessions.TryGetValue(worldid, out var worldEntry)) return;
        if (_worldnameText is RichTextLabel text)
            text.Text = $"{worldEntry.WorldName}";
        if (_activeSessionsList is not null)
        {
            foreach (Node child in _activeSessionsList.GetChildren())
                child.QueueFree();

            foreach (var session in worldEntry.Sessions)
            {
                if (_sessionEntry is null) break;
                var inst = _sessionEntry.Instantiate<SessionInstance>();
                _activeSessionsList.AddChild(inst);
                inst.UpdateData(session, this);
            }

        }
    }
    // TODO Fix later
    /// <summary>
    /// Joins the session with the given id
    /// </summary>
    /// <param name="id"></param>
    internal async void joinSession(string id)
    {
        bool connected = false;
        MultiplayerApi.PeerConnectedEventHandler updateconnected = (long id) => connected = true;
        Multiplayer.PeerConnected += updateconnected;
        var cm = ClientManager.Instance;
        cm.JoinNatServer(id);
        await Task.Delay(5000);
        if (!connected)
            cm.JoinNatServerRelay(id);
    }
}
