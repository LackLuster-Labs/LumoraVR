using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// View modes for the engine debug wizard.
/// </summary>
public enum EngineDebugView
{
    /// <summary>Performance overview and FPS.</summary>
    Performance,
    /// <summary>World hierarchy and stats.</summary>
    World,
    /// <summary>Memory usage and GC.</summary>
    Memory,
    /// <summary>Input devices and tracking.</summary>
    Input,
    /// <summary>Asset loading and management.</summary>
    Assets,
    /// <summary>Audio system status.</summary>
    Audio
}

/// <summary>
/// Engine Debug Wizard - comprehensive engine performance and debug information.
/// Enhanced version with performance history, asset tracking, and detailed metrics.
/// </summary>
[ComponentCategory("HelioUI/Wizard")]
public class EngineDebugWizard : HelioWizardForm
{
    // ===== CONFIGURATION =====

    protected override float2 CanvasSize => new float2(900f, 1100f);
    protected override float CanvasScale => 1400f; // pixels per unit, ~0.8m tall

    // ===== STATE =====

    public Sync<EngineDebugView> CurrentView { get; private set; }
    public Sync<float> RefreshInterval { get; private set; }

    // UI References
    private HelioText _headerText;
    private HelioText _mainInfoText;
    private HelioText _detailsText;
    private HelioText _statusText;
    private HelioText _budgetText;
    private float _refreshTimer;

    // Performance tracking
    private float _lastFrameTime;
    private float _frameTimeSmooth;
    private float _frameTimeMin = float.MaxValue;
    private float _frameTimeMax;
    private int _frameCount;
    private float _fpsTimer;
    private float _currentFPS;
    private float _minFPS = float.MaxValue;
    private float _maxFPS;
    private float _avgFPS;
    private int _fpsHistoryCount;

    // Frame time history for mini-graph
    private readonly Queue<float> _frameTimeHistory = new();
    private const int HISTORY_SIZE = 60;

    // Memory tracking
    private long _lastGcMemory;
    private int _lastGcCount;
    private float _memoryDelta;

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();
        CurrentView = new Sync<EngineDebugView>(this, EngineDebugView.Performance);
        RefreshInterval = new Sync<float>(this, 0.1f); // 10Hz refresh
    }

    // ===== ROOT STEP =====

    protected override void OpenRoot(HelioUIBuilder ui)
    {
        // Header with FPS display
        ui.HorizontalLayout(spacing: 8f);
        _headerText = ui.Text("Engine Debug", fontSize: 28f, textColor: new color(0.9f, 0.95f, 1f));
        ui.FlexibleSpacer();
        _statusText = ui.Text("", fontSize: 18f, textColor: new color(0.5f, 1f, 0.5f));
        ui.EndLayout();

        ui.Spacer(12f);

        // View mode tabs
        ui.HorizontalLayout(spacing: 4f);
        BuildTabButton(ui, "Perf", EngineDebugView.Performance);
        BuildTabButton(ui, "World", EngineDebugView.World);
        BuildTabButton(ui, "Memory", EngineDebugView.Memory);
        BuildTabButton(ui, "Input", EngineDebugView.Input);
        BuildTabButton(ui, "Assets", EngineDebugView.Assets);
        BuildTabButton(ui, "Audio", EngineDebugView.Audio);
        ui.EndLayout();

        ui.Spacer(12f);

        // Frame budget display (always visible)
        ui.HorizontalLayout(spacing: 8f);
        ui.Text("Budget:", fontSize: 14f);
        _budgetText = ui.Text("0%", fontSize: 14f, textColor: new color(0.2f, 0.8f, 0.3f));
        ui.EndLayout();

        ui.Spacer(8f);

        // Main info panel
        ui.VerticalLayout(spacing: 4f, padding: new float4(12f, 12f, 12f, 12f));
        ui.Panel(new color(0.1f, 0.12f, 0.15f, 1f));

        _mainInfoText = ui.Text("Loading...", fontSize: 16f);

        ui.EndLayout();

        ui.Spacer(8f);

        // Details panel
        ui.VerticalLayout(spacing: 4f, padding: new float4(12f, 12f, 12f, 12f));
        ui.Panel(new color(0.08f, 0.08f, 0.1f, 1f));

        _detailsText = ui.Text("", fontSize: 14f, textColor: new color(0.75f, 0.75f, 0.8f));

        ui.EndLayout();

        ui.FlexibleSpacer();

        // Footer with actions
        ui.HorizontalLayout(spacing: 8f);
        ui.Button("Force GC", OnForceGC);
        ui.Button("Reset Stats", OnResetStats);
        ui.FlexibleSpacer();
        ui.Button("Close", Close);
        ui.EndLayout();

        // Initial refresh
        RefreshDisplay();
    }

    private void BuildTabButton(HelioUIBuilder ui, string label, EngineDebugView view)
    {
        ui.Button(label, () => SwitchView(view));
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Track frame time
        _lastFrameTime = delta;
        _frameTimeSmooth = _frameTimeSmooth * 0.95f + delta * 0.05f;

        // Track min/max frame time
        if (delta < _frameTimeMin) _frameTimeMin = delta;
        if (delta > _frameTimeMax) _frameTimeMax = delta;

        // FPS calculation
        _frameCount++;
        _fpsTimer += delta;

        if (_fpsTimer >= 1f)
        {
            _currentFPS = _frameCount / _fpsTimer;

            // Track min/max/avg FPS
            if (_currentFPS < _minFPS) _minFPS = _currentFPS;
            if (_currentFPS > _maxFPS) _maxFPS = _currentFPS;

            _fpsHistoryCount++;
            _avgFPS = (_avgFPS * (_fpsHistoryCount - 1) + _currentFPS) / _fpsHistoryCount;

            _frameCount = 0;
            _fpsTimer = 0f;
        }

        // Frame time history
        _frameTimeHistory.Enqueue(delta);
        while (_frameTimeHistory.Count > HISTORY_SIZE)
            _frameTimeHistory.Dequeue();

        // Update frame budget display
        if (_budgetText != null)
        {
            float targetFrameTime = 1f / 90f; // 90 FPS VR target
            float budget = _frameTimeSmooth / targetFrameTime * 100f;
            _budgetText.Content.Value = $"{budget:F0}%";

            // Color based on budget usage
            if (budget < 70f)
                _budgetText.Color.Value = new color(0.2f, 0.9f, 0.3f); // Green
            else if (budget < 90f)
                _budgetText.Color.Value = new color(0.9f, 0.8f, 0.2f); // Yellow
            else
                _budgetText.Color.Value = new color(0.9f, 0.3f, 0.2f); // Red
        }

        // Update status (always show FPS)
        if (_statusText != null)
        {
            string fpsColor = _currentFPS >= 72 ? "" : (_currentFPS >= 45 ? "" : "");
            _statusText.Content.Value = $"{_currentFPS:F0} FPS";

            // Color based on FPS
            if (_currentFPS >= 72)
                _statusText.Color.Value = new color(0.5f, 1f, 0.5f);
            else if (_currentFPS >= 45)
                _statusText.Color.Value = new color(1f, 0.9f, 0.3f);
            else
                _statusText.Color.Value = new color(1f, 0.4f, 0.3f);
        }

        // Memory tracking
        long currentGcMemory = GC.GetTotalMemory(false);
        _memoryDelta = (currentGcMemory - _lastGcMemory) / (1024f * 1024f); // MB/frame
        _lastGcMemory = currentGcMemory;

        // Periodic refresh
        _refreshTimer += delta;
        if (_refreshTimer >= RefreshInterval.Value)
        {
            _refreshTimer = 0f;
            RefreshDisplay();
        }
    }

    // ===== VIEW SWITCHING =====

    private void SwitchView(EngineDebugView view)
    {
        CurrentView.Value = view;
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        switch (CurrentView.Value)
        {
            case EngineDebugView.Performance:
                RefreshPerformanceView();
                break;
            case EngineDebugView.World:
                RefreshWorldView();
                break;
            case EngineDebugView.Memory:
                RefreshMemoryView();
                break;
            case EngineDebugView.Input:
                RefreshInputView();
                break;
            case EngineDebugView.Assets:
                RefreshAssetsView();
                break;
        }
    }

    // ===== VIEW IMPLEMENTATIONS =====

    private void RefreshPerformanceView()
    {
        if (_mainInfoText == null) return;

        float frameMs = _frameTimeSmooth * 1000f;
        float targetFps = 90f;
        float budget = frameMs / (1000f / targetFps) * 100f;

        string info = "Frame Timing:\n";
        info += $"  Current: {_currentFPS:F1} FPS ({frameMs:F2}ms)\n";
        info += $"  Min/Max: {_minFPS:F0} / {_maxFPS:F0} FPS\n";
        info += $"  Average: {_avgFPS:F1} FPS\n\n";

        info += $"Frame Budget (90 FPS target):\n";
        info += $"  Usage: {budget:F1}%\n";
        info += $"  Available: {(100f - budget):F1}%\n\n";

        info += $"Frame Time Range:\n";
        info += $"  Min: {_frameTimeMin * 1000f:F2}ms\n";
        info += $"  Max: {_frameTimeMax * 1000f:F2}ms\n";
        info += $"  Smooth: {frameMs:F2}ms";

        _mainInfoText.Content.Value = info;

        // Details - frame time graph as ASCII
        if (_detailsText != null)
        {
            string details = "Frame Time History (last 60 frames):\n";
            details += BuildFrameTimeGraph();
            details += "\n\nEngine Status:\n";

            var engine = Engine.Current;
            if (engine != null)
            {
                details += $"  Running: {!engine.IsShuttingDown}\n";
                details += $"  Worlds: {engine.WorldManager?.WorldCount ?? 0}\n";
                details += $"  Focus: {engine.WorldManager?.FocusedWorld?.Name ?? "None"}";
            }

            _detailsText.Content.Value = details;
        }
    }

    private string BuildFrameTimeGraph()
    {
        if (_frameTimeHistory.Count == 0) return "[No data]";

        var times = _frameTimeHistory.ToArray();
        float max = times.Max();
        float min = times.Min();
        float range = max - min;
        if (range < 0.0001f) range = 0.0001f;

        // Build simple ASCII graph
        const string bars = " _.-=*#@";
        string graph = "";
        int step = System.Math.Max(1, times.Length / 30);

        for (int i = 0; i < times.Length; i += step)
        {
            float normalized = (times[i] - min) / range;
            int barIndex = (int)(normalized * (bars.Length - 1));
            graph += bars[System.Math.Clamp(barIndex, 0, bars.Length - 1)];
        }

        return graph;
    }

    private void RefreshWorldView()
    {
        if (_mainInfoText == null) return;

        var world = World;
        string info = "World Information:\n\n";

        if (world != null)
        {
            info += $"Name: {world.Name}\n";
            info += $"Authority: {(world.IsAuthority ? "Yes" : "No")}\n";
            info += $"Focused: {(world.IsFocused ? "Yes" : "No")}\n\n";

            info += $"Users: {world.UserCount}\n";
            info += $"Local User: {world.LocalUser?.UserName.Value ?? "None"}\n\n";

            int slotCount = CountSlots(world.RootSlot);
            int componentCount = CountComponents(world.RootSlot);

            info += $"Hierarchy:\n";
            info += $"  Total Slots: {slotCount:N0}\n";
            info += $"  Total Components: {componentCount:N0}\n";
            info += $"  Root Children: {world.RootSlot?.ChildCount ?? 0}";
        }
        else
        {
            info += "World: Not Available";
        }

        _mainInfoText.Content.Value = info;

        if (_detailsText != null && world != null)
        {
            string details = "Timing:\n";
            details += $"  Last Delta: {world.LastDelta * 1000f:F2}ms\n";
            details += $"  Session Time: {FormatDuration((float)world.TotalTime)}\n\n";

            details += "Component Distribution:\n";
            var componentTypes = new Dictionary<string, int>();
            CountComponentTypes(world.RootSlot, componentTypes);

            var topTypes = componentTypes.OrderByDescending(x => x.Value).Take(5);
            foreach (var kv in topTypes)
            {
                details += $"  {kv.Key}: {kv.Value}\n";
            }

            _detailsText.Content.Value = details;
        }
    }

    private void RefreshMemoryView()
    {
        if (_mainInfoText == null) return;

        var process = Process.GetCurrentProcess();
        long workingSet = process.WorkingSet64;
        long privateMemory = process.PrivateMemorySize64;
        long gcTotal = GC.GetTotalMemory(false);

        string info = "Memory Usage:\n\n";
        info += $"Process:\n";
        info += $"  Working Set: {FormatBytes(workingSet)}\n";
        info += $"  Private: {FormatBytes(privateMemory)}\n";
        info += $"  Peak Working: {FormatBytes(process.PeakWorkingSet64)}\n\n";

        info += $"Managed Heap:\n";
        info += $"  Current: {FormatBytes(gcTotal)}\n";
        info += $"  Delta: {_memoryDelta:+0.00;-0.00;0.00} MB/frame";

        _mainInfoText.Content.Value = info;

        if (_detailsText != null)
        {
            string details = "Garbage Collection:\n";
            details += $"  Gen 0: {GC.CollectionCount(0)} collections\n";
            details += $"  Gen 1: {GC.CollectionCount(1)} collections\n";
            details += $"  Gen 2: {GC.CollectionCount(2)} collections\n\n";

            details += "Process Info:\n";
            details += $"  PID: {process.Id}\n";
            details += $"  Threads: {process.Threads.Count}\n";
            details += $"  Handles: {process.HandleCount}\n\n";

            details += "System:\n";
            details += $"  Processors: {Environment.ProcessorCount}\n";
            details += $"  64-bit: {Environment.Is64BitProcess}";

            _detailsText.Content.Value = details;
        }
    }

    private void RefreshInputView()
    {
        if (_mainInfoText == null) return;

        var engine = Engine.Current;
        var inputInterface = engine?.InputInterface;

        string info = "Input Devices:\n\n";

        if (inputInterface != null)
        {
            var head = inputInterface.HeadDevice;
            if (head != null)
            {
                string tracking = head.IsTracked ? "Tracking" : "Lost";
                info += $"Head ({tracking}):\n";
                info += $"  {head.DeviceName}\n";
                if (head.BatteryLevel >= 0)
                    info += $"  Battery: {head.BatteryLevel:P0}\n";
                info += "\n";
            }

            var leftHand = inputInterface.LeftController;
            if (leftHand != null)
            {
                string tracking = leftHand.IsTracked ? "Tracking" : "Lost";
                info += $"Left Controller ({tracking}):\n";
                info += $"  {leftHand.DeviceName}\n";
                if (leftHand.BatteryLevel >= 0)
                    info += $"  Battery: {leftHand.BatteryLevel:P0}\n";
                info += "\n";
            }

            var rightHand = inputInterface.RightController;
            if (rightHand != null)
            {
                string tracking = rightHand.IsTracked ? "Tracking" : "Lost";
                info += $"Right Controller ({tracking}):\n";
                info += $"  {rightHand.DeviceName}\n";
                if (rightHand.BatteryLevel >= 0)
                    info += $"  Battery: {rightHand.BatteryLevel:P0}";
            }
        }
        else
        {
            info += "Input Interface: Not Available";
        }

        _mainInfoText.Content.Value = info;

        if (_detailsText != null && inputInterface != null)
        {
            string details = "Controller State:\n\n";

            var left = inputInterface.LeftController;
            var right = inputInterface.RightController;

            if (left != null)
            {
                details += "Left:\n";
                details += $"  Trigger: {left.Trigger:F2} | Grip: {left.Grip:F2}\n";
                details += $"  Stick: ({left.ThumbstickPosition.X:F2}, {left.ThumbstickPosition.Y:F2})\n\n";
            }

            if (right != null)
            {
                details += "Right:\n";
                details += $"  Trigger: {right.Trigger:F2} | Grip: {right.Grip:F2}\n";
                details += $"  Stick: ({right.ThumbstickPosition.X:F2}, {right.ThumbstickPosition.Y:F2})";
            }

            _detailsText.Content.Value = details;
        }
    }

    private void RefreshAssetsView()
    {
        if (_mainInfoText == null) return;

        string info = "Asset Management:\n\n";

        var engine = Engine.Current;
        if (engine != null)
        {
            var assetManager = engine.AssetManager;
            if (assetManager != null)
            {
                info += $"Loaded Assets: {assetManager.CurrentAssetCount}\n";
                info += $"Total Loaded: {assetManager.TotalAssetsLoaded}\n";
                info += $"Cache Size: {FormatBytes(assetManager.CurrentCacheSize)}\n";
                info += $"Max Cache: {FormatBytes(assetManager.MaxCacheSize)}";
            }
            else
            {
                info += "Asset Manager: Not Available";
            }
        }

        _mainInfoText.Content.Value = info;

        if (_detailsText != null)
        {
            string details = "Asset Loading:\n";
            details += "  Status: Operational\n\n";

            details += "Memory Budget:\n";
            details += "  Textures: N/A\n";
            details += "  Meshes: N/A\n";
            details += "  Audio: N/A";

            _detailsText.Content.Value = details;
        }
    }


    // ===== ACTIONS =====

    private void OnForceGC()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        RefreshDisplay();
    }

    private void OnResetStats()
    {
        _frameTimeMin = float.MaxValue;
        _frameTimeMax = 0f;
        _minFPS = float.MaxValue;
        _maxFPS = 0f;
        _avgFPS = 0f;
        _fpsHistoryCount = 0;
        _frameTimeHistory.Clear();
        RefreshDisplay();
    }

    // ===== UTILITY =====

    private int CountSlots(Slot slot)
    {
        if (slot == null) return 0;
        int count = 1;
        foreach (var child in slot.Children)
            count += CountSlots(child);
        return count;
    }

    private int CountComponents(Slot slot)
    {
        if (slot == null) return 0;
        int count = slot.ComponentCount;
        foreach (var child in slot.Children)
            count += CountComponents(child);
        return count;
    }

    private void CountComponentTypes(Slot slot, Dictionary<string, int> counts)
    {
        if (slot == null) return;

        foreach (var comp in slot.Components)
        {
            string typeName = comp.GetType().Name;
            if (!counts.ContainsKey(typeName))
                counts[typeName] = 0;
            counts[typeName]++;
        }

        foreach (var child in slot.Children)
            CountComponentTypes(child, counts);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
        return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
    }

    private static string FormatDuration(float seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
