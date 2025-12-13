using Godot;
using System;

namespace Aquamarine.Source.Godot.UI;

/// <summary>
/// Professional loading UI for engine initialization.
/// Displays progress bar, status text, and phase transitions during boot.
/// </summary>
public partial class LoadingScreen : Control
{
	// ===== UI NODE REFERENCES =====
	private Label _statusLabel;
	private Label _percentageLabel;
	private ProgressBar _progressBar;
	private AnimationPlayer _animationPlayer;
	private Control _loadingSpinner;

	// ===== STATE =====
	private float _targetProgress = 0f;
	private float _currentProgress = 0f;
	private bool _isVisible = true;
	private bool _fadeOutQueued = false;

	// ===== CONFIGURATION =====
	private const float PROGRESS_SMOOTH_SPEED = 2.0f; // How fast progress bar animates
	private const float SPINNER_ROTATION_SPEED = 2.0f; // Radians per second

	public override void _Ready()
	{
		// IMPORTANT: Make immediately visible - no fade in!
		// This prevents the "flash" where screen is empty before animation starts
		Visible = true;
		Modulate = Colors.White; // Full opacity immediately
		_isVisible = true;

		// Cache node references
		_statusLabel = GetNode<Label>("CenterContainer/VBoxContainer/ProgressContainer/StatusLabel");
		_percentageLabel = GetNode<Label>("CenterContainer/VBoxContainer/ProgressContainer/PercentageLabel");
		_progressBar = GetNode<ProgressBar>("CenterContainer/VBoxContainer/ProgressContainer/ProgressBarContainer/ProgressBar");



		// Initialize progress
		UpdateProgressDisplay(0f);
	}

	public override void _Process(double delta)
	{
		// Smoothly interpolate progress bar
		if (_currentProgress < _targetProgress)
		{
			_currentProgress = Mathf.MoveToward(_currentProgress, _targetProgress, (float)delta * PROGRESS_SMOOTH_SPEED * 100f);
			UpdateProgressDisplay(_currentProgress);
		}

		// Rotate spinner
		if (_loadingSpinner != null && _isVisible)
		{
			_loadingSpinner.Rotation += (float)delta * SPINNER_ROTATION_SPEED;
		}
	}

	/// <summary>
	/// Update loading progress (0-100).
	/// </summary>
	public void SetProgress(float percentage)
	{
		_targetProgress = Mathf.Clamp(percentage, 0f, 100f);
	}

	/// <summary>
	/// Update status text message.
	/// </summary>
	public void SetStatus(string status)
	{
		if (_statusLabel != null)
		{
			_statusLabel.Text = status;
		}
	}

	/// <summary>
	/// Update progress for a specific initialization phase.
	/// Automatically calculates percentage and sets appropriate message.
	/// </summary>
	public void SetPhase(int phaseNumber, int totalPhases, string phaseName)
	{
		// Calculate percentage (each phase is equal weight)
		float phaseProgress = (phaseNumber / (float)totalPhases) * 100f;
		SetProgress(phaseProgress);
		SetStatus($"[{phaseNumber}/{totalPhases}] {phaseName}");
	}

	/// <summary>
	/// Hide the loading screen with fade-out animation.
	/// </summary>
	public void Hide()
	{
		if (!_isVisible)
			return;

		_isVisible = false;
		Visible = false;
		QueueFree(); // Remove immediately since animation player was removed
	}

	/// <summary>
	/// Show the loading screen with fade-in animation.
	/// </summary>
	public new void Show()
	{
		if (_isVisible)
			return;

		_isVisible = true;
		Visible = true;
	}

	/// <summary>
	/// Update progress bar and percentage label.
	/// </summary>
	private void UpdateProgressDisplay(float percentage)
	{
		if (_progressBar != null)
		{
			_progressBar.Value = percentage;
		}

		if (_percentageLabel != null)
		{
			_percentageLabel.Text = $"{Mathf.RoundToInt(percentage)}%";
		}
	}

	/// <summary>
	/// Called when animation finishes (for cleanup).
	/// </summary>
	private void _on_animation_finished(StringName animName)
	{
		// AnimationPlayer removed; keep handler to avoid errors if signal still exists
	}

	// ===== PHASE-SPECIFIC HELPER METHODS =====

	/// <summary>
	/// Predefined status messages for each initialization phase.
	/// Makes it easy for LumoraEngineRunner to call without knowing exact text.
	/// </summary>
	public static class PhaseMessages
	{
		public const string EnvironmentSetup = "Setting up environment...";
		public const string XRDetection = "Detecting VR hardware...";
		public const string HeadOutputCreation = "Initializing rendering system...";
		public const string EngineCoreInit = "Starting Aquamarine Engine...";
		public const string SystemIntegration = "Connecting input and audio systems...";
		public const string UserspaceSetup = "Loading user interface...";
		public const string Ready = "Ready!";
	}

	/// <summary>
	/// Shorthand method to update phase using enum index.
	/// </summary>
	public void UpdatePhase(int phaseIndex, string customMessage = null)
	{
		string[] defaultMessages = new[]
		{
			PhaseMessages.EnvironmentSetup,      // Phase 0
			PhaseMessages.XRDetection,            // Phase 1
			PhaseMessages.HeadOutputCreation,     // Phase 2
			PhaseMessages.EngineCoreInit,         // Phase 3
			PhaseMessages.SystemIntegration,      // Phase 4
			PhaseMessages.UserspaceSetup,         // Phase 5
			PhaseMessages.Ready                   // Phase 6
		};

		int totalPhases = 6; // Not counting "Ready" as a phase
		string message = customMessage ?? (phaseIndex < defaultMessages.Length ? defaultMessages[phaseIndex] : "Initializing...");

		if (phaseIndex >= totalPhases)
		{
			// Final phase - set to 100%
			SetProgress(100f);
			SetStatus(message);
		}
		else
		{
			// Regular phase update
			SetPhase(phaseIndex + 1, totalPhases, message);
		}
	}
}
