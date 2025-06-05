using System;
using Godot;
using LumoraVR.Source.Logging;
using System.Diagnostics;
using DiscordRPC;

namespace LumoraVR.Source.Management
{
    public partial class DiscordManager : Node
    {
        public static DiscordManager Instance;
        private DiscordRpcClient _discord;
        private bool _isInitialized;
        private readonly Stopwatch _uptime = new();
        private const string ClientId = "1330400493054464092";

        public override void _Ready()
        {
            Instance = this;
            InitializeDiscord();
        }

        public void InitializeDiscord()
        {
            try
            {
                _discord = new DiscordRpcClient(ClientId);
                _discord.Initialize();
                _isInitialized = true;
                _uptime.Start();
                Logger.Log("Discord manager initialized successfully");
                UpdatePresence("Beta Testing", "In Game", "lumoravralpha", "LumoraVR");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize Discord: {ex.Message}");
                _isInitialized = false;
            }
        }

        private double _updateTimer;
        public override void _Process(double delta)
        {
            if (!_isInitialized) return;

            try
            {
                _discord.Invoke();
                _updateTimer += delta;
                if (_updateTimer >= 60.0)
                {
                    _updateTimer = 0;
                    UpdatePresence("Beta Testing", "In Game", "lumoravralpha", "LumoraVR");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Discord callback error: {ex.Message}");
                _isInitialized = false;
            }
        }

        public void UpdatePresence(string details, string state, string largeImage, string largeText)
        {
            if (!_isInitialized) return;

            try
            {
                var presence = new RichPresence
                {
                    Details = details,
                    State = state,
                    Assets = new DiscordRPC.Assets
                    {
                        LargeImageKey = largeImage,
                        LargeImageText = largeText,
                        SmallImageKey = "lumoravrbeta_tester",
                        SmallImageText = "Lumora Beta Tester"
                    },
                    Timestamps = new Timestamps
                    {
                        StartUnixMilliseconds = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)_uptime.Elapsed.TotalMilliseconds)
                    }
                };
                _discord.SetPresence(presence);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error updating Discord presence: {ex.Message}");
            }
        }

        public override void _ExitTree()
        {
            if (_isInitialized)
            {
                _discord.Dispose();
            }
        }
    }
}
