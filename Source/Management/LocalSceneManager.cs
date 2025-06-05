using LumoraVR.Source.Logging;
using LumoraVR.Source.Scene.RootObjects;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LumoraVR.Source.Management
{
    public partial class LocalSceneManager : Node
    {
        public static LocalSceneManager Instance;

        [Export] private PackedScene _localPlayerScene;
        [Export] private NodePath _spawnPointPath;

        private Node3D _spawnPoint;
        private PlayerCharacterController _localPlayer;

        public override void _Ready()
        {
            Instance = this;
            _spawnPoint = GetNode<Node3D>(_spawnPointPath);

            SpawnLocalPlayer();

            Logger.Log("LocalSceneManager initialized.");
        }

        private void SpawnLocalPlayer()
        {
            _localPlayer = _localPlayerScene.Instantiate<PlayerCharacterController>();
            _localPlayer.SetPlayerAuthority(1);
            _localPlayer.Name = "LocalPlayer";
            AddChild(_localPlayer);

            if (_spawnPoint != null)
            {
                _localPlayer.GlobalPosition = _spawnPoint.GlobalPosition;
            }

            Logger.Log("Local player spawned successfully.");
        }
    }
}
