using Godot;
using System;
using Aquamarine.Source.Management.World;

namespace Aquamarine.Source.Management.World
{
    public partial class LoadingWorldUI : Control
    {
        [Export] private Label _worldNameLabel;
        [Export] private ProgressBar _progressBar;
        [Export] private Label _statusLabel;

        private string _currentWorldId = "";

        public override void _Ready()
        {
            if (_worldNameLabel == null)
                _worldNameLabel = GetNode<Label>("LoadingPanel/VBoxContainer/WorldName");

            if (_progressBar == null)
                _progressBar = GetNode<ProgressBar>("LoadingPanel/VBoxContainer/MarginContainer/ProgressBar");

            if (_statusLabel == null)
                _statusLabel = GetNode<Label>("LoadingPanel/VBoxContainer/StatusLabel");

            _progressBar.Value = 0;
            _statusLabel.Text = "Preparing...";

            if (WorldManager.Instance != null)
            {
                WorldManager.Instance.WorldLoadingStarted += OnWorldLoadingStarted;
                WorldManager.Instance.WorldLoadingProgress += OnWorldLoadingProgress;
                WorldManager.Instance.WorldLoadingCompleted += OnWorldLoadingCompleted;
                WorldManager.Instance.WorldLoadingFailed += OnWorldLoadingFailed;
            }
        }

        private void OnWorldLoadingStarted(string worldId)
        {
            _currentWorldId = worldId;

            string displayName = GetDisplayName(worldId);
            _worldNameLabel.Text = displayName;

            _progressBar.Value = 0;
            _statusLabel.Text = $"Loading {displayName}...";
        }

        private void OnWorldLoadingProgress(string worldId, float progress)
        {
            if (worldId != _currentWorldId) return;

            _progressBar.Value = progress * 100f;

            if (progress < 0.5f)
                _statusLabel.Text = "Loading assets...";
            else if (progress < 0.8f)
                _statusLabel.Text = "Processing world data...";
            else
                _statusLabel.Text = "Finalizing...";
        }

        private void OnWorldLoadingCompleted(string worldId)
        {
            if (worldId != _currentWorldId) return;

            _progressBar.Value = 100;
            _statusLabel.Text = "Load complete!";
        }

        private void OnWorldLoadingFailed(string worldId, string error)
        {
            if (worldId != _currentWorldId) return;

            _progressBar.Value = 0;
            _statusLabel.Text = $"Error: {error}";
        }

        private string GetDisplayName(string worldId)
        {
            return worldId switch
            {
                "local_home" => "Home Space",
                "multiplayer_base" => "Multiplayer Base",
                _ => worldId.Replace("_", " ").ToTitleCase()
            };
        }

        public override void _ExitTree()
        {
            if (WorldManager.Instance != null)
            {
                WorldManager.Instance.WorldLoadingStarted -= OnWorldLoadingStarted;
                WorldManager.Instance.WorldLoadingProgress -= OnWorldLoadingProgress;
                WorldManager.Instance.WorldLoadingCompleted -= OnWorldLoadingCompleted;
                WorldManager.Instance.WorldLoadingFailed -= OnWorldLoadingFailed;
            }
        }
    }

    public static class StringExtensions
    {
        public static string ToTitleCase(this string str)
        {
            if (string.IsNullOrEmpty(str))
                return string.Empty;

            var words = str.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (string.IsNullOrEmpty(words[i]))
                    continue;

                char[] letters = words[i].ToCharArray();
                letters[0] = char.ToUpper(letters[0]);
                words[i] = new string(letters);
            }

            return string.Join(" ", words);
        }
    }
}