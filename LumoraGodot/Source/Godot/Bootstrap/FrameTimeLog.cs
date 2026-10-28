// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// --Lumora-FrameLog=<path>   [--Lumora-FrameLogSeconds=<n>]
//
// One CSV row per frame of where the wall time went, with no console in the loop. The console's
// frame capture samples one frame every quarter second and subtracts THIS frame's engine time from
// a delta that describes the PREVIOUS iteration, so each sample is off by a frame and it only ever
// sees four frames a second. This writes every frame, and a row is everything between two of its
// calls at the end of the host's _Process: the rest of the last frame (the scene tree, the render
// submit, the present, any vsync or frame-limiter sleep), this frame's physics ticks, then this
// frame's own update. Wall time, measured here: Godot's process delta never says more than
// max_physics_steps_per_frame physics steps, 133 ms at the defaults, so a 600 ms stall read as 133. -xlinka
//
//   delta_ms       wall time since the previous row
//   ours_ms        network + engine + tail measured in this _Process
//   godot_ms       delta_ms - ours_ms, everything Godot did between our two updates
//   render_cpu_ms  main viewport render submit, CPU side
//   render_gpu_ms  main viewport GPU time
//   setup_cpu_ms   RenderingServer frame setup
//   physics_ticks  physics steps that ran inside this delta
//   physics_ms     last physics step, main thread
//   max_fps        Engine.MaxFps in force, 0 = uncapped
//   vsync          window vsync mode in force
//   focused        window has focus
//
// Seconds > 0 quits the app once that much has been logged, so a measurement run needs no hand on
// the window. Nothing here runs unless the flag is present. -xlinka
//
// Beside it, <path>-textures.csv: every texture load's stages as they happen (asked for, its bytes
// gathered, waiting for and holding a delivery turn, decoded, preview, compressed or read from the GPU
// cache, queued for the main thread, built, done) and the variant ladders made behind them, on the same
// clock as time_s. A world that is slow to show its textures says which stage the time went to. -xlinka
//
//   texture  one number per texture load
//   stage    the stage reached
//   time_s   on the frame rows' clock
//   thread   managed thread id
//   detail   address, size, rung or format, depending on the stage
public sealed class FrameTimeLog : IDisposable
{
    public const string PathFlag = "--Lumora-FrameLog";
    public const string SecondsFlag = "--Lumora-FrameLogSeconds";
    private const int FlushEveryFrames = 120;

    private readonly StreamWriter _writer;
    private readonly StreamWriter? _textures;
    private long _startStamp;
    private readonly double _quitAfterSeconds;
    private readonly StringBuilder _line = new(256);
    private long _prevStamp;
    private ulong _prevPhysicsFrames;
    private double _elapsed;
    private long _frame;
    private bool _measuring;
    private bool _quitRequested;

    public static FrameTimeLog? FromCommandLine()
    {
        var path = LaunchArgs.ReadValue(PathFlag);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        double seconds = 0;
        var secondsText = LaunchArgs.ReadValue(SecondsFlag);
        if (!string.IsNullOrWhiteSpace(secondsText))
            double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);

        try
        {
            return new FrameTimeLog(path, seconds);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"FrameTimeLog: cannot open '{path}': {ex.Message}");
            return null;
        }
    }

    private FrameTimeLog(string path, double quitAfterSeconds)
    {
        bool virtualPath = path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("res://", StringComparison.OrdinalIgnoreCase);
        var resolved = virtualPath ? ProjectSettings.GlobalizePath(path) : path;
        var directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(resolved, false, new UTF8Encoding(false), 1 << 16);
        _writer.WriteLine("frame,time_s,delta_ms,ours_ms,godot_ms,render_cpu_ms,render_gpu_ms,setup_cpu_ms,physics_ticks,physics_ms,max_fps,vsync,focused");
        _quitAfterSeconds = quitAfterSeconds;
        _prevPhysicsFrames = global::Godot.Engine.GetPhysicsFrames();

        var texturesPath = Path.Combine(directory ?? string.Empty, Path.GetFileNameWithoutExtension(resolved) + "-textures.csv");
        try
        {
            _textures = new StreamWriter(texturesPath, false, new UTF8Encoding(false), 1 << 16);
            _textures.WriteLine("texture,stage,time_s,thread,detail");
            Lumora.Core.Assets.TextureLoadTrace.Enabled = true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"FrameTimeLog: cannot open '{texturesPath}': {ex.Message}");
        }
        LumoraLogger.Log($"FrameTimeLog: writing every frame to {resolved}"
            + (quitAfterSeconds > 0 ? $", quitting after {quitAfterSeconds:0.#} s" : string.Empty));
    }

    // Called at the end of the host's _Process with this frame's measured pieces.
    public void Record(Node host, double delta, double networkMs, double engineMs, double tailMs)
    {
        var rid = host.GetViewport()?.GetViewportRid() ?? default;
        double renderCpu = 0, renderGpu = 0;
        if (rid.IsValid)
        {
            if (!_measuring)
            {
                RenderingServer.ViewportSetMeasureRenderTime(rid, true);
                _measuring = true;
            }
            renderCpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
            renderGpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
        }

        ulong physicsFrames = global::Godot.Engine.GetPhysicsFrames();
        ulong ticks = physicsFrames - _prevPhysicsFrames;
        _prevPhysicsFrames = physicsFrames;

        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_startStamp == 0)
            _startStamp = stamp - (long)(delta * System.Diagnostics.Stopwatch.Frequency);
        double deltaMs = _prevStamp != 0 ? (stamp - _prevStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : delta * 1000.0;
        _prevStamp = stamp;
        double oursMs = networkMs + engineMs + tailMs;
        double godotMs = deltaMs - oursMs;
        _elapsed += deltaMs / 1000.0;

        bool focused;
        try { focused = DisplayServer.WindowIsFocused(); }
        catch { focused = true; }

        var c = CultureInfo.InvariantCulture;
        _line.Clear();
        _line.Append(_frame).Append(',')
            .Append(_elapsed.ToString("0.000", c)).Append(',')
            .Append(deltaMs.ToString("0.000", c)).Append(',')
            .Append(oursMs.ToString("0.000", c)).Append(',')
            .Append(godotMs.ToString("0.000", c)).Append(',')
            .Append(renderCpu.ToString("0.000", c)).Append(',')
            .Append(renderGpu.ToString("0.000", c)).Append(',')
            .Append(RenderingServer.GetFrameSetupTimeCpu().ToString("0.000", c)).Append(',')
            .Append(ticks).Append(',')
            .Append((Performance.Singleton.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0).ToString("0.000", c)).Append(',')
            .Append(global::Godot.Engine.MaxFps).Append(',')
            .Append(DisplayServer.WindowGetVsyncMode()).Append(',')
            .Append(focused ? 1 : 0);
        _writer.WriteLine(_line.ToString());

        _frame++;
        WriteTextureStages(c);
        if (_frame % FlushEveryFrames == 0)
        {
            _writer.Flush();
            _textures?.Flush();
        }

        if (_quitAfterSeconds > 0 && !_quitRequested && _elapsed >= _quitAfterSeconds)
        {
            _quitRequested = true;
            LumoraLogger.Log($"FrameTimeLog: {_frame} frames logged, quitting");
            host.GetTree()?.Quit();
        }
    }

    private void WriteTextureStages(CultureInfo c)
    {
        if (_textures == null)
            return;
        foreach (var stamp in Lumora.Core.Assets.TextureLoadTrace.Drain())
        {
            _line.Clear();
            _line.Append(stamp.Id).Append(',')
                .Append(stamp.Stage).Append(',')
                .Append(((stamp.Ticks - _startStamp) / (double)System.Diagnostics.Stopwatch.Frequency).ToString("0.0000", c)).Append(',')
                .Append(stamp.Thread).Append(',')
                .Append((stamp.Detail ?? string.Empty).Replace(',', ';'));
            _textures.WriteLine(_line.ToString());
        }
    }

    public void Dispose()
    {
        try
        {
            Lumora.Core.Assets.TextureLoadTrace.Enabled = false;
            _writer.Flush();
            _writer.Dispose();
            if (_textures != null)
            {
                WriteTextureStages(CultureInfo.InvariantCulture);
                _textures.Dispose();
            }
        }
        catch
        {
            // The file is a diagnostic; a failed final flush is not worth a shutdown error.
        }
    }
}
