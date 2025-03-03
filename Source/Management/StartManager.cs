using System;
using System.Linq;
using Aquamarine.Source.Logging;
using Godot;

namespace Aquamarine.Source.Management
{
    public partial class StartManager : Node
    {
        [Export] public Label Text;
        public override void _Ready()
        {
            base._Ready();
            try
            {
                var isServer = ServerManager.CurrentServerType is not ServerManager.ServerType.NotAServer;

                Text.Text = isServer.ToString();
                Logger.Log($"Application started in {(isServer ? "server" : "client")} mode.");

                // Defer scene change to avoid modifying the scene tree immediately
                CallDeferred(nameof(ChangeScene), isServer);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in StartManager: {ex.Message}");
            }
        }

        private void ChangeScene(bool isServer)
        {
            GetTree().ChangeSceneToFile(isServer ? "res://Scenes/Server.tscn" : "res://Scenes/Client.tscn");
            Logger.Log($"Scene changed to {(isServer ? "Server.tscn" : "Client.tscn")}.");
        }
    }
}