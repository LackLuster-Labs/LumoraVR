using System;
using Godot;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Godot.Bootstrap;

/// <summary>
/// SystemInfoHook - Tracks system performance metrics.
/// Platform system information hook for Godot.
///
/// Responsibilities:
/// - Track FPS (frames per second)
/// - Monitor GPU time
/// - Track output device info
/// - Provide system stats to Engine
/// </summary>
public partial class SystemInfoHook : Node
{
    // ===== PERFORMANCE TRACKING =====
    private double _lastFrameTime = 0;
    private double _deltaTimeAccumulator = 0;
    private int _frameCount = 0;
    private const int FPS_SAMPLE_FRAMES = 60;

    // ===== PUBLIC STATS =====
    public float CurrentFPS { get; private set; } = 0f;
    public float AverageFPS { get; private set; } = 0f;
    public float GPUTimeMs { get; private set; } = 0f;
    public string OutputDevice { get; private set; } = "Unknown";

    // ===== SYSTEM INFO =====
    public string GPUName { get; private set; } = "Unknown";
    public string CPUName { get; private set; } = "Unknown";
    public long TotalMemoryMB { get; private set; } = 0;
    public string OSName { get; private set; } = "Unknown";

    public override void _Ready()
    {
        // Gather system info
        GatherSystemInfo();

        _lastFrameTime = Time.GetTicksUsec() / 1000000.0;

        AquaLogger.Log("SystemInfoHook: Initialized");
        AquaLogger.Log($"  GPU: {GPUName}");
        AquaLogger.Log($"  CPU: {CPUName}");
        AquaLogger.Log($"  OS: {OSName}");
        AquaLogger.Log($"  Memory: {TotalMemoryMB} MB");
    }

    /// <summary>
    /// Gather system information.
    /// </summary>
    private void GatherSystemInfo()
    {
        // GPU info
        GPUName = RenderingServer.GetVideoAdapterName();

        // CPU info
        CPUName = OS.GetProcessorName();

        // OS info
        OSName = OS.GetName() + " " + OS.GetVersion();

        // Memory info
        var memInfo = OS.GetMemoryInfo();
        if (memInfo.ContainsKey("physical"))
        {
            TotalMemoryMB = (long)memInfo["physical"] / (1024 * 1024);
        }

        // Output device (VR or Screen)
        var xrInterface = XRServer.FindInterface("OpenXR");
        if (xrInterface != null && xrInterface.IsInitialized())
        {
            OutputDevice = "OpenXR VR Headset";
        }
        else
        {
            OutputDevice = "Screen";
        }
    }

    /// <summary>
    /// Update performance metrics every frame.
    /// </summary>
    public override void _Process(double delta)
    {
        // Track FPS
        double currentTime = Time.GetTicksUsec() / 1000000.0;
        double frameDelta = currentTime - _lastFrameTime;
        _lastFrameTime = currentTime;

        // Instant FPS
        if (frameDelta > 0)
        {
            CurrentFPS = (float)(1.0 / frameDelta);
        }

        // Average FPS (rolling average over FPS_SAMPLE_FRAMES)
        _deltaTimeAccumulator += frameDelta;
        _frameCount++;

        if (_frameCount >= FPS_SAMPLE_FRAMES)
        {
            AverageFPS = (float)(FPS_SAMPLE_FRAMES / _deltaTimeAccumulator);
            _deltaTimeAccumulator = 0;
            _frameCount = 0;
        }

        // GPU time (Godot 4.x doesn't expose direct GPU time easily)
        // Approximate from frame time
        GPUTimeMs = (float)(frameDelta * 1000.0);
    }

    /// <summary>
    /// Get debug string with all stats.
    /// </summary>
    public string GetDebugString()
    {
        return $"FPS: {CurrentFPS:F1} (Avg: {AverageFPS:F1}) | GPU: {GPUTimeMs:F2}ms | Device: {OutputDevice}";
    }
}
