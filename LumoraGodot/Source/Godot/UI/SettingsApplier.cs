// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Source.Godot.Bootstrap;
using Lumora.Source.Godot.Rendering;

namespace Lumora.Source.UI;

// Applies the engine-owned settings the platform layer is responsible for: vsync, frame cap, window
// mode, 3D render scale and the master audio bus. Changes apply live; EngineSettings saves them itself.
public partial class SettingsApplier : Node
{
	public override void _Ready()
	{
		base._Ready();
		EngineSettings.Load();
		EngineSettings.Changed += Apply;
		XRModeManager.ModeChanged += OnXrModeChanged;
		// Turning or unfolding a phone hands the window a different panel.
		if (AndroidDeviceMode.IsPhone)
			GetTree().Root.SizeChanged += ApplyHandheldResolution;
		Apply();
	}

	public override void _ExitTree()
	{
		EngineSettings.Changed -= Apply;
		XRModeManager.ModeChanged -= OnXrModeChanged;
		if (AndroidDeviceMode.IsPhone && GetTree()?.Root is { } root)
			root.SizeChanged -= ApplyHandheldResolution;
		base._ExitTree();
	}

	// Differential apply: Changed fires for every settings write (including each
	// slider tick), so only touch the subsystems whose value actually changed -
	// reassigning render scale or vsync per tick stalls the renderer.
	private int? _appliedMaxFps;
	private bool? _appliedFullscreen;
	private float? _appliedRenderScale;
	private float? _appliedMeshLodThreshold;
	private bool? _appliedDynamicResolution;
	private int? _appliedUpscaler;
	private int? _appliedResolutionHeight;
	// What the chosen resolution costs the 3D buffer, 1.0 at native. Multiplied into the render-scale
	// ceiling so it composes with the user's Render Scale and with dynamic resolution instead of
	// fighting them.
	private float _resolutionScale = 1f;
	private float? _appliedUserHeight;
	private int? _appliedBackgroundFps;
	private Lumora.Godot.Hooks.SkyEnvironment.PostSettings? _appliedPost;
	private int? _appliedAntiAliasing;
	private int? _appliedShadowQuality;
	private string? _appliedInputDevice;
	private string? _appliedOutputDevice;

	// When the window loses focus or is minimized the compositor stops blocking the swap, so vsync no longer throttles
	// us and the loop free-runs (the FPS graph spikes way past the vsync rate, burning GPU for a window nobody sees).
	// Engine.MaxFps is enforced regardless of focus or vsync, so while unfocused we clamp to the user's background-fps
	// setting and restore their normal limit when focus returns. -xlinka
	private bool _background;

	// A camera on the window is there to be recorded, and the recorder is what has focus, so the background cap
	// stays off while it is up. -xlinka
	private bool _feedCapLifted;

	// --Lumora-Debug opens the console as a second window, and that is the window a person profiling
	// is looking at, so the game window sits unfocused for most of a capture. With the background cap
	// in force every such capture measured the frame limiter, not the frame: in the first clean
	// scratch-space profile 116 of 365 samples were exactly 33.33 ms, the cap's 30 fps, on a 74 Hz
	// display, and the console booked that sleep as time Godot spent. A profiling run keeps the
	// foreground rate whether or not the window has focus. Same for a frame-log run. -xlinka
	private static readonly bool ProfilingRun =
		LaunchArgs.HasFlag(LaunchArgs.DebugFlag) || LaunchArgs.ReadValue(FrameTimeLog.PathFlag) != null;
	private bool _backgroundThrottled;

	// A frame-log run measures the frame, so it draws with vsync off and no frame cap: a vsynced log is a
	// log of the refresh interval and a capped one of the limiter. --Lumora-VSync still picks the swap
	// for a run that wants to measure exactly that. -xlinka
	private static readonly bool FrameLogRun = LaunchArgs.ReadValue(FrameTimeLog.PathFlag) != null;
	private bool _frameLogRunNoted;

	// VSYNC
	//
	// EngineSettings.VSync is the user's on/off. How the swap waits when it is on is a second choice,
	// and it decides the frame rate of every frame that cannot make the refresh interval. FIFO, Godot's
	// "Enabled", blocks the whole main loop until the next vertical blank, so a frame that misses one
	// interval pays for a full second one: 21 ms of work on a 74 Hz display becomes a 27 ms frame, on
	// 60 Hz a 33 ms one. Mailbox keeps vsync, no tearing, but lets the loop run at its real cost and
	// scans out the newest finished frame at each blank. Adaptive tears instead of waiting when a
	// frame is late. The typed settings live in the core, so the choice is read from the generic
	// store under Engine.Video.VSyncMode ("fifo", "mailbox", "adaptive"); a missing key is fifo,
	// exactly what shipped. --Lumora-VSync=<off|on|fifo|mailbox|adaptive> overrides the lot for one
	// run, for measurements. Vulkan falls back to FIFO on a surface that cannot do the mode asked for.
	//
	// A headset owns frame timing while it renders: the OpenXR frame wait paces the loop and a
	// blocking desktop swap on top of it would hold the headset to the monitor's refresh. The XR
	// bring-up disables vsync for that reason, and this used to put it straight back on at boot
	// because it only compared against its own cache, so a VR boot ran with the mirror window
	// vsynced. In VR the answer is always off; after F8 back to desktop the user's choice applies
	// again. -xlinka
	private const string VSyncModeKey = "Engine.Video.VSyncMode";
	private const string VSyncOverrideFlag = "--Lumora-VSync";
	private static readonly string? VSyncOverride = LaunchArgs.ReadValue(VSyncOverrideFlag);
	private DisplayServer.VSyncMode? _appliedVsyncMode;

	private void Apply()
	{
		ApplyVSync();

		if (_appliedMaxFps != EngineSettings.MaxFps || _appliedBackgroundFps != EngineSettings.BackgroundFps)
		{
			_appliedMaxFps = EngineSettings.MaxFps;
			_appliedBackgroundFps = EngineSettings.BackgroundFps;
			ApplyEffectiveFps();
			Lumora.Core.Logging.Logger.Debug($"Settings: fps limit -> {(EngineSettings.MaxFps == 0 ? "off" : EngineSettings.MaxFps.ToString())} (background {(EngineSettings.BackgroundFps == 0 ? "off" : EngineSettings.BackgroundFps.ToString())})");
		}

		if (_appliedFullscreen != EngineSettings.Fullscreen)
		{
			_appliedFullscreen = EngineSettings.Fullscreen;
			if (!OS.HasFeature("android"))
				DisplayServer.WindowSetMode(EngineSettings.Fullscreen
					? DisplayServer.WindowMode.Fullscreen
					: DisplayServer.WindowMode.Windowed);
			ApplyResolution();
		}

		if (_appliedResolutionHeight != EngineSettings.ResolutionHeight)
		{
			_appliedResolutionHeight = EngineSettings.ResolutionHeight;
			ApplyResolution();
			Lumora.Core.Logging.Logger.Debug($"Settings: resolution -> {EngineSettings.DescribeResolutionHeight(EngineSettings.ResolutionHeight)}");
		}

		if (_appliedRenderScale != EngineSettings.RenderScale)
		{
			_appliedRenderScale = EngineSettings.RenderScale;
			ApplyRenderScaleCeiling();
		}

		if (_appliedDynamicResolution != EngineSettings.DynamicResolution)
		{
			_appliedDynamicResolution = EngineSettings.DynamicResolution;
			// Back to exactly what the user asked for the moment it is switched off.
			ApplyRenderScaleCeiling();
			Lumora.Core.Logging.Logger.Debug($"Settings: dynamic resolution -> {(EngineSettings.DynamicResolution ? "on" : "off")}");
		}

		if (_appliedUpscaler != EngineSettings.Upscaler)
		{
			_appliedUpscaler = EngineSettings.Upscaler;
			ApplyUpscaler();
			Lumora.Core.Logging.Logger.Debug($"Settings: upscaler -> {EngineSettings.DescribeUpscaler(EngineSettings.Upscaler)}");
		}

		// The whole post stack lives on the same Environment, so one changed value rewrites the set
		// rather than a separate walk over every claimed environment per setting.
		var post = new Lumora.Godot.Hooks.SkyEnvironment.PostSettings
		{
			Bloom = EngineSettings.Bloom,
			AmbientOcclusion = EngineSettings.AmbientOcclusion,
			ScreenSpaceReflections = EngineSettings.ScreenSpaceReflections,
			ScreenSpaceIndirectLighting = EngineSettings.ScreenSpaceIndirectLighting,
		};
		if (!_appliedPost.HasValue || !_appliedPost.Value.Equals(post))
		{
			// The screen-space passes run at the engine's Medium quality with two blur passes unless
			// told otherwise, and that is where the grain and the crawling on moving bodies comes from:
			// this occlusion has no temporal filter, only its blur. High with an extra pass is the
			// cheapest fix, still at half resolution. A global, so once. -xlinka
			if (!_appliedPost.HasValue && !OS.HasFeature("android"))
			{
				RenderingServer.EnvironmentSetSsaoQuality(RenderingServer.EnvironmentSsaoQuality.High, true, 0.5f, 3, 50f, 300f);
				RenderingServer.EnvironmentSetSsilQuality(RenderingServer.EnvironmentSsilQuality.High, true, 0.5f, 4, 50f, 300f);
			}
			_appliedPost = post;
			Lumora.Godot.Hooks.SkyEnvironment.ApplyPostSettings(in post, this);
			string rendering = Lumora.Godot.Hooks.SkyEnvironment.DescribeRendering(this);
			string postLine = $"Settings: post -> (bloom {post.Bloom:0.##}, ao {post.AmbientOcclusion:0.##}, "
				+ $"ssil {(post.ScreenSpaceIndirectLighting ? "on" : "off")}, ssr {(post.ScreenSpaceReflections ? "on" : "off")}); "
				+ rendering;
			// Every slider tick lands here. The line earns the log only when what renders is not the
			// environment the settings went into. -xlinka
			if (rendering.Contains("DIFFERS", System.StringComparison.Ordinal))
				Lumora.Core.Logging.Logger.Log(postLine);
			else
				Lumora.Core.Logging.Logger.Debug(postLine);
		}

		if (_appliedAntiAliasing != EngineSettings.AntiAliasing)
		{
			_appliedAntiAliasing = EngineSettings.AntiAliasing;
			var viewport = GetViewport();
			if (viewport != null && !viewport.UseXR && !HeadsetOwnsFrameTiming())
			{
                ViewportQuality.ApplyAntiAliasing(viewport, EngineSettings.AntiAliasing);
			}
			Lumora.Core.Logging.Logger.Debug($"Settings: desktop anti-aliasing -> {EngineSettings.DescribeAntiAliasing(EngineSettings.AntiAliasing)}"
				+ (HeadsetOwnsFrameTiming() ? " (VR quality profile remains active)" : string.Empty));
		}

		if (_appliedShadowQuality != EngineSettings.ShadowQuality)
		{
			_appliedShadowQuality = EngineSettings.ShadowQuality;
			// Desktop preferences must not replace the standalone shadow budget at boot or on Save.
			// Our soft presets start at Godot's Medium sample count. Soft Low leaves a visible
			// stipple on avatar shadows without TAA, and the desktop/VR viewports use MSAA.
			// Keep the stored preset indices: existing Low selections also get the cleaner filter.
			var quality = OS.HasFeature("android") ? RenderingServer.ShadowQuality.Hard : EngineSettings.ShadowQuality switch
			{
				0 => RenderingServer.ShadowQuality.Hard,
				2 => RenderingServer.ShadowQuality.SoftHigh,
				3 => RenderingServer.ShadowQuality.SoftUltra,
				_ => RenderingServer.ShadowQuality.SoftMedium,
			};
			RenderingServer.PositionalSoftShadowFilterSetQuality(quality);
			RenderingServer.DirectionalSoftShadowFilterSetQuality(quality);
			Lumora.Core.Logging.Logger.Debug($"Settings: shadow quality -> {(OS.HasFeature("android") ? "Hard (standalone)" : EngineSettings.DescribeShadowQuality(EngineSettings.ShadowQuality))}");
		}

		if (_appliedMeshLodThreshold != EngineSettings.MeshLodThreshold)
		{
			_appliedMeshLodThreshold = EngineSettings.MeshLodThreshold;
			ApplyMeshLodThreshold(GetTree()?.Root, EngineSettings.MeshLodThreshold);
			Lumora.Core.Logging.Logger.Debug($"Settings: mesh LOD threshold -> {EngineSettings.DescribeMeshLodThreshold(EngineSettings.MeshLodThreshold)}");
		}

		// Master volume is the mixer's own gain now (AudioSystem folds it into the device output every
		// frame); applying it to the platform bus as well would square it. -xlinka

		ApplyAudioDevices();

		if (_appliedUserHeight != EngineSettings.UserHeight)
		{
			_appliedUserHeight = EngineSettings.UserHeight;
			// Feed the calibrated height to the input layer; AvatarIK.MaybeRescaleAvatar reads it and re-scales the
			// avatar so its eye height matches (live). -xlinka
			var input = Lumora.Core.Engine.Current?.InputInterface;
			if (input != null)
				input.UserHeight = EngineSettings.UserHeight;
		}
	}

	// Which microphone and which speakers. Nothing is cached until the platform seam is actually there,
	// so a change made before the engine finished booting is applied when it is rather than being
	// recorded as already done. -xlinka
	private void ApplyAudioDevices()
	{
		var devices = Lumora.Core.Engine.Current?.AudioSystem?.DevicePlatform;
		if (devices == null)
			return;

		if (_appliedInputDevice != EngineSettings.AudioInputDevice)
		{
			_appliedInputDevice = EngineSettings.AudioInputDevice;
			devices.ApplyInput(_appliedInputDevice);
		}

		if (_appliedOutputDevice != EngineSettings.AudioOutputDevice)
		{
			_appliedOutputDevice = EngineSettings.AudioOutputDevice;
			devices.ApplyOutput(_appliedOutputDevice);
		}
	}

	private void OnXrModeChanged(bool vrActive)
	{
		// A session can remain initialized in desktop mode. Restore current preferences, including
		// edits made in VR, rather than leaving the cached pre-VR settings on the root viewport.
		_appliedAntiAliasing = null;
		_smoothedFrame = 0;
		_sinceDecision = 0;
		ApplyRenderScaleCeiling();
		ApplyUpscaler();
		Apply();
		ApplyEffectiveFps();
	}

	private static DisplayServer.VSyncMode ResolveVSyncMode()
	{
		if (HeadsetOwnsFrameTiming())
			return DisplayServer.VSyncMode.Disabled;

		string choice = VSyncOverride
			?? (FrameLogRun ? "off" : null)
			?? (EngineSettings.VSync ? Settings.ReadValue<string>(VSyncModeKey, "fifo") : "off");

		return (choice ?? "fifo").Trim().ToLowerInvariant() switch
		{
			"off" or "disabled" or "0" or "false" => DisplayServer.VSyncMode.Disabled,
			"mailbox" => DisplayServer.VSyncMode.Mailbox,
			"adaptive" => DisplayServer.VSyncMode.Adaptive,
			_ => DisplayServer.VSyncMode.Enabled,
		};
	}

	// Compared against what was last asked for, not what the window reports: a mode the surface
	// cannot do falls back to FIFO inside Godot, and comparing against the window would then re-ask
	// for it (a swapchain rebuild) on every settings write.
	private void ApplyVSync()
	{
		var mode = ResolveVSyncMode();
		if (_appliedVsyncMode == mode)
			return;
		_appliedVsyncMode = mode;

		if (DisplayServer.WindowGetVsyncMode() != mode)
			DisplayServer.WindowSetVsyncMode(mode);

		Lumora.Core.Logging.Logger.Debug($"Settings: vsync -> {DescribeVSync(mode)}"
			+ (VSyncOverride != null ? $" ({VSyncOverrideFlag}={VSyncOverride})" : string.Empty)
			+ (HeadsetOwnsFrameTiming() ? " (headset paces the frame)" : string.Empty));
	}

	private static string DescribeVSync(DisplayServer.VSyncMode mode) => mode switch
	{
		DisplayServer.VSyncMode.Disabled => "off",
		DisplayServer.VSyncMode.Mailbox => "on (mailbox)",
		DisplayServer.VSyncMode.Adaptive => "on (adaptive)",
		_ => "on",
	};

	// The headset is rendering (VR mode active), not merely holding a session: after F8 to desktop the
	// OpenXR session stays up but no viewport draws through it, so the monitor's swap is the only
	// pacing left and the user's vsync choice applies again.
	private static bool HeadsetOwnsFrameTiming()
	{
		var manager = XRModeManager.Instance;
		if (manager != null && GodotObject.IsInstanceValid(manager))
			return manager.IsVRActive;
		return IsVrActive();
	}

	// RESOLUTION
	//
	// Windowed, this resizes the window, which is what picking 1080p in any other game does. Fullscreen,
	// the window stays at the desktop resolution (Godot's fullscreen is borderless, it does not take the
	// display mode) and the 3D buffer renders at the chosen height instead. That is the half that costs
	// the framerate on a 4k screen, and it leaves the interface rendering at native so text stays sharp
	// rather than being upscaled with the world. -xlinka
	private void ApplyResolution()
	{
		if (OS.HasFeature("android"))
		{
			// A headset's eye resolution comes from OpenXR, never the desktop window preference.
			if (AndroidDeviceMode.IsPhone)
				ApplyHandheldResolution();
			return;
		}

		int height = EngineSettings.ResolutionHeight;
		var screen = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());
		if (screen.Y <= 0)
			return;

		if (height <= 0 || height >= screen.Y)
		{
			_resolutionScale = 1f;
			ApplyRenderScaleCeiling();
			return;
		}

		if (EngineSettings.Fullscreen)
		{
			_resolutionScale = (float)height / screen.Y;
		}
		else
		{
			_resolutionScale = 1f;
			int width = (int)System.Math.Round(height * ((float)screen.X / screen.Y));
			DisplayServer.WindowSetSize(new Vector2I(System.Math.Max(width, 640), System.Math.Max(height, 480)));
		}

		ApplyRenderScaleCeiling();
	}

	// A phone never renders the world at its panel's resolution. These panels are dense (the Fold's inner
	// one is 2184x1968): far more 3D pixels than the GPU fills at a steady rate, and more than the eye
	// separates at arm's length. The world gets at most three quarters of the panel and never more than
	// 1080 lines; the interface stays native, and Render Scale and dynamic resolution work under that.
	// -xlinka
	private const int HandheldMaxRenderLines = 1080;
	private const float HandheldMaxPanelFraction = 0.75f;

	private void ApplyHandheldResolution()
	{
		var window = DisplayServer.WindowGetSize();
		int lines = System.Math.Min(window.X, window.Y);
		if (lines <= 0)
			return;
		float scale = System.Math.Min(HandheldMaxPanelFraction, (float)HandheldMaxRenderLines / lines);
		bool changed = System.Math.Abs(scale - _resolutionScale) > 0.001f;
		_resolutionScale = scale;
		ApplyRenderScaleCeiling();
		if (changed)
			Lumora.Core.Logging.Logger.Log($"Settings: phone panel {window.X}x{window.Y}, world renders at {scale * 100f:0}% before Render Scale");
	}

	// The ceiling everything else works under: the user's Render Scale, narrowed by whatever the chosen
	// resolution asks for. Dynamic resolution rides below this; with it off, this IS the scale.
	private float RenderScaleCeiling => EngineSettings.RenderScale * _resolutionScale;

	private void ApplyRenderScaleCeiling()
	{
		_dynamicScale = RenderScaleCeiling;
		var root = GetTree()?.Root;
		if (root != null && !root.UseXR && !HeadsetOwnsFrameTiming())
			root.Scaling3DScale = _dynamicScale;
	}

	// FSR is a Forward+ feature and it only does anything below 1.0 scale, which is exactly what the
	// controller below produces on a big window. XR stays on bilinear: the mobile renderer our
	// standalone build uses has neither mode, and FSR2 through OpenXR is a known Godot bug where the
	// image simply does not improve, so a headset toggle would be a lie. -xlinka
	private void ApplyUpscaler()
	{
		var root = GetTree()?.Root;
		if (root == null || root.UseXR || HeadsetOwnsFrameTiming())
			return;

		// Not offered on Android, and a choice synced from a PC must not turn FSR 2 on there.
		root.Scaling3DMode = OS.HasFeature("android") ? Viewport.Scaling3DModeEnum.Bilinear : EngineSettings.Upscaler switch
		{
			1 => Viewport.Scaling3DModeEnum.Fsr,
			2 => Viewport.Scaling3DModeEnum.Fsr2,
			_ => Viewport.Scaling3DModeEnum.Bilinear
		};
	}

	// ADAPTIVE RESOLUTION
	//
	// Nothing scaled with the window before this: Scaling3DScale was written once from the user's
	// setting, so a 4k fullscreen window rendered four times the pixels of 1080p and simply ran at
	// whatever framerate that cost. Now the user's RenderScale is the CEILING and this walks the
	// actual scale down when frames run long, back up when they do not.
	//
	// Falls fast and rises slow on purpose: dropping resolution the frame after a stall is what keeps
	// the number up, while creeping back adds pixels only once the headroom has held for a while, so
	// the picture does not pulse. The floor is deliberately not lower than half, past which the image
	// is worse than the framerate is worth. -xlinka
	private const float ScaleFloor = 0.5f;
	private const float ScaleFallStep = 0.05f;
	private const float ScaleRiseStep = 0.02f;
	private const double DecideInterval = 0.4;

	private float _dynamicScale = 1f;
	private double _smoothedFrame;
	private double _sinceDecision;
	private bool _measuring;

	public override void _Process(double delta)
	{
		base._Process(delta);

		if (XRModeManager.FeedOnWindow != _feedCapLifted)
		{
			_feedCapLifted = XRModeManager.FeedOnWindow;
			ApplyEffectiveFps();
		}

		if (!EngineSettings.DynamicResolution || _background)
			return;

		// XR owns its own render target scale (see ViewportQuality); do not fight it.
		if (HeadsetOwnsFrameTiming())
			return;

		var root = GetTree()?.Root;
		if (root == null || root.UseXR || delta <= 0.0)
			return;

		// MEASURED render time, not the frame delta. With vsync on, delta sits exactly on the refresh
		// interval whether we are coasting or barely keeping up, so a delta-driven controller can never
		// see headroom and would ratchet the resolution down and leave it there. The viewport reports
		// what the frame actually cost. -xlinka
		var viewportRid = root.GetViewportRid();
		if (!_measuring)
		{
			RenderingServer.ViewportSetMeasureRenderTime(viewportRid, true);
			_measuring = true;
			return;
		}

		double cost = System.Math.Max(
			RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewportRid),
			RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewportRid)) / 1000.0;
		if (cost <= 0.0)
			return;   // nothing measured yet, or the driver will not report it: leave the scale alone

		_smoothedFrame = _smoothedFrame <= 0.0 ? cost : _smoothedFrame + (cost - _smoothedFrame) * 0.1;

		_sinceDecision += delta;
		if (_sinceDecision < DecideInterval)
			return;
		_sinceDecision = 0.0;

		// What we are trying to hit: the user's frame cap if they set one, otherwise the display's own
		// refresh rate, which is the rate vsync is going to hold us to anyway.
		double targetFps = EngineSettings.MaxFps > 0 ? EngineSettings.MaxFps : ScreenRefreshRate();
		if (targetFps < 30.0)
			targetFps = 30.0;
		double target = 1.0 / targetFps;

		float ceiling = RenderScaleCeiling;
		float previous = _dynamicScale;

		// Rendering is not the only thing in a frame, so aim it at most of the budget rather than all of
		// it, and only add pixels back when it is comfortably under.
		if (_smoothedFrame > target * 0.85)
			_dynamicScale -= ScaleFallStep;
		else if (_smoothedFrame < target * 0.60)
			_dynamicScale += ScaleRiseStep;

		if (_dynamicScale < ScaleFloor) _dynamicScale = ScaleFloor;
		if (_dynamicScale > ceiling) _dynamicScale = ceiling;

		if (Mathf.Abs(_dynamicScale - previous) > 0.001f)
			root.Scaling3DScale = _dynamicScale;
	}

	private static double ScreenRefreshRate()
	{
		float hz = DisplayServer.ScreenGetRefreshRate(DisplayServer.WindowGetCurrentScreen());
		// The server answers -1 on displays it cannot read.
		return hz > 1f ? hz : 60.0;
	}

	public override void _Notification(int what)
	{
		base._Notification(what);
		switch (what)
		{
			// Both the application-level and per-window focus signals fire on desktop; the transition guard in
			// SetBackground makes the duplicate harmless and covers platforms that only emit one of them. -xlinka
			case (int)NotificationApplicationFocusOut:
			case (int)NotificationWMWindowFocusOut:
				SetBackground(true);
				break;
			case (int)NotificationApplicationFocusIn:
			case (int)NotificationWMWindowFocusIn:
				SetBackground(false);
				break;
		}
	}

	private void SetBackground(bool background)
	{
		Lumora.Core.Engine.Current?.InputInterface?.ReportWindowFocus(!background);
		if (_background == background)
		{
			return;
		}

		_background = background;
		ApplyEffectiveFps();
	}

	// Resolves Engine.MaxFps from the user's cap and the current focus state. While unfocused (and not in VR, where the
	// headset compositor owns frame timing) we clamp to the user's background-fps setting; a background setting of 0
	// disables the throttle. In VR, OpenXR owns pacing and both desktop frame caps are suspended.
	//
	// The transitions are logged: a capture that ran at the background rate should say so in the log
	// next to the settings it ran with, instead of leaving a 30 fps mystery for the profiler.
	private void ApplyEffectiveFps()
	{
		int userCap = FrameLogRun ? 0 : EngineSettings.MaxFps;
		bool inVr = HeadsetOwnsFrameTiming();
		if (FrameLogRun && !_frameLogRunNoted)
		{
			_frameLogRunNoted = true;
			Lumora.Core.Logging.Logger.Log($"Settings: frame log run, vsync {DescribeVSync(ResolveVSyncMode())} and no frame cap"
				+ (VSyncOverride != null ? $" ({VSyncOverrideFlag}={VSyncOverride})" : string.Empty));
		}
		// A desktop cap of 60 must not hold a 72/90/120 Hz headset below its refresh rate.
		int effective = inVr ? 0 : userCap;
		bool throttled = false;

		if (_background && !inVr && !ProfilingRun && !_feedCapLifted)
		{
			int backgroundCap = EngineSettings.BackgroundFps;
			if (backgroundCap > 0)
			{
				effective = userCap == 0 ? backgroundCap : System.Math.Min(userCap, backgroundCap);
				throttled = effective != userCap;
			}
		}

		global::Godot.Engine.MaxFps = effective;

		if (throttled != _backgroundThrottled)
		{
			_backgroundThrottled = throttled;
			Lumora.Core.Logging.Logger.Debug(throttled
				? $"Settings: window unfocused, fps capped to {effective} until focus returns"
				: "Settings: window focused, background fps cap lifted");
		}
		else if (_background && !throttled && ProfilingRun && EngineSettings.BackgroundFps > 0 && !inVr)
		{
			Lumora.Core.Logging.Logger.Debug("Settings: window unfocused, background fps cap suspended for the profiling run");
		}
	}

	// The threshold is a per-viewport property, and the XR sub-viewport is a different one from the
	// window's. Walk what is in the tree rather than naming them, so a mirror or capture viewport that
	// renders 3D gets the same setting instead of quietly staying at the project default. Viewports
	// created after this runs inherit that project default until the next change. -xlinka
	private static void ApplyMeshLodThreshold(Node? node, float threshold)
	{
		if (node == null)
			return;

		if (node is Viewport viewport)
			viewport.MeshLodThreshold = threshold;

		foreach (var child in node.GetChildren())
			ApplyMeshLodThreshold(child, threshold);
	}

	private static bool IsVrActive()
		=> XRServer.FindInterface("OpenXR")?.IsInitialized() == true;
}
