using System;
using System.Linq;
using Aquamarine.Source.Helpers;
using Aquamarine.Source.Logging;
using Aquamarine.Source.Scene;
using Aquamarine.Source.Scene.RootObjects;
using Godot;
using Godot.Collections;
using Array = Godot.Collections.Array;

// namespace Aquamarine.Source.Management;

namespace Aquamarine.Source.Management
{
    public partial class MultiplayerScene : Node
    {
        public static MultiplayerScene Instance;

        [Export] public MultiplayerSpawner Spawner;
        [Export] public MultiplayerSpawner PlayerSpawner;
        [Export] public Node3D PlayerRoot;

        public System.Collections.Generic.Dictionary<int, PlayerInfo> PlayerList = new();
        public System.Collections.Generic.Dictionary<string, Prefab> Prefabs = new();

        public PlayerCharacterController GetPlayer(int id)
        {
            var tryFind = PlayerRoot.GetChildren().OfType<PlayerCharacterController>().FirstOrDefault(i => i.Authority == id);
            return tryFind;
        }

        public PlayerCharacterController GetLocalPlayer()
        {
            if (IsInstanceValid(_local)) return _local;

            if (Multiplayer.MultiplayerPeer is null) return _local;

            var local = Multiplayer.GetUniqueId();
            _local = PlayerRoot.GetChildren().OfType<PlayerCharacterController>().FirstOrDefault(i => i.Authority == local);

            return _local;
        }

        private PlayerCharacterController _local;

        public override void _Ready()
        {
            base._Ready();
            Instance = this;
            Logger.Log("MultiplayerScene initialized.");
        }
        public void SendUpdatedPlayerList()
        {
            if (!IsMultiplayerAuthority()) return;

            var dict = new Dictionary();

            foreach (var playerPair in PlayerList)
            {
                dict[playerPair.Key] = new Dictionary
                {
                    {"name", playerPair.Value.Name },
                };
            }

            Rpc(MethodName.UpdatePlayerList, dict);

            UpdatePlayerNametags();
        }

        private void UpdatePlayerNametags()
        {
            foreach (var player in PlayerList)
            {
                var controller = GetPlayer(player.Key);
                if (controller is null) continue;
                //GD.Print(player.Value.Name);
                controller.Nametag.Text = player.Value.Name;
            }
        }

        [Rpc(CallLocal = false, TransferChannel = SerializationHelpers.WorldUpdateChannel, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
        private void UpdatePlayerList(Dictionary playerList)
        {
            var newList = new System.Collections.Generic.Dictionary<int, PlayerInfo>();

            foreach (var pair in playerList)
            {
                var valueDict = pair.Value.AsGodotDictionary();

                var player = new PlayerInfo
                {
                    Name = valueDict["name"].AsString(),
                };
                newList[pair.Key.AsInt32()] = player;
            }

            PlayerList = newList;

            UpdatePlayerNametags();
        }
        [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferChannel = SerializationHelpers.SessionControlChannel, CallLocal = false)]
        public void DisconnectPlayer()
        {
            if (!IsMultiplayerAuthority()) return;

            var id = Multiplayer.GetRemoteSenderId();
            Logger.Log($"Player {id} has disconnected");

            Multiplayer.MultiplayerPeer.DisconnectPeer(id);
        }
        [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferChannel = SerializationHelpers.SessionControlChannel, CallLocal = false)]
        public void SetPlayerName(string name)
        {
            if (!IsMultiplayerAuthority()) return;

            if (!PlayerList.TryGetValue(Multiplayer.GetRemoteSenderId(), out var player)) return;

            player.Name = name;
            SendUpdatedPlayerList();
        }

        public void SendAllPrefabs(int? user = null)
        {
            if (!IsMultiplayerAuthority()) return;

            foreach (var prefab in Prefabs) SendPrefab(prefab.Key, user);
        }
        public void SendPrefab(string prefabName, int? user = null)
        {
            if (user.HasValue) RpcId(user.Value, MethodName.RecievePrefab, prefabName);
            else Rpc(MethodName.RecievePrefab, prefabName);
        }
        [Rpc(TransferChannel = SerializationHelpers.PrefabChannel)]
        private void RecievePrefab(string prefabName)
        {

        }

        public void RequestPrefab(string prefabName)
        {

        }

        //[Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SerializationHelpers.WorldUpdateChannel)]
        private void InternalSpawnPlayer(int authority, Vector3 position)
        {
            try
            {
                var charController = PlayerCharacterController.PackedScene.Instantiate<PlayerCharacterController>();
                charController.SetPlayerAuthority(authority);
                charController.Name = authority.ToString();
                PlayerRoot.AddChild(charController);
                PlayerRoot.GlobalPosition = position;
                Logger.Log($"Player with authority {authority} spawned at position {position}.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error spawning player with authority {authority}: {ex.Message}");
            }
        }

        //[Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = SerializationHelpers.WorldUpdateChannel)]
        private void InternalRemovePlayer(int authority)
        {
            try
            {
                var charController = PlayerRoot.GetChildren().FirstOrDefault(i => i is PlayerCharacterController cont && cont.Authority == authority);
                if (charController is null)
                {
                    Logger.Warn($"Attempted to remove player with authority {authority}, but they were not found.");
                    return;
                }
                PlayerRoot.RemoveChild(charController);
                charController.QueueFree();
                Logger.Log($"Player with authority {authority} removed.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error removing player with authority {authority}: {ex.Message}");
            }
        }
        public void SpawnPlayer(int authority, Vector3? position = null)
        {
            if (IsMultiplayerAuthority())
            {
                InternalSpawnPlayer(authority, position ?? Vector3.Zero);
                //Rpc(MethodName.InternalSpawnPlayer, authority, position ?? Vector3.Zero);
                Logger.Log($"SpawnPlayer called for authority {authority}.");
            }
            else
            {
                Logger.Warn($"SpawnPlayer call ignored due to lack of multiplayer authority for authority {authority}.");
            }
        }

        public void RemovePlayer(int authority)
        {
            if (IsMultiplayerAuthority())
            {
                InternalRemovePlayer(authority);
                //Rpc(MethodName.InternalRemovePlayer, authority);
                Logger.Log($"RemovePlayer called for authority {authority}.");
            }
            else
            {
                Logger.Warn($"RemovePlayer call ignored due to lack of multiplayer authority for authority {authority}.");
            }
        }
    }
}
