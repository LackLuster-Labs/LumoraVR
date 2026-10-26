// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Nexus.Transport.LNL;
using Lumora.Core.Networking.Sync;
#if GODOT_PC
using Lumora.Godot.Networking.Transports.Steam;
#endif
using Lumora.Core.Components.Meshes;
using Lumora.Core.Templates;
using Lumora.Source.Godot.Input.Drivers;
using Lumora.Godot.Hooks;
using Lumora.Source.Godot.Rendering;
using Lumora.Source.Godot.UI;
using Lumora.Source.Input;
using Lumora.Source.UI;
using Lumora.Godot.Input;
using Lumora.Godot.Debug;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

using ThreadingMutex = System.Threading.Mutex;
using InspectorInputHandler = Lumora.Source.Input.InspectorInputHandler;

namespace Lumora.Source.Godot.Bootstrap;

public partial class LumoraEngineRunner : Node
{
	private const string DebugFlag = LaunchArgs.DebugFlag;
	private const string DebugConsoleFlag = "--Lumora-DebugConsole";
	private const string DebugConsoleScenePath = "res://Scenes/UI/Debug/DebugWindow.tscn";
	private const string DebugConsoleMutexName = "Lumora.DebugConsole.SingleInstance";
	private const double DebugPerfSendIntervalSec = 0.25;
	private const double DebugMemorySendIntervalSec = 0.5;

	// CONFIGURATION
	[Export] public bool VerboseInit { get; set; } = false;
	[Export] public bool DumpSceneTreeOnReady { get; set; } = false;
	[Export] public bool AutoHostLocalHome { get; set; } = true;
	[Export] public bool AutoConnectLocalHome { get; set; } = true;
	[Export] public int LocalHomePort { get; set; } = 44844;
	[Export] public int MaximumXrRefreshRate { get; set; } = 90;
	private const float StandaloneMaxRefreshRate = 72f;

	// CORE SYSTEMS
	private Lumora.Core.Engine _engine = null!;
	private HeadOutput _headOutput = null!;
	private Lumora.Source.Godot.UI.DashScreenOverlay _dashOverlay = null!;
	private SystemInfoHook _systemInfoHook = null!;
	private InputInterface _inputInterface = null!;
	private LoadingScreen _loadingScreen = null!;

	// INPUT DRIVERS
	private GodotMouseDriver _mouseDriver = null!;
	private GodotKeyboardDriver _keyboardDriver = null!;
	private GodotGamepadDriver _gamepadDriver = null!;
	private GodotVRDriver _vrDriver = null!;
	private SRanipalDriver? _sranipalDriver;
	private ClipboardImporter _clipboardImporter = null!;
	private LocalDB _localDB = null!;
	private InspectorInputHandler _inspectorInputHandler = null!;
	private DebugUdpSender? _debugUdpSender;
	private FrameTimeLog? _frameLog;
	private readonly PhysicsStepPacer _stepPacer = new();

	// STATE
	private bool _engineInitialized = false;
	private bool _shutdownRequested = false;
	private bool _missingInputInterfaceWarned = false;
	private double _debugPerfTimer;
	private double _debugMemoryTimer;
	private double _debugRenderProfileTimer;
	private double _debugFrameTimer;
	private double _debugNetworkTimer;
	private readonly List<Lumora.Core.UpdateManager.ProfileEntry> _profByTypeBuffer = new();
	private readonly List<Lumora.Core.UpdateManager.ProfileEntry> _profBySlotBuffer = new();
	private InitializationPhase _currentPhase = InitializationPhase.EnvironmentSetup;
	// Auto: try to detect a headset on every launch; falls back to Desktop gracefully.
	private XrLaunchMode _xrLaunchMode = XrLaunchMode.Auto;

	// XRModeManager handles F8 runtime switching between Desktop and VR.
	private XRModeManager _xrModeManager = null!;
	private ThreadingMutex? _debugConsoleInstanceMutex;
	private bool _ownsDebugConsoleLock;
	private OpenXRInterface _openXRInterface = null!;
	private bool _openXRSignalsConnected;
	private bool _xrIsFocused;
	private static readonly Dictionary<Type, long> ComponentMemoryEstimateCache = new();

	// SCENE REFERENCES
	private Node3D _inputRoot = null!;
	private Camera3D _mainCamera = null!;
	private Viewport _xrViewport = null!;

	// Where the headset renders from. On a standalone headset XR goes on the ROOT viewport; on desktop
	// the dedicated SubViewport stays so the monitor can show the DesktopCamera mirror while the
	// headset shows the XR camera.
	//
	// Measured on a Pico 4, same scene, 1.3x eye bWWWWWWuffer: root viewport 13.3 ms GPU at a flat 72 fps;
	// SubViewport 12.2 ms GPU at 62 fps. Less GPU work and fewer frames, because with the SubViewport
	// the root window is still presented every frame and the Android surface cannot disable vsync
	// ("V-Sync mode Disabled is not available" at boot), so the present blocks on the flat display
	// instead of the headset. With XR on the root, the frame goes to OpenXR and that wait is gone.
	// --xr-subviewport forces the other path on standalone, --xr-root forces this one on desktop. -xlinka
	// Root viewport on standalone only. Tried on desktop 2026-09-16: the Virtual Desktop runtime
	// refused the root viewport's swapchain ("Failed to get swapchain images", RUNTIME_FAILURE) and
	// the headset went dark, while the SubViewport path has always worked there. An XR texture array
	// cannot be assigned to a TextureRect either, so XRModeManager attaches the XR SubViewport to the
	// window instead and OpenXR copies the left eye onto it; the window renders no world of its own. -xlinka
	private static bool UseRootViewportForXr
		=> XRViewportLayout.UseRootViewport;
	private bool _vrInitializedAtBoot;
	private double _discordPresenceTimer;
	private double _refreshRatePollTimer;

	// Fallback for image formats the engine's own decoder cannot read - WebP above all, which imported
	// avatars use constantly. Godot decodes it here and hands back straight RGBA rows in the same
	// top-down order everything else uses.
	// An animated WebP as one vertical strip of composited frames. Each frame's bitstream is a still
	// the decoder accepts; the canvas is carried between frames the way the file says (blend or
	// replace, keep or clear), and every Nth frame is kept so the strip stays under the texture cap.
	private static byte[]? DecodeAnimatedWithGodot(byte[] encoded, out int width, out int height, out int frames, out float fps)
	{
		width = 0;
		height = 0;
		frames = 1;
		fps = 0f;
		if (!Lumora.Core.Assets.WebpStill.TryExtractFrames(encoded, out int canvasWidth, out int canvasHeight, out var list) || list.Count == 0
			|| canvasWidth <= 0 || canvasHeight <= 0)
			return null;

		const int MaxFrames = 48;
		const int MaxStripHeight = 4096;
		int limit = System.Math.Max(1, System.Math.Min(MaxFrames, MaxStripHeight / canvasHeight));
		int step = System.Math.Max(1, (int)System.Math.Ceiling(list.Count / (double)limit));
		int selected = 0;
		for (int i = 0; i < list.Count; i += step) selected++;

		var clear = new Color(0f, 0f, 0f, 0f);
		using var canvas = Image.CreateEmpty(canvasWidth, canvasHeight, false, Image.Format.Rgba8);
		canvas.Fill(clear);
		using var strip = Image.CreateEmpty(canvasWidth, canvasHeight * selected, false, Image.Format.Rgba8);
		strip.Fill(clear);

		double totalMs = 0;
		int placed = 0;
		Rect2I previousRect = default;
		bool previousDispose = false;
		for (int i = 0; i < list.Count; i++)
		{
			var frame = list[i];
			if (previousDispose)
				canvas.FillRect(previousRect, clear);

			using (var picture = new Image())
			{
				if (picture.LoadWebpFromBuffer(frame.Still) == Error.Ok)
				{
					if (picture.GetFormat() != Image.Format.Rgba8)
						picture.Convert(Image.Format.Rgba8);
					var source = new Rect2I(0, 0, picture.GetWidth(), picture.GetHeight());
					var at = new Vector2I(frame.X, frame.Y);
					if (frame.BlendAlpha)
						canvas.BlendRect(picture, source, at);
					else
						canvas.BlitRect(picture, source, at);
				}
			}

			previousRect = new Rect2I(frame.X, frame.Y, frame.Width, frame.Height);
			previousDispose = frame.DisposeToBackground;
			if (i % step == 0 && placed < selected)
			{
				strip.BlitRect(canvas, new Rect2I(0, 0, canvasWidth, canvasHeight), new Vector2I(0, placed * canvasHeight));
				placed++;
			}
			totalMs += frame.DurationMs;
		}

		if (placed == 0)
			return null;
		width = canvasWidth;
		height = canvasHeight * placed;
		frames = placed;
		double averageMs = totalMs / list.Count * step;
		fps = averageMs > 0 ? (float)System.Math.Clamp(1000.0 / averageMs, 1.0, 50.0) : 10f;
		return strip.GetData();
	}

	private static byte[]? DecodeImageWithGodot(byte[] encoded, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (encoded == null || encoded.Length == 0)
			return null;

		using var image = new Image();
		var error = Lumora.Godot.Helpers.GodotImageLoader.Load(image, encoded);
		if (error != Error.Ok)
			return null;

		// KTX and DDS arrive already block-compressed; the engine side wants bytes it can scan and
		// resample, and Convert refuses a compressed source. Decompress needs the matching decoder in
		// this build, so a failure here is a real "cannot read", not a crash.
		if (image.IsCompressed() && image.Decompress() != Error.Ok)
			return null;

		if (image.GetFormat() != Image.Format.Rgba8)
			image.Convert(Image.Format.Rgba8);
		// A DDS carries its mip chain; the caller wants the top level only and builds its own.
		if (image.HasMipmaps())
			image.ClearMipmaps();

		width = image.GetWidth();
		height = image.GetHeight();
		return image.GetData();
	}

	// EXR only. The engine decodes Radiance itself; EXR needs the platform's reader, and that reader
	// is bound in this Godot build but lives in a module that not every export template carries.
	// A missing module comes back as an error from the load call, which is the honest outcome: the
	// texture fails to load with a reason instead of tone-mapping to 8-bit behind the user's back.
	// Output is RGBA half floats, 8 bytes per texel, top-down rows. -xlinka
	private static byte[]? DecodeHdrImageWithGodot(byte[] encoded, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (encoded == null || encoded.Length == 0)
			return null;

		using var image = new Image();
		var error = image.LoadExrFromBuffer(encoded);
		if (error != Error.Ok)
		{
			LumoraLogger.Warn($"LumoraEngineRunner: EXR decode unavailable on this build ({error})");
			return null;
		}

		if (image.GetFormat() != Image.Format.Rgbah)
			image.Convert(Image.Format.Rgbah);

		width = image.GetWidth();
		height = image.GetHeight();
		return image.GetData();
	}

	private void LogCallback(LumoraLogger.LogLevel level,String message){
		switch(level){
			case LumoraLogger.LogLevel.ERROR:
				GD.PushError(message);
				break;
			case LumoraLogger.LogLevel.WARN:
				GD.PushWarning(message);
				break;
			default:
				GD.Print(message);
				break;
		}
	}
	private enum InitializationPhase
	{
		EnvironmentSetup,
		XRDetection,
		HeadOutputCreation,
		EngineCoreInit,
		SystemIntegration,
		UserspaceSetup,
		Ready
	}

	// defaults to desktop to avoid launching OpenXR runtimes unexpectedly
	private enum XrLaunchMode
	{
		Desktop,
		Auto,
		Vr
	}

	private double _hostDiscordMs;

	public override void _Ready()
	{
		if (DiscordManager.ExitAfterHandoff)
			return;
		LumoraLogger.LogToGameEngine += LogCallback;
		// --local-services talks to services on this machine, accounts on :5000 and assets on :5001, instead
		// of the live ones. -xlinka
		if (LaunchArgs.HasFlag("--local-services"))
		{
			Lumora.Nexus.Cloud.Cdn.ServiceConfig.UseEnvironment(Lumora.Nexus.Cloud.Cdn.ServiceEnvironment.Development);
			var services = Lumora.Nexus.Cloud.Cdn.ServiceConfig.Current;
			LumoraLogger.Log($"LumoraEngineRunner: local services, accounts at {services.ApiBase}, assets at {services.ContentBase}");
		}
		Lumora.Core.Assets.TextureVariantStore.PlatformDecoder = DecodeImageWithGodot;
		Lumora.Core.Assets.TextureVariantStore.AnimatedDecoder = DecodeAnimatedWithGodot;
		Lumora.Core.Assets.TextureVariantStore.PlatformHdrDecoder = DecodeHdrImageWithGodot;
		Lumora.Core.Networking.AssetFetcher.ResourcePackReader = Lumora.Godot.Helpers.ResourcePackReader.Read;
		// Godot object picking logs in stereo and is not part of engine-side interaction. - xlinka
		GetViewport().PhysicsObjectPicking = false;

		// Every Node3D transform is written once per render frame from the engine update below
		// (input -> world update -> slot flush -> camera), all before the frame renders. Physics
		// interpolation assumes transforms only change on the fixed physics tick and smears every
		// engine-driven node between stale tick snapshots while the camera, which opts out, stays
		// crisp: hands, held objects, the avatar and the context menu all jittered under it. The
		// project setting says the same, but the editor rewrites project.godot without comments and
		// the last write folded that setting into a junk key, so the rule is enforced here. -xlinka
		GetTree().PhysicsInterpolation = false;

#if GODOT_PC
		if (ShouldUseSteam() && !SteamManager.Initialize())
		{
			GetTree().Quit();
			return;
		}
#endif

		if (HasCommandLineFlag(DebugConsoleFlag))
		{
			if (!TryAcquireDebugConsoleLock())
			{
				LumoraLogger.Log("LumoraEngineRunner: Debug console already running, skipping duplicate launch");
				GetTree().Quit();
				return;
			}

			LumoraLogger.Log("LumoraEngineRunner: Starting in debug console mode");
			CallDeferred(nameof(SwitchToDebugConsoleScene));
			return;
		}

		LumoraLogger.Log("LumoraEngineRunner: Starting engine bootstrap...");

		if (HasCommandLineFlag(DebugFlag))
		{
			_debugUdpSender = new DebugUdpSender();
			// Per-receiver input timing: the particle sim, cloth, dynamic bones and both IK solvers all
			// run through that dispatch, and without this they are an unattributed lump inside the sync
			// phase. Same opt-in gate as the update profiler. -xlinka
			Lumora.Core.Input.InputInterface.ProfileReceivers = true;
			// Per-slot/per-component update profiling carries a small per-frame cost, so only turn it on when the
			// debug console is actually attached (--lumora-debug). Normal runs pay nothing. -xlinka
			Lumora.Core.UpdateManager.ProfilingEnabled = true;
			LaunchDebugConsoleProcess();
		}

		_frameLog = FrameTimeLog.FromCommandLine();

		InitializeLoadingScreen();

		CallDeferred(MethodName.StartInitialization);
	}

	private void SwitchToDebugConsoleScene()
	{
		var result = GetTree().ChangeSceneToFile(DebugConsoleScenePath);
		if (result != Error.Ok)
		{
			LumoraLogger.Error($"DebugConsole: failed to open scene '{DebugConsoleScenePath}' ({result})");
		}
	}

	// One way to run this executable as a service instead of a client, meant for --headless:
	//   --publish-builtins=<key>   push the build's own assets up by hash and write builtins.json
	// The key is the service's worker key. It opens no world on its own; the engine's local home still
	// comes up underneath, which costs little and keeps one startup path. Variants are made by whoever
	// saves, never by a service, so there is no variant worker. -xlinka
	private static string? ReadCommandLineValue(string flag) => LaunchArgs.ReadValue(flag);

	private void StartServiceModes()
	{
		var client = _engine?.CDNClient;
		if (client == null)
			return;

		var publishKey = ReadCommandLineValue("--publish-builtins");
		if (!string.IsNullOrEmpty(publishKey))
		{
			var root = _engine!.ResourceRoot;
			LumoraLogger.Log($"LumoraEngineRunner: publishing built-in assets from {root}");
			_ = Task.Run(async () =>
			{
				var (published, failed) = await Lumora.Core.Assets.BuiltinAssetRegistry.PublishAsync(client, publishKey!, root,
					line => LumoraLogger.Log($"publish: {line}"));
				LumoraLogger.Log($"LumoraEngineRunner: built-ins published: {published} ok, {failed} failed");
			});
		}
	}

	// A build with no display server is a service: it has no window, no speakers and usually no sound
	// card at all. The flag is checked as well because the driver name is only meaningful once the
	// display server is up. -xlinka
	private static bool IsHeadless()
	{
		if (LaunchArgs.HasFlag("--headless"))
			return true;
		try
		{
			return DisplayServer.GetName() == "headless";
		}
		catch
		{
			return true;
		}
	}

	private static bool HasCommandLineFlag(string flag) => LaunchArgs.HasFlag(flag);

	private static IEnumerable<string> GetAllCommandLineArgs() => LaunchArgs.All();

	private static XrLaunchMode ParseXrModeValue(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return XrLaunchMode.Desktop;
		}

		return value.Trim().ToLowerInvariant() switch
		{
			"off" => XrLaunchMode.Desktop,
			"desktop" => XrLaunchMode.Desktop,
			"2d" => XrLaunchMode.Desktop,
			"auto" => XrLaunchMode.Auto,
			"on" => XrLaunchMode.Vr,
			"vr" => XrLaunchMode.Vr,
			"openxr" => XrLaunchMode.Vr,
			_ => XrLaunchMode.Desktop
		};
	}

	private static XrLaunchMode ResolveXrLaunchMode()
	{
		if (AndroidDeviceMode.IsPhone)
			return XrLaunchMode.Desktop;
		// On a standalone Android build (Quest, Pico, Vive Focus) there's no
		// such thing as desktop mode - the device IS the screen. Force VR and
		// ignore any conflicting --desktop / --xr-mode flag the user might
		// have left in their preset.
		// - xlinka
		if (OS.HasFeature("android"))
			return XrLaunchMode.Vr;

		// Default to Auto so a headset is detected automatically unless the user
		// explicitly passes --desktop / --xr-mode=off on the command line.
		var mode = XrLaunchMode.Auto;
		var args = GetAllCommandLineArgs().ToArray();

		for (int i = 0; i < args.Length; i++)
		{
			var arg = args[i]?.Trim();
			if (string.IsNullOrWhiteSpace(arg))
			{
				continue;
			}

			var normalized = arg.ToLowerInvariant();
			if (normalized is "-desktop" or "--desktop")
			{
				mode = XrLaunchMode.Desktop;
				continue;
			}

			if (normalized is "-vr" or "--vr")
			{
				mode = XrLaunchMode.Vr;
				continue;
			}

			if (normalized.StartsWith("--xr-mode=", StringComparison.Ordinal))
			{
				mode = ParseXrModeValue(normalized.Substring("--xr-mode=".Length));
				continue;
			}

			if (normalized == "--xr-mode" && i + 1 < args.Length)
			{
				mode = ParseXrModeValue(args[++i]);
			}
		}

		return mode;
	}

	private void LaunchDebugConsoleProcess()
	{
		try
		{
			if (IsDebugConsoleRunning())
			{
				LumoraLogger.Log("DebugConsole: already running, not launching a duplicate process");
				return;
			}

			var executablePath = OS.GetExecutablePath();
			if (string.IsNullOrWhiteSpace(executablePath))
			{
				LumoraLogger.Warn("DebugConsole: could not resolve executable path");
				return;
			}

			var args = new List<string>();
			if (OS.HasFeature("editor"))
			{
				args.Add("--path");
				args.Add(ProjectSettings.GlobalizePath("res://"));
			}

			args.Add("--");
			args.Add(DebugConsoleFlag);
			args.Add("-desktop");
			args.Add("--xr-mode");
			args.Add("off");

			var processId = OS.CreateProcess(executablePath, args.ToArray(), false);
			if (processId > 0)
			{
				LumoraLogger.Log($"DebugConsole: launched separate process (pid={processId})");
			}
			else
			{
				LumoraLogger.Warn($"DebugConsole: process launch failed (pid={processId})");
			}
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"DebugConsole: process launch exception: {ex.Message}");
		}
	}

	private bool TryAcquireDebugConsoleLock()
	{
		try
		{
			_debugConsoleInstanceMutex = new ThreadingMutex(true, DebugConsoleMutexName, out var createdNew);
			_ownsDebugConsoleLock = createdNew;

			if (!createdNew)
			{
				_debugConsoleInstanceMutex.Dispose();
				_debugConsoleInstanceMutex = null;
			}

			return createdNew;
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"DebugConsole: failed to create single-instance lock: {ex.Message}");
			return true;
		}
	}

	private static bool IsDebugConsoleRunning()
	{
		try
		{
			if (ThreadingMutex.TryOpenExisting(DebugConsoleMutexName, out var existingMutex))
			{
				existingMutex.Dispose();
				return true;
			}
		}
		catch
		{
			// Best effort only; if this check fails, allow launch.
		}

		return false;
	}

	private void InitializeLoadingScreen()
	{
		if (VerboseInit)
		{
			LumoraLogger.Debug("InitializeLoadingScreen: Loading loading screen scene...");
		}

		var loadingScreenScene = GD.Load<PackedScene>(LumAssets.UI.LoadingScreen);
		if (loadingScreenScene != null)
		{
			if (VerboseInit)
			{
				LumoraLogger.Debug("InitializeLoadingScreen: Scene loaded, instantiating...");
			}

			_loadingScreen = loadingScreenScene.Instantiate<LoadingScreen>();
			AddChild(_loadingScreen);
			LumoraLogger.Debug("LoadingScreen: Initialized and displayed");
		}
		else
		{
			LumoraLogger.Warn("LoadingScreen: Failed to load scene - continuing without loading UI");
		}
	}

	private async void StartInitialization()
	{
		if (VerboseInit)
		{
			LumoraLogger.Debug("StartInitialization: Beginning async initialization...");
		}

		try
		{
			_currentPhase = InitializationPhase.EnvironmentSetup;
			await PhaseEnvironmentSetup();

			_currentPhase = InitializationPhase.XRDetection;
			await PhaseXRDetection();

			_currentPhase = InitializationPhase.HeadOutputCreation;
			await PhaseHeadOutputCreation();

			_currentPhase = InitializationPhase.EngineCoreInit;
			await PhaseEngineCoreInit();

			_currentPhase = InitializationPhase.SystemIntegration;
			await PhaseSystemIntegration();

			_currentPhase = InitializationPhase.UserspaceSetup;
			await PhaseUserspaceSetup();

			_currentPhase = InitializationPhase.Ready;
			OnEngineReady();
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"LumoraEngineRunner: Initialization failed at phase {_currentPhase}: {ex.Message}");
			LumoraLogger.Error($"Stack trace: {ex.StackTrace}");
			_loadingScreen?.ShowError("Startup could not finish. Please restart Lumora.");
			DisplayServer.ScreenSetKeepOn(false);
		}
	}

	private async Task PhaseEnvironmentSetup()
	{
		LumoraLogger.Log("[Phase 1/6] Environment Setup");
		_loadingScreen?.UpdatePhase(0);

		// Leave vsync off but allow high framerates; mouse driver normalizes deltas to 60 Hz feel
		global::Godot.Engine.MaxFps = 0;
		DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

		// Prevent screen sleep
		DisplayServer.ScreenSetKeepOn(true);

		var args = GetAllCommandLineArgs().ToArray();
		LumoraLogger.Log($"Command-line args: {string.Join(" ", args)}");
		LogLaunchDiagnostics();
		_xrLaunchMode = ResolveXrLaunchMode();
		LumoraLogger.Log($"XR launch mode: {_xrLaunchMode}");

		await Task.Delay(150); // Artificial delay to show phase message
	}

	// THE POOL HAS TO BE BIG ENOUGH FOR AN IMPORT BEFORE THE IMPORT STARTS.
	//
	// .NET starts with as many worker threads as there are cores and then grows by roughly ONE PER
	// SECOND under load. Importing an avatar asks for well over a hundred assets in the same instant,
	// each wanting a thread for its load state machine and another for its decode, so the work sat in
	// a queue and drained at the injection rate rather than at the speed of the machine. Raising the
	// floor costs nothing when idle - these are threads the pool is allowed to create, not threads it
	// creates now - and removes a stall that reads as "the textures never loaded".
	//
	// Measured before: 67 texture loads started, 34 reads finished, 13 reached the GPU in 44 seconds.
	// -xlinka
	private static void WidenThreadPoolForAssetBursts()
	{
		try
		{
			System.Threading.ThreadPool.GetMinThreads(out int workers, out int completion);
			int wantWorkers = System.Math.Max(workers, System.Math.Max(32, System.Environment.ProcessorCount * 2));
			int wantCompletion = System.Math.Max(completion, 32);
			if (wantWorkers == workers && wantCompletion == completion)
				return;
			if (System.Threading.ThreadPool.SetMinThreads(wantWorkers, wantCompletion))
				LumoraLogger.Log($"Thread pool: minimum raised to {wantWorkers} workers / {wantCompletion} IO (was {workers}/{completion}).");
			else
				LumoraLogger.Warn($"Thread pool: refused a minimum of {wantWorkers}/{wantCompletion}; asset bursts will drain slowly.");
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"Thread pool: could not raise the minimum ({ex.Message}).");
		}
	}

	private void LogLaunchDiagnostics()
	{
		WidenThreadPoolForAssetBursts();
		LumoraLogger.Log($"Runtime: .NET {System.Environment.Version}, OS={RuntimeInformation.OSDescription}, Arch={RuntimeInformation.ProcessArchitecture}, CPUs={System.Environment.ProcessorCount}");
		LumoraLogger.Log($"Godot platform: {OS.GetName()}, model={SafeDiagnostic(OS.GetModelName)}");
		// Every line above says Windows under Proton, and a bug report that says Windows when the
		// machine is a Steam Deck wastes everybody's afternoon.
		LumoraLogger.Log($"Host: {Lumora.Core.WinePlatform.Describe()}");

		// WHEN THE CODE THAT IS ACTUALLY RUNNING WAS BUILT.
		//
		// Twice now a fix has been declared not to work off a log from a process that started before
		// the build finished, and the only way to tell was to notice that a string added an hour ago
		// was missing. The assemblies can say so themselves in one line. -xlinka
		LogAssemblyStamp("LumoraCore", typeof(Lumora.Core.Engine).Assembly);
		LogAssemblyStamp("LumoraHooks", typeof(Lumora.Godot.Hooks.SlotHook).Assembly);
		Lumora.Core.WinePlatform.LogOnce();
		LumoraLogger.Log($"Display: window={DisplayServer.WindowGetSize()}, screen={DisplayServer.ScreenGetSize()}");
		LumoraLogger.Log($"Renderer: {SafeDiagnostic(RenderingServer.GetVideoAdapterName)}");
	}

	// The PE header's timestamp is what the compiler stamped in, which is the build, not a file copy.
	// Deterministic builds put a content hash there instead; that is still a fingerprint that changes
	// whenever the code does, so it answers the same question either way.
	private static void LogAssemblyStamp(string name, Assembly assembly)
	{
		try
		{
			// Assembly.Location comes back EMPTY for anything loaded from a bundle or a byte array,
			// which is how Godot loads these, so fall back to finding the file beside the app.
			var path = assembly.Location;
			if (string.IsNullOrEmpty(path))
			{
				var guess = System.IO.Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll");
				if (System.IO.File.Exists(guess))
					path = guess;
			}
			string stamp = string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)
				? "unknown"
				: System.IO.File.GetLastWriteTime(path).ToString("HH:mm:ss");
			LumoraLogger.Log($"Build: {name} written {stamp}");
		}
		catch (Exception ex)
		{
			LumoraLogger.Log($"Build: {name} stamp unavailable ({ex.Message})");
		}
	}

	private static string SafeDiagnostic(Func<string> read)
	{
		try
		{
			var value = read();
			return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
		}
		catch
		{
			return "unknown";
		}
	}

	// Architecture: the root viewport is always the normal desktop window. The XR SubViewport
	// owns the headset render path and XRModeManager keeps that viewport UseXR=true while the
	// OpenXR session is alive. F8 swaps input and camera ownership; it does not tear down OpenXR.
	// - xlinka
	private async Task PhaseXRDetection()
	{
		LumoraLogger.Log("[Phase 2/6] XR Detection");
		_loadingScreen?.UpdatePhase(1);

		if (_xrLaunchMode == XrLaunchMode.Desktop)
		{
			LumoraLogger.Log("XR Device: Disabled (desktop launch mode)");
			GetViewport().UseXR = false;
			await Task.Delay(120);
			return;
		}

		var xrInterface = XRServer.FindInterface("OpenXR");
		if (xrInterface == null)
		{
			if (OS.HasFeature("android"))
			{
				LumoraLogger.Error("XR: OpenXR interface not found on standalone Android. " +
					"The OpenXR Vendors plugin / loader for this headset is probably missing from the APK. Quitting.");
				GetTree().Quit();
				return;
			}

			if (_xrLaunchMode == XrLaunchMode.Vr)
				LumoraLogger.Warn("XR: VR mode requested, but OpenXR interface was not found. Running in screen mode.");
			else
				LumoraLogger.Log("XR Device: None (Screen Mode)");

			GetViewport().UseXR = false;
			await Task.Delay(120);
			return;
		}

		var openXRInterface = xrInterface as OpenXRInterface;
		ViewportQuality.ConfigureOpenXRBeforeInitialize(openXRInterface, LumoraLogger.Log);

		if (!xrInterface.IsInitialized())
		{
			LumoraLogger.Log("XR: OpenXR interface found but not initialized. Attempting Initialize()...");
			if (!xrInterface.Initialize())
			{
				if (OS.HasFeature("android"))
				{
					LumoraLogger.Error("XR: OpenXR Initialize() failed on standalone Android. " +
						"Check the device's OpenXR runtime is installed and the APK has the correct loader. Quitting.");
					GetTree().Quit();
					return;
				}

				// Without an explicit VR request a failed init just means no headset is up, which is every
				// desktop launch; only a requested VR session that cannot start is worth a warning.
				//
				// Either way it says WHICH runtime was asked. A PC with more than one runtime installed
				// has exactly one active, and the game only ever talks to that one: streaming over Steam
				// Link while Virtual Desktop's runtime is active gets a truthful "no headset" from a
				// runtime that was never going to have one, and until now the log said only "no headset
				// session" and left the person to guess. -xlinka
				var diagnosis = OpenXRRuntimeReport.DiagnoseNoHeadset();
				if (_xrLaunchMode == XrLaunchMode.Vr)
					LumoraLogger.Warn($"XR: OpenXR Initialize() failed with VR mode requested - falling back to screen mode. {diagnosis}");
				else
					LumoraLogger.Log($"XR: OpenXR runtime present but no headset session - screen mode. {diagnosis}");
				GetViewport().UseXR = false;
				await Task.Delay(120);
				return;
			}
		}

		if (XRServer.PrimaryInterface == null)
			XRServer.PrimaryInterface = xrInterface;

		_openXRInterface = openXRInterface!;
		GetViewport().UseXR = false;
		ConfigureOpenXRViewport(EnsureXRViewport());
		ConnectOpenXREvents(_openXRInterface);

		_vrInitializedAtBoot = true;

		LumoraLogger.Log(UseRootViewportForXr
			? "XR Device: OpenXR (Active) - root viewport renders the HMD"
			: "XR Device: OpenXR (Active) - dedicated XR viewport renders HMD, root viewport stays desktop");
		TrimStandaloneRendering();
		AttachVrLoadingEnvironment();
		await Task.Delay(120);
	}

	// On a standalone headset, render the scene ONCE and skip the post passes.
	//
	// Measured on a Pico 4 at 1 fps: the XR viewport alone took 270 ms of GPU at 1504x1504 with glow
	// on, and the frame was 800 ms. The rest was the ROOT viewport. On desktop it is the monitor
	// mirror and earns its keep; on a standalone headset the device's flat display is never visible
	// while the headset is worn, yet the desktop camera was still drawing the whole world into it every
	// frame at full resolution. Disable3D leaves it as a 2D surface, which is what the runtime's own
	// mirror wants anyway. Glow is a full-resolution multi-pass bloom chain on the mobile renderer and
	// is decoration; off. -xlinka
	private void TrimStandaloneRendering()
	{
		if (!HeadsetDeviceMode.IsHeadset)
			return;

		var root = GetViewport();
		bool rootIsEyeRender = _xrViewport == root;
		if (!rootIsEyeRender)
		{
			root.Disable3D = true;
			root.Msaa3D = Viewport.Msaa.Disabled;
			root.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
		}

		int glowOff = 0;
		foreach (var env in new[] { root.World3D?.Environment, _xrViewport?.World3D?.Environment, _xrViewport?.GetCamera3D()?.Environment })
		{
			if (env == null || !env.GlowEnabled)
				continue;
			env.GlowEnabled = false;
			glowOff++;
		}

		LumoraLogger.Log(rootIsEyeRender
			? $"Standalone: root viewport IS the eye render; glow disabled on {glowOff} environment(s)"
			: $"Standalone: root viewport 3D disabled (headset viewport is the only 3D render); glow disabled on {glowOff} environment(s)");
	}

	// The headset shows whatever the XR viewport renders from the moment UseXR flips on, and until now
	// that was the bare bootstrap world: near-black for four more phases, indistinguishable from a hung
	// process. The 2D LoadingScreen is a Control and OpenXR composites only the 3D viewport, so the
	// loading screen grows a 3D twin here, driven by the same UpdatePhase calls. Camera comes from the
	// XR viewport's current camera (what the headset actually renders from), with the scene's
	// %XRCamera3D as the fallback. -xlinka
	private void AttachVrLoadingEnvironment()
	{
		if (_loadingScreen == null || !GodotObject.IsInstanceValid(_loadingScreen))
			return;

		if (_xrViewport == null || !GodotObject.IsInstanceValid(_xrViewport))
		{
			LumoraLogger.Warn("LoadingScreen: VR environment skipped - XR viewport missing after OpenXR init.");
			return;
		}

		var xrCamera = _xrViewport.GetCamera3D()
			?? GetTree()?.CurrentScene?.GetNodeOrNull<Camera3D>("%XRCamera3D");
		if (xrCamera == null)
		{
			LumoraLogger.Warn("LoadingScreen: VR environment skipped - XR viewport has no current camera and %XRCamera3D was not found.");
			return;
		}

		_loadingScreen.AttachVrEnvironment(xrCamera, _xrViewport);
	}

	private Viewport EnsureXRViewport()
	{
		if (_xrViewport != null && GodotObject.IsInstanceValid(_xrViewport))
			return _xrViewport;
		_xrViewport = XRViewportLayout.Ensure(GetTree()?.CurrentScene ?? this, GetViewport());
		return _xrViewport;
	}
	private void ConfigureOpenXRViewport(Viewport viewport)
	{
		if (viewport == null)
			return;

		DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
		ViewportQuality.ConfigureOpenXRAfterInitialize(_openXRInterface, viewport, LumoraLogger.Log);
		XRViewportLayout.ApplySessionState(GetViewport(), viewport, true, true);
	}

	private void ConnectOpenXREvents(OpenXRInterface xrInterface)
	{
		if (xrInterface == null || _openXRSignalsConnected)
			return;

		xrInterface.SessionBegun += OnOpenXRSessionBegun;
		xrInterface.SessionVisible += OnOpenXRVisibleState;
		xrInterface.SessionFocussed += OnOpenXRFocusedState;
		xrInterface.SessionStopping += OnOpenXRStopping;
		xrInterface.PoseRecentered += OnOpenXRPoseRecentered;
		_openXRSignalsConnected = true;
	}

	private void DisconnectOpenXREvents()
	{
		if (_openXRInterface == null || !_openXRSignalsConnected)
			return;

		_openXRInterface.SessionBegun -= OnOpenXRSessionBegun;
		_openXRInterface.SessionVisible -= OnOpenXRVisibleState;
		_openXRInterface.SessionFocussed -= OnOpenXRFocusedState;
		_openXRInterface.SessionStopping -= OnOpenXRStopping;
		_openXRInterface.PoseRecentered -= OnOpenXRPoseRecentered;
		_openXRSignalsConnected = false;
	}

	private void OnOpenXRSessionBegun()
	{
		if (_openXRInterface == null)
			return;

		var currentRefreshRate = _openXRInterface.DisplayRefreshRate;
		LumoraLogger.Log(currentRefreshRate > 0.0f
			? $"OpenXR: Refresh rate reported as {currentRefreshRate}"
			: "OpenXR: No refresh rate given by XR runtime");

		var newRate = currentRefreshRate;
		var availableRates = _openXRInterface.GetAvailableDisplayRefreshRates();
		if (availableRates.Count == 0)
		{
			LumoraLogger.Log("OpenXR: Target does not support refresh rate extension");
		}
		else if (availableRates.Count == 1)
		{
			newRate = (float)availableRates[0];
		}
		else
		{
			LumoraLogger.Log($"OpenXR: Available refresh rates: {availableRates}");
			// A standalone headset stops at 72. The Pico 4 offers 90, an 11.1 ms budget the 1.2x
			// supersampled frame does not fit, and missing it means the compositor halves us to 45.
			// 72 Hz is 13.9 ms, which fits with room; a steady 72 beats a 90 that keeps falling over. -xlinka
			float ceiling = HeadsetDeviceMode.IsHeadset ? StandaloneMaxRefreshRate : MaximumXrRefreshRate;
			newRate = 0f;
			foreach (float rate in availableRates)
			{
				if (rate > newRate && rate <= ceiling)
					newRate = rate;
			}
			if (newRate <= 0f)
				newRate = currentRefreshRate;
		}

		if (newRate > 0.0f && Math.Abs(currentRefreshRate - newRate) > 0.01f)
		{
			LumoraLogger.Log($"OpenXR: Setting refresh rate to {newRate}");
			_openXRInterface.DisplayRefreshRate = newRate;
			currentRefreshRate = newRate;
		}

		if (currentRefreshRate > 0.0f)
		{
			var physicsRate = Math.Max(60, (int)Math.Round(currentRefreshRate));
			global::Godot.Engine.PhysicsTicksPerSecond = physicsRate;
			LumoraLogger.Log($"OpenXR: Physics tick rate set to {physicsRate}");
		}

		Lumora.Core.Engine.SetDisplayRefreshRate(CurrentDisplayRefreshRate());
	}

	// The rate frames are actually presented at: the headset's while it drives the frame, the window's
	// monitor otherwise, 60 when neither will say.
	private double CurrentDisplayRefreshRate()
	{
		bool xr = _xrViewport != null && GodotObject.IsInstanceValid(_xrViewport) && _xrViewport.UseXR;
		if (xr && _openXRInterface != null && GodotObject.IsInstanceValid(_openXRInterface))
		{
			float xrRate = _openXRInterface.DisplayRefreshRate;
			if (xrRate > 1f)
				return xrRate;
		}
		float screenRate = DisplayServer.ScreenGetRefreshRate(DisplayServer.WindowGetCurrentScreen());
		return screenRate > 1f ? screenRate : 60.0;
	}

	private void OnOpenXRVisibleState()
	{
		if (!_xrIsFocused)
			return;

		_xrIsFocused = false;
		_engine?.InputInterface?.ReportHeadsetFocus(false);
		LumoraLogger.Log("OpenXR: visible but not focused");
	}

	private void OnOpenXRFocusedState()
	{
		_xrIsFocused = true;
		_engine?.InputInterface?.ReportHeadsetFocus(true);
		LumoraLogger.Log("OpenXR: focused");
	}

	private void OnOpenXRStopping()
	{
		_engine?.InputInterface?.ReportHeadsetFocus(false);
		LumoraLogger.Log("OpenXR: stopping");
	}

	private void OnOpenXRPoseRecentered()
	{
		LumoraLogger.Log("OpenXR: pose recentered");
	}

	private async Task PhaseHeadOutputCreation()
	{
		LumoraLogger.Log("[Phase 3/6] HeadOutput Creation");
		_loadingScreen?.UpdatePhase(2);

		// DesktopCamera lives under the root viewport and drives the desktop
		// window. XRCamera3D is moved under XRViewport during PhaseXRDetection
		// and drives the headset.
		_mainCamera = GetNodeOrNull<Camera3D>("%DesktopCamera");
		if (_mainCamera == null)
		{
			LumoraLogger.Error("HeadOutput: %DesktopCamera not found in Bootstrap.tscn - falling back to a runtime-created camera under the root viewport.");
			_mainCamera = new Camera3D { Name = "MainCameraFallback" };
			GetViewport().AddChild(_mainCamera);
		}

		// The main view must not draw the hidden capture layer (the dash renders
		// its UI there for an offscreen render-texture grab) or the overlay
		// layer. Render-texture cameras opt those layers back in. - xlinka
		// Also cull the free-cam indicator: third-person/free-cam now drive THIS camera, so its marker
		// would otherwise sit right on the lens. It stays on its own layer for spectator/other views.
		// The local head rides the same idea: LocalViewOverride moves the local avatar's head renderers
		// onto LOCAL_HEAD_LAYER so these two cameras skip them, which is what lets the near plane sit at
		// millimetres instead of being used as a head-remover. EYES_ONLY_LAYER is the mirror image and
		// deliberately NOT in this mask: it carries the headless twin of a skinned body that only these
		// two cameras are meant to draw; every other camera masks it off. -xlinka
		uint hiddenAndOverlay = (uint)(Lumora.Godot.Helpers.RenderHelper.HIDDEN_LAYER
			| Lumora.Godot.Helpers.RenderHelper.OVERLAY_LAYER
			| Lumora.Godot.Helpers.RenderHelper.FREECAM_INDICATOR_LAYER
			| Lumora.Godot.Helpers.RenderHelper.LOCAL_HEAD_LAYER
			| Lumora.Godot.Helpers.RenderHelper.OWN_NAMEPLATE_LAYER);
		_mainCamera.CullMask &= ~hiddenAndOverlay;
		var xrCamera = GetNodeOrNull<Camera3D>("%XRCamera3D");
		if (xrCamera != null)
			xrCamera.CullMask &= ~hiddenAndOverlay;

		_headOutput = new HeadOutput();
		AddChild(_headOutput);
		_headOutput.Initialize(_mainCamera);

		LumoraLogger.Log($"HeadOutput initialized with camera: {_mainCamera.Name}");

		// Desktop draws the dash as a screen composite over this camera's output instead of on its
		// world panel, so it cannot lag the view. Built once here and ticked from _Process; it
		// decides on its own frame by frame whether to show, and hides itself outright in VR.
		_dashOverlay = new Lumora.Source.Godot.UI.DashScreenOverlay { Name = "DashScreenOverlay" };
		AddChild(_dashOverlay);


		await Task.Delay(180); // Artificial delay to show phase message
	}

	private async Task PhaseEngineCoreInit()
	{
		LumoraLogger.Log("[Phase 4/6] Engine Core Initialization");
		_loadingScreen?.UpdatePhase(3);

		try
		{
			if (VerboseInit)
			{
				LumoraLogger.Debug("PhaseEngineCoreInit: Creating SystemInfoHook...");
			}

			_systemInfoHook = new SystemInfoHook();
			AddChild(_systemInfoHook);
			if (VerboseInit)
			{
				LumoraLogger.Debug("PhaseEngineCoreInit: SystemInfoHook created");
			}

			if (VerboseInit)
			{
				LumoraLogger.Debug("PhaseEngineCoreInit: Creating Engine instance...");
			}

			// Seal the local-encryption master key with the OS keystore (DPAPI on Windows; derived
			// fallback on Linux/Android until their native keystores are wired). Must be set before
			// anything touches the vault (engine init loads the local home / asset DB).
			Lumora.Core.Persistence.LocalEncryption.Sealer = new PlatformSecretSealer();

			_engine = new Lumora.Core.Engine
			{
				AutoHostLocalHome = this.AutoHostLocalHome,
				AutoConnectLocalHome = this.AutoConnectLocalHome
			};
			// In an EXPORT res:// has no real on-disk directory, so GlobalizePath("res://") returns empty - which
			// left every res:// asset (notably fonts -> no text) unresolvable. Fall back to the executable's folder
			// as a stable, non-empty root; the actual bytes still load from the .pck via the VFS (ResourceLoader)
			// in the asset hooks, so the root only needs to be a consistent prefix the hook can strip. -xlinka
			var resourceRoot = ProjectSettings.GlobalizePath("res://");
			if (string.IsNullOrWhiteSpace(resourceRoot))
			{
				resourceRoot = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? string.Empty;
			}
			// On Android the executable path is the literal "apk", whose directory is empty, so BOTH
			// fallbacks came back blank and every res:// asset failed with "Resource root not set":
			// no fonts, and Helio text without a font draws nothing. The root only has to be a stable
			// prefix the hooks strip back off, so a synthetic folder under user data does the job. -xlinka
			if (string.IsNullOrWhiteSpace(resourceRoot))
			{
				resourceRoot = System.IO.Path.Combine(OS.GetUserDataDir(), "res");
				LumoraLogger.Log($"LumoraEngineRunner: no on-disk resource root on this platform; using '{resourceRoot}' as the res:// prefix");
			}
			_engine.ResourceRoot = resourceRoot;
			LoadBundledLocales();
			_engine.QuitRequested += OnQuitRequested;
			if (VerboseInit)
			{
				LumoraLogger.Debug("PhaseEngineCoreInit: Engine instance created");
			}

			// hooks must be registered before engine init so slots get hooks when they're created
			if (VerboseInit)
			{
				LumoraLogger.Debug("PhaseEngineCoreInit: Registering hooks...");
			}

			RegisterHooks();
			if (VerboseInit)
			{
				LumoraLogger.Debug("PhaseEngineCoreInit: Hooks registered");
			}

			// Register network transports BEFORE InitializeAsync: it auto-hosts the local home, which
			// opens a listener - if no manager is registered yet that fails ("no network manager
			// registered"). Steam is already initialized by now (early in _Ready). - xlinka
			RegisterNetworkManagers();

			LumoraLogger.Debug("LumoraEngineRunner: Calling Engine.InitializeAsync()...");
			await _engine.InitializeAsync();

			LumoraLogger.Log("LumoraEngineRunner: Engine initialized successfully");
			StartServiceModes();

			if (VerboseInit)
			{
				LumoraLogger.Debug("PhaseEngineCoreInit: Initializing WorldManager hook...");
			}

			var worldManagerHook = WorldManagerHook.Constructor();
			// Hook set BEFORE Initialize() so existing worlds can find it.
			_engine.WorldManager.Hook = worldManagerHook;
			worldManagerHook.Initialize(_engine.WorldManager, GetTree().Root);
			LumoraLogger.Debug("LumoraEngineRunner: WorldManager hook initialized");

			await Task.Delay(200); // Artificial delay to show phase message (longer for core init)
		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"LumoraEngineRunner: Engine initialization failed: {ex.Message}");
			LumoraLogger.Error($"Stack trace: {ex.StackTrace}");
			throw; // Re-throw to be caught by StartInitialization
		}
	}

	private async Task PhaseSystemIntegration()
	{
		LumoraLogger.Log("[Phase 5/6] System Integration");
		_loadingScreen?.UpdatePhase(4);

		// Get InputInterface from Engine (it's already initialized in Engine.InitializeAsync)
		_inputInterface = _engine.InputInterface;

		// A service page is not a stream URL, and only the platform layer can run the process that turns
		// one into the other, so the core gets handed a resolver instead of owning one. -xlinka
		Lumora.Core.Media.MediaServices.VideoServiceResolver = new Lumora.Source.Godot.Services.VideoServiceResolver();

		// The mixer renders on its own thread and needs somewhere to push to. Without a display there
		// is no device worth opening, so the simulator runs dry: time still advances, nothing is heard.
		// -xlinka
		var audioOutput = IsHeadless() ? null : new GodotAudioDeviceOutput();
		_engine.AudioSystem.Initialize(audioOutput);
#if GODOT_PC
		Lumora.Godot.Hooks.Video.LibVlcRuntime.WarmUp();
#endif

		// The microphone. Registered, not opened: the capture input only starts the device once
		// something actually subscribes to it, which is the local user's voice stream deciding it is
		// not muted. A service build has nothing to capture from and nobody to send it to.
		if (!IsHeadless())
		{
			var audioInput = new GodotAudioDeviceInput();
			_engine.AudioSystem.RegisterAudioInput(new Lumora.Core.Audio.AudioInput(audioInput));

			// Every endpoint beside it, for streaming: outputs read back, the whole machine minus this
			// program, every microphone. Godot captures one input and knows no loopbacks, so without
			// these the stream picker could only ever offer the mic the voice is already on. Registered,
			// not opened, same as the one above. -xlinka
			if (OperatingSystem.IsWindows())
			{
				Lumora.Audio.Wasapi.WasapiEndpoints.Warn = message => Lumora.Core.Logging.Logger.Warn(message);
				foreach (var endpoint in Lumora.Audio.Wasapi.WasapiEndpoints.Enumerate())
					_engine.AudioSystem.RegisterAudioInput(new Lumora.Core.Audio.AudioInput(endpoint));
			}

			// Enumerates on construction, so the settings screen has a device list from the first time
			// it is opened. Applying here is what makes a saved choice survive a relaunch, and it needs
			// the settings read first: the applier node that normally does that is not built until the
			// userspace phase, two phases after the mixer opens a device. Load is idempotent. -xlinka
			_engine.AudioSystem.DevicePlatform = new GodotAudioDevicePlatform(audioInput, audioOutput);
			Lumora.Core.EngineSettings.Load();
			_engine.AudioSystem.ApplyDeviceSelection();
		}

		// Register Godot-specific builtin asset loader (reads from res:// using FileAccess)
		Lumora.Nexus.Assets.BuiltinAssetHelper.PlatformLoader = (relativePath) =>
		{
			string resPath = $"res://{relativePath}";
			if (!FileAccess.FileExists(resPath))
			{
				LumoraLogger.Warn($"BuiltinAssetHelper: res:// path not found: '{resPath}'");
				return null!;
			}
			return FileAccess.GetFileAsBytes(resPath);
		};

		// Register low-level input drivers (keyboard, mouse, VR tracking layer)
		RegisterInputDrivers();

		// Create the XR Mode Manager - owns Desktop/VR provider nodes and handles F8 hot-swap.
		// `_vrInitializedAtBoot` is the source of truth for whether PhaseXRDetection
		// successfully brought up the OpenXR session. XRModeManager owns the
		// active mode's viewport UseXR flag from here.
		_xrModeManager = new XRModeManager();
		_xrModeManager.Name = "XRModeManager";
		AddChild(_xrModeManager);
		_xrModeManager.Initialize(
			_engine, _headOutput, _inputInterface, _mainCamera, _vrDriver,
			startingInVR: _vrInitializedAtBoot,
			allowAutoVr: _xrLaunchMode != XrLaunchMode.Desktop);
		if (AndroidDeviceMode.IsPhone)
		{
			_xrModeManager.EnableTouchControls(_mouseDriver);
			OS.RequestPermission("android.permission.RECORD_AUDIO");
		}

		// Mirrors and portals render from the eye the person is actually using, which only the mode
		// manager knows across a desktop/VR switch.
		Lumora.Core.Photos.PhotoEncoder.Platform = Lumora.Godot.Hooks.RenderTextureHook.Encode;
		Lumora.Core.Photos.PhotoLibrary.PicturesDirectory = OS.GetSystemDir(OS.SystemDir.Pictures);
		Lumora.Godot.Helpers.RenderHelper.ViewerCamera = () =>
			_xrModeManager != null && GodotObject.IsInstanceValid(_xrModeManager)
				? _xrModeManager.CurrentCamera
				: _mainCamera;

		await Task.Delay(150); // Artificial delay to show phase message
	}

	// The desktop scene overlay (DesktopInput) is created by XRModeManager when desktop mode is
	// active; VR mode has no extra scene node.
	private void RegisterInputDrivers()
	{
		_keyboardDriver = new GodotKeyboardDriver();
		_inputInterface.RegisterKeyboardDriver(_keyboardDriver);

		_mouseDriver = new GodotMouseDriver();
		_inputInterface.RegisterMouseDriver(_mouseDriver);

		// registered as a plain input driver too so it gets the hot-plug hookup
		_gamepadDriver = new GodotGamepadDriver();
		_inputInterface.RegisterGamepadDriver(_gamepadDriver);
		_inputInterface.RegisterInputDriver(_gamepadDriver);

		_vrDriver = new GodotVRDriver();
		_vrDriver.InitializeVR();
		_vrDriver.FindXRNodes(GetTree().Root);
		_inputInterface.RegisterVRDriver(_vrDriver);
		_vrDriver.LogRuntimeDiagnostics();

		// Vendor eye and lip tracking, when the machine has the runtime for it. Registered after the
		// VR driver so its UpdateOrder puts it after the OpenXR gaze floor, which it replaces on the
		// hardware that can do better. Costs nothing on a machine without it: the gate is a file
		// check and the whole driver stays unregistered. -xlinka
		if (SRanipalDriver.ShouldRegister())
		{
			_sranipalDriver = new SRanipalDriver();
			_inputInterface.RegisterInputDriver(_sranipalDriver);
		}
		LumoraLogger.Log($"Input drivers: keyboard={_keyboardDriver.GetType().Name}, mouse={_mouseDriver.GetType().Name}, gamepad={_gamepadDriver.ConnectedPadCount} pad(s), vr={_vrDriver.VRSystemName}, active={_vrDriver.IsVRActive}");

		_localDB = new LocalDB();
		_ = _localDB.InitializeAsync();

		// wires LocalDB into Engine for local:// URI resolution
		if (_engine != null)
		{
			_engine.LocalDB = _localDB;
		}

		// Clipboard TEXT, for the text fields. Separate service from the ClipboardImporter below, which
		// handles the FILE/IMAGE side of the same clipboard and feeds the asset pipeline.
		_inputInterface.ClipboardText = new GodotClipboardText();

		// What the view is showing at a world point, for the color picker's eyedropper.
		_inputInterface.ViewColorSampler = new GodotViewColorSampler(() => GetViewport()?.GetCamera3D() ?? _mainCamera);

		// A JPEG of the same frame, for the session thumbnail a host publishes and the picture written
		// beside a saved world.
		_inputInterface.ViewCapture = new GodotViewCapture(GetViewport);

		_clipboardImporter = new ClipboardImporter();
		_clipboardImporter.Name = "ClipboardImporter";
		AddChild(_clipboardImporter);
		_clipboardImporter.Initialize(_localDB, null!, _mainCamera);
		_clipboardImporter.OnAssetImported += OnClipboardAssetImported;
		LumoraLogger.Debug("ClipboardImporter: Created for paste handling");
	}

	// The OS clipboard's text, straight off DisplayServer. Wrapped in try/catch because the headless
	// display server has no clipboard at all and throws rather than returning empty - a text field
	// asking for a paste is not a reason to take the frame down with it. -xlinka
	private sealed class GodotClipboardText : IClipboardText
	{
		public string GetText()
		{
			try
			{
				return DisplayServer.ClipboardGet() ?? string.Empty;
			}
			catch (Exception ex)
			{
				LumoraLogger.Warn($"Clipboard: read failed: {ex.Message}");
				return string.Empty;
			}
		}

		public void SetText(string text)
		{
			try
			{
				DisplayServer.ClipboardSet(text ?? string.Empty);
			}
			catch (Exception ex)
			{
				LumoraLogger.Warn($"Clipboard: write failed: {ex.Message}");
			}
		}
	}

	// The colour the local view is actually showing at a world point: project the point through the
	// camera that is currently rendering, then read that pixel out of the viewport image. GetImage pulls
	// the frame back off the GPU, which is a full pipeline stall - one per eyedropper click and never
	// per frame, which is exactly how the picker uses it.
	//
	// Two things this cannot do anything about, both by nature of reading a COMPOSITED frame: whatever
	// is drawn in front of the point is what comes back (the local pointer's own reticle sits right
	// there, which is why this is the last resort behind the datamodel walk), and the alpha of a
	// composited pixel says nothing about the surface's opacity, so it reports opaque. Headless, a
	// point behind the eye, a point off screen and a refused readback all come back false rather than
	// guessing. -xlinka
	private sealed class GodotViewColorSampler : IViewColorSampler
	{
		private readonly Func<Camera3D?> _resolveCamera;

		public GodotViewColorSampler(Func<Camera3D?> resolveCamera)
		{
			_resolveCamera = resolveCamera;
		}

		public bool TrySample(float3 worldPoint, out colorHDR color)
		{
			color = colorHDR.White;
			try
			{
				var camera = _resolveCamera();
				if (camera == null || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree())
				{
					return false;
				}

				var point = new Vector3(worldPoint.x, worldPoint.y, worldPoint.z);
				if (camera.IsPositionBehind(point))
				{
					return false;
				}

				var viewport = camera.GetViewport();
				// In VR the window shows the headset's eye and renders no world of its own (XRModeManager),
				// so its pixels are not the view; an armed eyedropper turns that render back on.
				if (viewport == null || viewport.Disable3D)
				{
					return false;
				}

				var image = viewport.GetTexture()?.GetImage();
				if (image == null || image.IsEmpty())
				{
					return false;
				}

				// UnprojectPosition answers in the viewport's visible rect, which is not always the
				// backbuffer's pixel size (3D render scaling, window content scale). Normalize through
				// the rect and re-multiply by the image, or a scaled viewport samples the wrong pixel.
				var rect = viewport.GetVisibleRect();
				if (rect.Size.X <= 0f || rect.Size.Y <= 0f)
				{
					return false;
				}
				var screen = camera.UnprojectPosition(point);
				float u = screen.X / rect.Size.X;
				float v = screen.Y / rect.Size.Y;
				if (u < 0f || u >= 1f || v < 0f || v >= 1f)
				{
					return false;
				}

				int x = System.Math.Clamp((int)(u * image.GetWidth()), 0, image.GetWidth() - 1);
				int y = System.Math.Clamp((int)(v * image.GetHeight()), 0, image.GetHeight() - 1);

				// The laser's reticle is an additive quad ~10px across drawn exactly at the point being
				// read, so the centre pixel of the composited frame is contaminated by the cursor itself.
				// Average four pixels on a ring outside the reticle instead; any surface a pixel fallback
				// is aimed at is far bigger than the 14px ring, and the centre is deliberately skipped.
				const int ring = 14;
				float r = 0f, g = 0f, b = 0f;
				int samples = 0;
				ReadOnlySpan<(int dx, int dy)> offsets = stackalloc (int, int)[] { (ring, ring), (-ring, ring), (ring, -ring), (-ring, -ring) };
				foreach (var (dx, dy) in offsets)
				{
					int sx = x + dx, sy = y + dy;
					if (sx < 0 || sy < 0 || sx >= image.GetWidth() || sy >= image.GetHeight())
						continue;
					var p = image.GetPixelv(new Vector2I(sx, sy));
					r += p.R; g += p.G; b += p.B;
					samples++;
				}
				if (samples == 0)
				{
					var centre = image.GetPixelv(new Vector2I(x, y));
					color = new colorHDR(centre.R, centre.G, centre.B, 1f);
					return true;
				}
				color = new colorHDR(r / samples, g / samples, b / samples, 1f);
				return true;
			}
			catch (Exception ex)
			{
				LumoraLogger.Warn($"ViewColorSampler: read failed: {ex.Message}");
				return false;
			}
		}
	}

	// A picture of what the local view is showing, encoded small. Same viewport read as the colour
	// sampler above (and the same caveat: it is the COMPOSITED frame, so whatever is drawn in front is
	// in the shot), resized to the caller's box and JPEG encoded.
	//
	// The frame comes back in whatever format the renderer is running (HDR half-float on Forward+), and
	// the JPEG encoder only takes 8-bit, so convert before encoding or every capture comes back empty.
	// Headless and a refused readback both return false rather than handing back a blank image. -xlinka
	private sealed class GodotViewCapture : IViewCapture
	{
		private readonly Func<Viewport?> _resolveViewport;

		public GodotViewCapture(Func<Viewport?> resolveViewport)
		{
			_resolveViewport = resolveViewport;
		}

		public bool TryCapture(int width, int height, out byte[] jpeg)
		{
			jpeg = Array.Empty<byte>();
			try
			{
				var viewport = _resolveViewport();
				if (viewport == null || !GodotObject.IsInstanceValid(viewport))
					return false;

				var image = viewport.GetTexture()?.GetImage();
				if (image == null || image.IsEmpty())
					return false;

				if (width > 0 && height > 0 && (image.GetWidth() != width || image.GetHeight() != height))
					image.Resize(width, height, Image.Interpolation.Bilinear);
				if (image.GetFormat() != Image.Format.Rgb8)
					image.Convert(Image.Format.Rgb8);

				var data = image.SaveJpgToBuffer(JpegQuality);
				if (data == null || data.Length == 0)
					return false;

				jpeg = data;
				return true;
			}
			catch (Exception ex)
			{
				LumoraLogger.Warn($"ViewCapture: read failed: {ex.Message}");
				return false;
			}
		}

		// Thumbnails ride inside session announcements, so the bytes matter more than the last few
		// percent of fidelity at 256x144.
		private const float JpegQuality = 0.75f;
	}

	private void OnClipboardAssetImported(string filePath, Lumora.Core.Slot slot)
	{
		LumoraLogger.Debug($"ClipboardImporter: Asset imported from '{filePath}' to slot '{slot?.SlotName.Value}'");
	}


	private async Task PhaseUserspaceSetup()
	{
		LumoraLogger.Log("[Phase 6/6] Userspace Setup");
		_loadingScreen?.UpdatePhase(5);

		try
		{
			var userspace = Userspace.SetupUserspace(_engine);
			LumoraLogger.Log($"LumoraEngineRunner: Userspace created: '{userspace.WorldName.Value}'");

			var dashboardToggle = new DashboardToggle();
			dashboardToggle.Name = "DashboardToggle";
			AddChild(dashboardToggle);
			LumoraLogger.Debug("LumoraEngineRunner: DashboardToggle created");

		}
		catch (Exception ex)
		{
			LumoraLogger.Error($"LumoraEngineRunner: Failed to setup userspace: {ex.Message}");
			throw;
		}

		await Task.Delay(100); // Allow world to initialize
		LumoraLogger.Debug("LumoraEngineRunner: Userspace setup complete");
	}

	private async void OnEngineReady()
	{
		LumoraLogger.Log("LumoraEngineRunner: Engine initialization COMPLETE!");

		_loadingScreen?.UpdatePhase(6);

		_engineInitialized = true;

		// Publishes a picture of the world you are hosting into its session metadata, so other people's
		// world browsers show something other than a placeholder. Idle unless we are the authority.
		AddChild(new Lumora.Source.Godot.Services.SessionThumbnailService { Name = "SessionThumbnails" });

		// Instantiate the shared loading indicator now the scene tree exists. EnsureCreated was defined but never
		// called, so neither the world-join overlay nor the new import progress ever showed. Idempotent. -xlinka
		Lumora.Source.Godot.UI.WorldLoadingIndicator.EnsureCreated();

		// Discord rich presence (fails soft if no app ID / Discord not running). _Process refreshes it.
		// Keep invites pending through startup and other world loads. A repeat invite to an open world
		// focuses that world instead of creating a second connection to the same session.
		DiscordManager.JoinRequested = uri =>
		{
			if (_engine?.WorldManager == null || _engine.WorldLoadingService?.IsLoading == true)
				return false;
			foreach (var world in _engine.WorldManager.Worlds)
			{
				if (!world.IsDisposed && world.SessionURLs.Contains(uri))
				{
					_engine.WorldManager.FocusWorld(world);
					return true;
				}
			}
			_engine.JoinThroughLoader("Discord invite", uri);
			return true;
		};
		DiscordManager.Initialize();
		DiscordManager.UpdatePresence(_engine.WorldManager?.FocusedWorld);

		// What the settle gate and the shared load allowance read from this side.
		Lumora.Core.Engine.PlatformWorkPendingProvider = static () => MainThreadWork.Pending;
		Lumora.Core.Engine.SetDisplayRefreshRate(CurrentDisplayRefreshRate());

		if (_clipboardImporter != null)
		{
			_clipboardImporter.SetEngine(_engine);
			if (_engine?.WorldManager?.FocusedWorld != null)
			{
				_clipboardImporter.SetTargetSlot(_engine.WorldManager.FocusedWorld.RootSlot);
			}
			LumoraLogger.Debug("ClipboardImporter: Configured with engine reference");
		}

		// bound to the "I" key
		_inspectorInputHandler = new InspectorInputHandler();
		_inspectorInputHandler.Name = "InspectorInputHandler";
		_inspectorInputHandler.Engine = _engine;
		AddChild(_inspectorInputHandler);
		LumoraLogger.Debug("InspectorInputHandler: Created for object inspection");

		// Optional scene tree dump for deep diagnostics only.
		if (DumpSceneTreeOnReady)
		{
			await Task.Delay(500); // Wait for everything to settle
			PrintSceneTree();
		}

		DisplayServer.ScreenSetKeepOn(false);


		await Task.Delay(450); // Let the final progress update settle before fading into the world.

		if (_loadingScreen != null && GodotObject.IsInstanceValid(_loadingScreen))
		{
			LumoraLogger.Debug("LoadingScreen: Hiding loading screen");
			_loadingScreen.Dismiss();
			_loadingScreen = null!;
		}

		// Bootstrap.tscn carries a DirectionalLight3D for the loading phase and nothing ever turned it
		// off, so every world rendered under a second sun: a fixed 45 degree white fill at half energy
		// WITH shadows, on top of whatever sun the world authored. A fill from a direction the sky does
		// not agree with is the definition of flat lighting, and its cascade was a whole extra shadow
		// pass per frame that no world asked for. Worlds carry their own lights; the loading visuals
		// are unshaded and never needed it. -xlinka
		var bootstrapSun = GetNodeOrNull<DirectionalLight3D>("%DirectionalLight3D");
		if (bootstrapSun != null && bootstrapSun.Visible)
		{
			bootstrapSun.Visible = false;
			LumoraLogger.Log("LumoraEngineRunner: bootstrap DirectionalLight3D off, worlds light themselves");
		}
	}

	public override void _PhysicsProcess(double delta) => _stepPacer.OnTick();

	public override void _Process(double delta)
	{
		if (!_engineInitialized || _shutdownRequested)
			return;

		// Wall time since the last frame began. Godot's delta stops at max_physics_steps_per_frame
		// physics steps, 133 ms at the defaults, and a profiler that never sees past 133 ms hides
		// every real stall. -xlinka
		long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
		_wallFrameMs = _frameStartTs != 0 ? (frameStart - _frameStartTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : delta * 1000.0;
		_frameStartTs = frameStart;
		double engineDelta = _stepPacer.BeginFrame(delta, _wallFrameMs / 1000.0);

		if (_inputInterface == null && !_missingInputInterfaceWarned)
		{
			_missingInputInterfaceWarned = true;
			LumoraLogger.Warn("LumoraEngineRunner: No InputInterface available in _Process");
		}

#if GODOT_PC
		if (ShouldUseSteam())
			SteamManager.RunCallbacks();
#endif

		// Pump invite callbacks every frame; publish changed session details once a second.
		long _discordTs = System.Diagnostics.Stopwatch.GetTimestamp();
		DiscordManager.Poll();
		_hostDiscordMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _discordTs) * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
		_discordPresenceTimer += delta;
		if (_discordPresenceTimer >= 1.0)
		{
			_discordPresenceTimer = 0.0;
			DiscordManager.UpdatePresence(_engine?.WorldManager?.FocusedWorld);
		}

		// The headset can come and go and a window can move monitors, so the load allowance follows the rate.
		_refreshRatePollTimer += delta;
		if (_refreshRatePollTimer >= 1.0)
		{
			_refreshRatePollTimer = 0.0;
			Lumora.Core.Engine.SetDisplayRefreshRate(CurrentDisplayRefreshRate());
		}

		Lumora.Godot.Debug.VideoMemoryWatch.Tick(delta, GetTree()?.Root);

		// Pump every registered network manager (LNL + Steam if present). Status-
		// changed callbacks fire during SteamManager.RunCallbacks() above, so
		// dispatching transports after that ensures connection state transitions
		// are observed in the same frame they occur. - xlinka
		// The first clean capture put 33% of the frame in "unaccounted" even after the engine phases
		// were instrumented, and engine input/coroutines/fixed/assets all came back at ~0. So the time
		// is either OUR work here in _Process outside Engine.Update, or Godot's own frame. Splitting
		// those two apart is the difference between a bug we can fix and a platform cost we cannot.
		// -xlinka
		long _hostTs = System.Diagnostics.Stopwatch.GetTimestamp();
		double _hostMspt = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		NetworkManagerRegistry.UpdateAll();
		_hostNetworkMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _hostTs) * _hostMspt;

		// Run engine update loop (includes one input pass + world updates).
		// Avoid a duplicate InputInterface.UpdateInputs call here.
		long _engineTs = System.Diagnostics.Stopwatch.GetTimestamp();
		_engine?.Update(engineDelta);
		_engine?.LateUpdate(engineDelta);
		_hostEngineMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _engineTs) * _hostMspt;

		long _tailTs = System.Diagnostics.Stopwatch.GetTimestamp();
		UpdateGodotMetrics(delta);
		SendDebugFrame(delta);
		SendDebugPerf(delta);
		SendDebugMemory(delta);
		SendDebugRenderProfile(delta);
		SendDebugNetwork(delta);

		_headOutput?.UpdatePositioning(_engine);
		_dashOverlay?.Tick(_engine);
		Lumora.Source.Godot.Rendering.CameraSanity.Check(GetViewport(), _xrViewport);
		Lumora.Source.Godot.Rendering.PipelineMsaa.Follow(_xrViewport != null && GodotObject.IsInstanceValid(_xrViewport) && _xrViewport.UseXR ? _xrViewport : GetViewport());
		// Includes the telemetry send itself, so the profiler is honest about its own cost.
		_hostTailMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _tailTs) * _hostMspt;

		_frameLog?.Record(this, delta, _hostNetworkMs, _hostEngineMs, _hostTailMs);
	}

	private double _hostNetworkMs;
	private double _hostEngineMs;
	private double _hostTailMs;
	private long _frameStartTs;
	private double _wallFrameMs;

	public override void _Input(InputEvent @event)
	{
		base._Input(@event);

		if (!_engineInitialized)
			return;

		_mouseDriver?.HandleInputEvent(@event);
		_keyboardDriver?.HandleInputEvent(@event);
	}

	private void OnEnginePanic(Exception ex)
	{
		LumoraLogger.Error($"ENGINE PANIC: {ex.Message}");
		LumoraLogger.Error($"Stack trace: {ex.StackTrace}");
		_shutdownRequested = true;
	}

	private void OnEngineShutdown()
	{
		LumoraLogger.Log("Engine shutdown requested");
		_shutdownRequested = true;
	}

	// Defer the tree quit so it runs at a safe point rather than mid engine-update.
	private void OnQuitRequested()
	{
		LumoraLogger.Log("LumoraEngineRunner: Quit requested; closing application");
		Callable.From(() => GetTree().Quit()).CallDeferred();
	}


	// fewer, broader connectors; called before engine initialization
	private void RegisterHooks()
	{
		LumoraLogger.Debug("Registering Godot hooks...");
		GodotHookRegistry.RegisterAll();

		LumoraLogger.Log("Hook registration complete");
	}

	// Locale tables ship as Assets/Locale/<code>.json. In the editor they are real files and the locale
	// manager's own scan finds them through ResourceRoot; in an export there is no such file on disk, so
	// the bytes have to come out of the resource pack through Godot's VFS - the same split the font hook
	// deals with. Both paths are run: the manager is additive and first-wins, so loading a table twice
	// is a no-op rather than a duplicate. -xlinka
	private static void LoadBundledLocales()
	{
		Lumora.Core.Localization.LocaleManager.Reload();

		const string localeDir = "res://Assets/Locale";
		string[] names;
		try
		{
			names = DirAccess.GetFilesAt(localeDir);
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"Locales: could not list {localeDir}: {ex.Message}");
			return;
		}
		if (names == null)
			return;

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var raw in names)
		{
			// Imported and remapped resources are listed under their sidecar name in an export.
			var name = raw;
			if (name.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
				name = name.Substring(0, name.Length - 7);
			else if (name.EndsWith(".remap", StringComparison.OrdinalIgnoreCase))
				name = name.Substring(0, name.Length - 6);
			if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
				continue;

			var path = localeDir + "/" + name;
			var json = FileAccess.FileExists(path) ? FileAccess.GetFileAsString(path) : LoadImportedJson(path);
			if (string.IsNullOrWhiteSpace(json))
				continue;
			Lumora.Core.Localization.LocaleManager.LoadJson(json, System.IO.Path.GetFileNameWithoutExtension(name));
		}
	}

	// A .json under res:// gets picked up by the JSON importer, and an export then ships the imported
	// resource with the raw file replaced by a remap - so FileAccess on the original path finds nothing.
	// Pull the parsed data back out and re-serialize it; the locale loader wants text either way.
	private static string? LoadImportedJson(string path)
	{
		try
		{
			var resource = ResourceLoader.Load<Json>(path);
			return resource == null ? null : Json.Stringify(resource.Data);
		}
		catch (Exception ex)
		{
			LumoraLogger.Warn($"Locales: could not read {path}: {ex.Message}");
			return null;
		}
	}

	private void UpdateGodotMetrics(double delta)
	{
		if (_engine?.WorldManager?.FocusedWorld == null) return;

		var metrics = _engine.WorldManager.FocusedWorld.Metrics;
		var perfMonitor = Performance.Singleton;

		metrics.GodotFps = perfMonitor.GetMonitor(Performance.Monitor.TimeFps);
		if (metrics.GodotFps <= 0)
		{
			metrics.GodotFps = global::Godot.Engine.GetFramesPerSecond();
		}
		metrics.GodotFrameTimeMs = metrics.GodotFps > 0 ? 1000.0 / metrics.GodotFps : delta * 1000.0;

		// Godot exposes this as CPU process time, not GPU render time.
		metrics.RenderTimeMs = perfMonitor.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
		metrics.PhysicsTimeMs = perfMonitor.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;

		metrics.VideoMemoryBytes = (long)perfMonitor.GetMonitor(Performance.Monitor.RenderVideoMemUsed);

		metrics.GodotObjectCount = (int)perfMonitor.GetMonitor(Performance.Monitor.ObjectCount);
		metrics.GodotNodeCount = (int)perfMonitor.GetMonitor(Performance.Monitor.ObjectNodeCount);
	}

	private void SendDebugPerf(double delta)
	{
		if (_debugUdpSender == null || _engine?.WorldManager?.FocusedWorld == null)
		{
			return;
		}

		_debugPerfTimer += delta;
		if (_debugPerfTimer < DebugPerfSendIntervalSec)
		{
			return;
		}
		_debugPerfTimer = 0;

		var world = _engine.WorldManager.FocusedWorld;
		var metrics = world.Metrics;
		var fps = world.LocalUser?.FPS.Value ?? (float)global::Godot.Engine.GetFramesPerSecond();

		// Frame time used to be 1000/fps, where fps is the REPLICATED LocalUser.FPS - a value rounded to
		// a whole number and only written every 30 frames. So the profiler graph was drawing a derived,
		// quantised, stale number and calling it a measurement: a frame time of 16.67 there could be
		// anything from 16.1 to 17.2 ms, and a single 200 ms hitch never showed up at all. This is the
		// frame's measured wall time. -xlinka
		var frameTime = (float)_wallFrameMs;

		_debugUdpSender.SendPerf(
			fps,
			frameTime,
			(float)metrics.RenderTimeMs,
			(float)metrics.PhysicsTimeMs,
			world.WorldName.Value ?? "Unnamed",
			metrics.SlotCount,
			metrics.ComponentCount,
			world.GetAllUsers().Count,
			GC.GetTotalMemory(false),
			metrics.VideoMemoryBytes,
			metrics.GodotObjectCount,
			metrics.GodotNodeCount);
	}

	private void SendDebugMemory(double delta)
	{
		if (_debugUdpSender == null || _engine?.WorldManager?.FocusedWorld == null)
		{
			return;
		}

		_debugMemoryTimer += delta;
		if (_debugMemoryTimer < DebugMemorySendIntervalSec)
		{
			return;
		}
		_debugMemoryTimer = 0;

		var world = _engine.WorldManager.FocusedWorld;
		var metrics = world.Metrics;

		var gcBytes = GC.GetTotalMemory(false);
		var gcInfo = GC.GetGCMemoryInfo();
		var committedBytes = gcInfo.TotalCommittedBytes > 0 ? gcInfo.TotalCommittedBytes : gcBytes;

		long workingSetBytes = 0;
		long privateBytes = 0;

		try
		{
			using var process = Process.GetCurrentProcess();
			workingSetBytes = process.WorkingSet64;
			privateBytes = process.PrivateMemorySize64;
		}
		catch
		{
			// Ignore process metric failures in restricted environments.
		}

		var breakdown = BuildComponentMemoryBreakdown(world.RootSlot);
		var estimatedBytes = breakdown.Sum(x => x.bytes);
		var topComponents = breakdown
			.OrderByDescending(x => x.bytes)
			.Take(24)
			.ToList();

		_debugUdpSender.SendMemory(
			committedBytes,
			gcBytes,
			GC.CollectionCount(0),
			GC.CollectionCount(1),
			GC.CollectionCount(2),
			estimatedBytes,
			workingSetBytes,
			privateBytes,
			metrics.VideoMemoryBytes,
			metrics.GodotObjectCount,
			metrics.GodotNodeCount,
			topComponents);
	}

	// Per-frame cost breakdown for the Frame tab. Sampled at the same rate as the other telemetry, but
	// what it reports is the LAST frame in full rather than an average, so a spike stays a spike.
	//
	// ViewportSetMeasureRenderTime is enabled once and only under --Lumora-Debug: Godot has to fence the
	// GPU to answer it, so it is not something to leave on for everyone. -xlinka
	private readonly HashSet<Rid> _measuredViewports = new();
	private readonly List<Lumora.Core.UpdateManager.ProfileEntry> _hookCostBuffer = new();
	private readonly List<SubViewport> _liveViewportBuffer = new();
	private readonly List<DebugUdpSender.FrameViewport> _viewportStatsBuffer = new();
	private readonly List<Lumora.Core.Input.InputInterface.ReceiverProfileEntry> _receiverProfileBuffer = new();

	private void SendDebugFrame(double delta)
	{
		if (_debugUdpSender == null || _engine?.WorldManager?.FocusedWorld == null)
		{
			return;
		}

		_debugFrameTimer += delta;
		if (_debugFrameTimer < DebugPerfSendIntervalSec)
		{
			return;
		}
		_debugFrameTimer = 0;

		var world = _engine.WorldManager.FocusedWorld;
		var profile = world.LastFrameProfile;
		var enginePhases = _engine.LastEnginePhases;

		var perf = Performance.Singleton;

		Rid viewport = GetViewport()?.GetViewportRid() ?? default;
		double renderCpuMs = 0.0, renderGpuMs = 0.0;
		long drawsVisible = 0, drawsShadow = 0, drawsCanvas = 0, objectsVisible = 0, objectsShadow = 0;

		if (viewport.IsValid)
		{
			EnsureMeasured(viewport);
			renderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
			renderGpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);

			drawsVisible = RenderingServer.ViewportGetRenderInfo(viewport,
				RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
			drawsShadow = RenderingServer.ViewportGetRenderInfo(viewport,
				RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
			drawsCanvas = RenderingServer.ViewportGetRenderInfo(viewport,
				RenderingServer.ViewportRenderInfoType.Canvas, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
			objectsVisible = RenderingServer.ViewportGetRenderInfo(viewport,
				RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame);
			objectsShadow = RenderingServer.ViewportGetRenderInfo(viewport,
				RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.ObjectsInFrame);
		}

		// Every offscreen viewport costed separately. The dash alone renders the world again, and so does
		// every mirror and camera; a single global draw-call total can never say which of them is the
		// expensive one. -xlinka
		_liveViewportBuffer.Clear();
		Lumora.Godot.Hooks.RenderTextureHook.CollectLiveViewports(_liveViewportBuffer);
		_viewportStatsBuffer.Clear();
		_viewportStatsBuffer.Add(new DebugUdpSender.FrameViewport("main", drawsVisible + drawsShadow + drawsCanvas,
			renderGpuMs, renderCpuMs));
		foreach (var sub in _liveViewportBuffer)
		{
			var rid = sub.GetViewportRid();
			if (!rid.IsValid)
				continue;
			EnsureMeasured(rid);
			long draws =
				RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame)
				+ RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame)
				+ RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Canvas, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
			var size = sub.Size;
			_viewportStatsBuffer.Add(new DebugUdpSender.FrameViewport(
				$"{sub.Name} {size.X}x{size.Y}",
				draws,
				RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid),
				RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid)));
		}

		// VMA's own accounting, which is closer to the truth than the RenderingInfo totals, plus the
		// driver's one-frame report. Null on the Compatibility backend and under --headless, so all of it
		// stays at zero rather than pretending.
		long gpuTexture = 0, gpuBuffer = 0, gpuTotal = 0, driverAllocs = 0, driverMem = 0;
		string perfReport = string.Empty;
		var device = RenderingServer.GetRenderingDevice();
		if (device != null)
		{
			gpuTexture = (long)device.GetMemoryUsage(RenderingDevice.MemoryType.Textures);
			gpuBuffer = (long)device.GetMemoryUsage(RenderingDevice.MemoryType.Buffers);
			gpuTotal = (long)device.GetMemoryUsage(RenderingDevice.MemoryType.Total);
			driverAllocs = (long)device.GetDriverAllocationCount();
			driverMem = (long)device.GetDriverTotalMemory();
			perfReport = device.GetPerfReport() ?? string.Empty;
		}

		_hookCostBuffer.Clear();
		world.UpdateManager?.CollectHookCost(_hookCostBuffer);
		var topHooks = _hookCostBuffer
			.OrderByDescending(e => e.Ms)
			.Take(24)
			.Select(e => (e.Name, e.Ms, e.Count));

		_debugUdpSender.SendFrame(new DebugUdpSender.FramePacket
		{
			HostNetworkMs = _hostNetworkMs,
			HostEngineMs = _hostEngineMs,
			HostTailMs = _hostTailMs,
			EngineInputMs = enginePhases.InputMs,
			EngineCoroutinesMs = enginePhases.CoroutinesMs,
			EngineFixedMs = enginePhases.FixedMs,
			EngineAssetsMs = enginePhases.AssetsMs,
			CpuFrameMs = _wallFrameMs,
			WorldTotalMs = profile.TotalMs,
			SyncMs = profile.SyncMs,
			PreMs = profile.PreMs,
			CompMs = profile.CompMs,
			ChangeMs = profile.ChangeMs,
			HooksMs = profile.HooksMs,
			EndMs = profile.EndMs,
			LateMs = profile.LateMs,
			ScriptsMs = profile.ScriptsMs,
			RenderCpuMs = renderCpuMs,
			RenderGpuMs = renderGpuMs,
			FrameSetupCpuMs = RenderingServer.GetFrameSetupTimeCpu(),
			DrawsVisible = drawsVisible,
			DrawsShadow = drawsShadow,
			DrawsCanvas = drawsCanvas,
			ObjectsVisible = objectsVisible,
			ObjectsShadow = objectsShadow,
			GpuTextureBytes = gpuTexture,
			GpuBufferBytes = gpuBuffer,
			GpuTotalBytes = gpuTotal,
			OrphanNodes = (long)perf.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount),
			StaticMemBytes = (long)perf.GetMonitor(Performance.Monitor.MemoryStatic),
			PhysicsActiveObjects = (long)perf.GetMonitor(Performance.Monitor.Physics3DActiveObjects),
			PhysicsCollisionPairs = (long)perf.GetMonitor(Performance.Monitor.Physics3DCollisionPairs),
			PhysicsIslands = (long)perf.GetMonitor(Performance.Monitor.Physics3DIslandCount),
			PipeCanvas = PipelineDelta(0, perf, Performance.Monitor.PipelineCompilationsCanvas),
			PipeMesh = PipelineDelta(1, perf, Performance.Monitor.PipelineCompilationsMesh),
			PipeSurface = PipelineDelta(2, perf, Performance.Monitor.PipelineCompilationsSurface),
			PipeDraw = PipelineDelta(3, perf, Performance.Monitor.PipelineCompilationsDraw),
			PipeSpecialization = PipelineDelta(4, perf, Performance.Monitor.PipelineCompilationsSpecialization),
			DriverAllocationCount = driverAllocs,
			DriverTotalMemBytes = driverMem,
			PerfReport = perfReport,
			HookCosts = topHooks,
			Viewports = _viewportStatsBuffer,
		});

		_pipelineBaselineTaken = true;
	}

	// The PipelineCompilations monitors are CUMULATIVE lifetime counters, not per-frame counts - the
	// first real capture made that obvious: the series was monotonic and sat flat at 411 for the whole
	// tail. Reporting the raw value meant the console would shout "411 shader compiles this frame"
	// forever after the first one, which is worse than not reporting it. The delta is the actual signal:
	// nonzero means the driver stopped to compile something THIS sample. -xlinka
	private readonly long[] _lastPipelineCounts = new long[5];
	private bool _pipelineBaselineTaken;

	private long PipelineDelta(int index, PerformanceInstance perf, Performance.Monitor monitor)
	{
		long current = (long)perf.GetMonitor(monitor);
		long previous = _lastPipelineCounts[index];
		_lastPipelineCounts[index] = current;
		// First sample has no previous to subtract, and the startup compiles are not news.
		if (!_pipelineBaselineTaken)
			return 0;
		return current > previous ? current - previous : 0;
	}

	// Measurement is per viewport and has to be switched on before it reports anything. It costs a GPU
	// fence, so it is only ever enabled under --Lumora-Debug and only once per viewport.
	private void EnsureMeasured(Rid viewport)
	{
		if (_measuredViewports.Add(viewport))
			RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
	}

	private void SendDebugRenderProfile(double delta)
	{
		if (_debugUdpSender == null || _engine?.WorldManager?.FocusedWorld == null)
		{
			return;
		}

		_debugRenderProfileTimer += delta;
		if (_debugRenderProfileTimer < DebugPerfSendIntervalSec)
		{
			return;
		}
		_debugRenderProfileTimer = 0;

		// GPU / render-pipeline stats straight from the renderer - this is the "Render / GPU" debug tab.
		_debugUdpSender.SendRenderStats(
			(long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
			(long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame),
			(long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame),
			(long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed),
			(long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed),
			(long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));

		// Latest-frame update profile, by component type AND by slot, top entries by cost.
		var updateManager = _engine.WorldManager.FocusedWorld.UpdateManager;
		if (updateManager != null)
		{
			_profByTypeBuffer.Clear();
			_profBySlotBuffer.Clear();
			updateManager.CollectProfile(_profByTypeBuffer, _profBySlotBuffer);

			// Input receivers are components too, and they are the expensive ones, so they belong in the
			// same by-type list rather than a tab of their own. The "(input)" suffix matches the phase
			// tagging the update profiler already uses.
			_receiverProfileBuffer.Clear();
			_engine.InputInterface?.CollectReceiverProfile(_receiverProfileBuffer);
			foreach (var entry in _receiverProfileBuffer)
				_profByTypeBuffer.Add(new Lumora.Core.UpdateManager.ProfileEntry(entry.Name, entry.Ms, entry.Count));

			var topComponents = _profByTypeBuffer
				.OrderByDescending(e => e.Ms)
				.Take(40)
				.Select(e => (e.Name, e.Ms, e.Count));
			var topSlots = _profBySlotBuffer
				.OrderByDescending(e => e.Ms)
				.Take(40)
				.Select(e => (e.Name, e.Ms, e.Count));

			_debugUdpSender.SendProfile(topComponents, topSlots);
		}
	}

	private void SendDebugNetwork(double delta)
	{
		if (_debugUdpSender == null || _engine?.WorldManager?.FocusedWorld == null)
		{
			return;
		}

		_debugNetworkTimer += delta;
		if (_debugNetworkTimer < DebugPerfSendIntervalSec)
		{
			return;
		}
		_debugNetworkTimer = 0;

		var world = _engine.WorldManager.FocusedWorld;
		var session = world.Session;

		// No session: send an offline snapshot so the Network tab reads "Local" with no traffic/rows.
		if (session == null || session.IsDisposed)
		{
			_debugUdpSender.SendNetwork(
				"Local", world.WorldName.Value ?? "Unnamed", "-", "-",
				0, 0, -1, false,
				0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0, 0, 0, 0, 0, 0, 0,
				Array.Empty<(string, string, string, int, bool, ulong)>());
			return;
		}

		var conns = session.Connections;
		var sync = session.Sync;
		var meta = session.Metadata;
		var assets = session.AssetTransferer;

		// Every mapped connection, plus the host connection on a client (it may not be user-mapped yet).
		var allConns = conns.GetAllConnections();
		var host = conns.HostConnection;
		if (host != null && !allConns.Contains(host))
		{
			allConns.Add(host);
		}

		var rows = new List<(string name, string transport, string endpoint, int ping, bool encrypted, ulong recvBytes)>();
		bool allEncrypted = allConns.Count > 0;
		foreach (var c in allConns)
		{
			if (c == null)
			{
				continue;
			}

			string name;
			if (conns.TryGetUser(c, out var user) && user != null)
			{
				name = user.UserName.Value ?? "(unknown)";
			}
			else
			{
				name = c == host ? "(host)" : "(pending)";
			}

			var endpoint = c.Identifier ?? c.Address?.ToString() ?? "-";
			rows.Add((name, c.TransportName, endpoint, c.Ping, c.IsEncrypted, c.ReceivedBytes));
			if (!c.IsEncrypted)
			{
				allEncrypted = false;
			}
		}

		// Latency: the host connection's ping on a client; the authority is the host, so 0.
		int latencyMs = world.IsAuthority ? 0 : (host?.Ping ?? -1);

		_debugUdpSender.SendNetwork(
			world.IsAuthority ? "Host" : "Client",
			world.WorldName.Value ?? "Unnamed",
			meta?.SessionId ?? "-",
			meta?.Visibility.ToString() ?? "-",
			sync?.SyncRate ?? 0,
			rows.Count,
			latencyMs,
			allEncrypted,
			sync?.TotalSentDeltas ?? 0, sync?.TotalReceivedDeltas ?? 0,
			sync?.TotalSentFulls ?? 0, sync?.TotalReceivedFulls ?? 0,
			sync?.TotalSentStreams ?? 0, sync?.TotalReceivedStreams ?? 0,
			sync?.TotalSentRawFrames ?? 0, sync?.TotalReceivedRawFrames ?? 0,
			sync?.TotalCorrections ?? 0, sync?.TotalProcessedMessages ?? 0,
			sync?.LastGeneratedDeltaChanges ?? 0,
			sync?.MessagesToProcessCount ?? 0, sync?.MessagesToTransmitCount ?? 0,
			sync?.IncomingRawCount ?? 0, sync?.PendingStreamCount ?? 0,
			assets?.UploadJobCount ?? 0, assets?.DownloadJobCount ?? 0,
			assets?.PendingAssetRequestCount ?? 0, assets?.PendingRelayCount ?? 0,
			rows);
	}

	private static List<(string name, int count, long bytes)> BuildComponentMemoryBreakdown(Slot rootSlot)
	{
		var perType = new Dictionary<string, (int count, long bytes)>(StringComparer.Ordinal);
		var stack = new Stack<Slot>();
		stack.Push(rootSlot);

		while (stack.Count > 0)
		{
			var slot = stack.Pop();

			foreach (var component in slot.Components)
			{
				if (component == null)
				{
					continue;
				}

				var type = component.GetType();
				var typeName = type.Name;
				var estimated = EstimateComponentMemory(type);

				if (perType.TryGetValue(typeName, out var existing))
				{
					perType[typeName] = (existing.count + 1, existing.bytes + estimated);
				}
				else
				{
					perType[typeName] = (1, estimated);
				}
			}

			foreach (var child in slot.Children)
			{
				stack.Push(child);
			}
		}

		var result = new List<(string name, int count, long bytes)>(perType.Count);
		foreach (var entry in perType)
		{
			result.Add((entry.Key, entry.Value.count, entry.Value.bytes));
		}

		return result;
	}

	private static long EstimateComponentMemory(Type type)
	{
		if (ComponentMemoryEstimateCache.TryGetValue(type, out var cached))
		{
			return cached;
		}

		long estimate = 64; // base object overhead

		var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
		foreach (var property in properties)
		{
			if (!typeof(ISyncMember).IsAssignableFrom(property.PropertyType))
			{
				continue;
			}

			var syncType = property.PropertyType;
			if (!syncType.IsGenericType)
			{
				estimate += 32;
				continue;
			}

			var valueType = syncType.GetGenericArguments().FirstOrDefault();
			if (valueType == typeof(string))
				estimate += 80;
			else if (valueType == typeof(float) || valueType == typeof(int))
				estimate += 24;
			else if (valueType == typeof(float2))
				estimate += 32;
			else if (valueType == typeof(float3))
				estimate += 40;
			else if (valueType == typeof(float4) || valueType == typeof(floatQ))
				estimate += 48;
			else if (valueType == typeof(float4x4))
				estimate += 96;
			else if (valueType == typeof(bool))
				estimate += 20;
			else if (valueType?.IsEnum == true)
				estimate += 24;
			else
				estimate += 48;
		}

		ComponentMemoryEstimateCache[type] = estimate;
		return estimate;
	}

	private void PrintSceneTree()
	{
		LumoraLogger.Debug("==========================================================");
		LumoraLogger.Debug("DEBUG: SCENE TREE DUMP");
		LumoraLogger.Debug("==========================================================");
		PrintNodeTree(GetTree().Root, 0);
		LumoraLogger.Debug("==========================================================");

		LumoraLogger.Debug($"Engine worlds count: {_engine?.WorldManager?.Worlds?.Count ?? 0}");
		if (_engine?.WorldManager?.Worlds != null)
		{
			foreach (var world in _engine.WorldManager.Worlds)
			{
				LumoraLogger.Debug($"  World: {world.WorldName.Value}");
				LumoraLogger.Debug($"    State: {world.State}");
				LumoraLogger.Debug($"    Focus: {world.Focus}");
				LumoraLogger.Debug($"    RootSlot children: {world.RootSlot?.Children.Count ?? 0}");
				if (world.RootSlot != null)
				{
					PrintSlotTree(world.RootSlot, 4);
				}
			}
		}
		LumoraLogger.Debug("==========================================================");
	}

	private void PrintNodeTree(Node node, int indent)
	{
		string prefix = new string(' ', indent);
		LumoraLogger.Debug($"{prefix}{node.Name} ({node.GetType().Name}) - Visible: {node is CanvasItem ci && ci.Visible || node is Node3D n3d && n3d.Visible}");

		foreach (Node child in node.GetChildren())
		{
			PrintNodeTree(child, indent + 2);
		}
	}

	private void PrintSlotTree(Lumora.Core.Slot slot, int indent)
	{
		string prefix = new string(' ', indent);
		LumoraLogger.Debug($"{prefix}Slot: {slot.SlotName.Value}");
		LumoraLogger.Debug($"{prefix}  Components: {slot.Components.Count}");
		foreach (var component in slot.Components)
		{
			LumoraLogger.Debug($"{prefix}    - {component.GetType().Name}");
		}

		foreach (var child in slot.Children)
		{
			PrintSlotTree(child, indent + 2);
		}
	}

	public override void _ExitTree()
	{
		if (DiscordManager.ExitAfterHandoff)
		{
			base._ExitTree();
			return;
		}
		Lumora.Godot.Hooks.GpuTextureVariants.Shutdown();
		LumoraLogger.Log("LumoraEngineRunner: Shutting down...");
		DisconnectOpenXREvents();

		// Hand the eye and lip cameras back before anything else goes down. A module left open keeps
		// the SRanipal service holding them and the next app to ask gets BUSY.
		_sranipalDriver?.Shutdown();

		if (_ownsDebugConsoleLock && _debugConsoleInstanceMutex != null)
		{
			try
			{
				_debugConsoleInstanceMutex.ReleaseMutex();
			}
			catch
			{
				// No-op: lock may already be released during shutdown.
			}
		}

		_debugConsoleInstanceMutex?.Dispose();
		_debugConsoleInstanceMutex = null;
		_ownsDebugConsoleLock = false;

		_debugUdpSender?.Dispose();
		_frameLog?.Dispose();
		_frameLog = null;
		// Stop transports before the SteamAPI shuts down - the Steam manager
		// frees its sockets/poll groups via SteamNetworkingSockets calls that
		// need the API still up. - xlinka
		DiscordManager.Shutdown();
		NetworkManagerRegistry.StopAll();
		_engine?.Dispose();
		_headOutput?.Dispose();
#if GODOT_PC
		if (ShouldUseSteam())
			SteamManager.Shutdown();
#endif

		// The console prints on the logger's thread; what is still queued goes out here, and nothing after
		// this reaches a Godot that is going away. The log file still gets it. -xlinka
		LumoraLogger.FlushToGameEngine();
		LumoraLogger.LogToGameEngine -= LogCallback;

		base._ExitTree();
	}

	// On for every desktop run, editor included, beside the direct transport: a host listens on both and
	// a joiner outside its network gets in over Steam. Without the Steam client running, Init fails and the
	// game carries on direct only. SteamAPI.Init also makes the client inject its overlay
	// (gameoverlayrenderer64.dll), which crashed an editor session once (WER faulting module, 2026-09-16);
	// --no-steam turns it off for a run if that comes back. -xlinka
	// Asked every frame; the answer is fixed at launch.
	private static bool? _useSteam;

	private static bool ShouldUseSteam() => _useSteam ??= ResolveUseSteam();

	private static bool ResolveUseSteam()
	{
		if (OS.HasFeature("android"))
			return false;
		return !LaunchArgs.HasFlag("--no-steam");
	}

	// Runs once after the engine is initialized so transports can publish
	// session URIs the moment a host opens a listener. LNL is always
	// registered; Steam is registered only when SteamAPI initialised
	// successfully and SteamNetworkingSockets is available. - xlinka
	private static void RegisterNetworkManagers()
	{
		var lnl = new LNLNetworkManager();
		NetworkManagerRegistry.Register(lnl);

#if GODOT_PC
		if (ShouldUseSteam() && SteamManager.Initialized)
		{
			var steam = new SteamNetworkManager();
			if (steam.Initialize())
			{
				NetworkManagerRegistry.Register(steam);
			}
			else
			{
				LumoraLogger.Warn("LumoraEngineRunner: SteamNetworkManager unavailable - falling back to LNL only");
			}
		}
#endif
	}
}
