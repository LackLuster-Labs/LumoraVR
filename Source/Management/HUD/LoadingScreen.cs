using Godot;
using System;

namespace LumoraVR.Source.Management.HUD;

/// <summary>
/// Manages the loading screen UI and animations.
/// </summary>
public partial class LoadingScreen : Control
{
    [Export] private ProgressBar _progressBar;
    [Export] private Label _statusLabel;
    [Export] private AnimationPlayer _animationPlayer;

    public double Progress
    {
        get => _progressBar.Value;
        set => _progressBar.Value = value;
    }

    public string StatusText
    {
        get => _statusLabel.Text;
        set => _statusLabel.Text = value;
    }

    public override void _Ready()
    {
        base._Ready();
        Visible = false;
        _progressBar.Value = 0;
        _statusLabel.Text = "Loading...";
    }

    /// <summary>
    /// Shows the loading screen with an optional initial status message.
    /// </summary>
    /// <param name="statusMessage">The status message to display</param>
    public void Show(string statusMessage = "Loading...")
    {
        _statusLabel.Text = statusMessage;
        _progressBar.Value = 0;
        Visible = true;
        if (_animationPlayer.HasAnimation("fade_in"))
        {
            _animationPlayer.Play("fade_in");
        }
    }

    /// <summary>
    /// Updates the loading progress and optional status message.
    /// </summary>
    /// <param name="progress">Progress value between 0 and 100</param>
    /// <param name="statusMessage">Optional new status message</param>
    public void UpdateProgress(float progress, string statusMessage = null)
    {
        _progressBar.Value = progress;

        if (statusMessage != null)
        {
            _statusLabel.Text = statusMessage;
        }
    }

    /// <summary>
    /// Hides the loading screen with an optional fade out animation.
    /// </summary>
    new public void Hide()
    {
        if (_animationPlayer.HasAnimation("fade_out"))
        {
            _animationPlayer.Play("fade_out");
            _animationPlayer.AnimationFinished += OnFadeOutFinished;
        }
        else
        {
            Visible = false;
        }
    }

    private void OnFadeOutFinished(StringName animName)
    {
        if (animName == "fade_out")
        {
            Visible = false;
            _animationPlayer.AnimationFinished -= OnFadeOutFinished;
        }
    }
}
