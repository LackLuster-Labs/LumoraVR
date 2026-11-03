// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;

namespace Lumora.Fluids.Hooks;

// The solver's compute pipelines on one device, from the imported SPIR-V (never runtime glslang). Shared by every
// world on that device. In the game they are made one per frame during the first fluid world's load hold, never in
// a burst mid-frame; a test on its own device makes them all at once. -xlinka
public sealed class FluidGpuPipelines
{
    public const int Commands = 0;
    public const int Skin = 1;
    public const int SortCount = 2;
    public const int SortScan = 3;
    public const int SortScatter = 4;
    public const int SortCell = 5;
    public const int PredictCollide = 6;
    public const int Density = 7;
    public const int Correct = 8;
    public const int Update = 9;
    public const int Viscous = 10;
    public const int FrameEndA = 11;
    public const int FrameEndB = 12;
    public const int FrameEndC = 13;
    public const int FrameEndD = 14;
    public const int Summary = 15;
    public const int Count = 16;

    public const string Folder = "res://Modules/Fluids/Compute/";

    public static readonly string[] Names =
    {
        "FluidCommands", "FluidSkin", "FluidSortCount", "FluidSortScan", "FluidSortScatter", "FluidSortCell",
        "FluidPredictCollide", "FluidDensity", "FluidCorrect", "FluidUpdate", "FluidViscous",
        "FluidFrameEndA", "FluidFrameEndB", "FluidFrameEndC", "FluidFrameEndD", "FluidSummary",
    };

    private static readonly Dictionary<ulong, FluidGpuPipelines> PerDevice = new();

    private readonly Rid[] _shaders = new Rid[Count];
    private readonly Rid[] _pipelines = new Rid[Count];
    private int _made;

    private FluidGpuPipelines(RenderingDevice device)
    {
        Device = device;
    }

    public RenderingDevice Device { get; }

    public bool Ready => _made >= Count && Error.Length == 0;

    public bool Failed => Error.Length > 0;

    public string Error { get; private set; } = string.Empty;

    public int Made => _made;

    public static FluidGpuPipelines For(RenderingDevice device)
    {
        ulong key = device.GetInstanceId();
        if (!PerDevice.TryGetValue(key, out var pipelines))
        {
            pipelines = new FluidGpuPipelines(device);
            PerDevice[key] = pipelines;
        }
        return pipelines;
    }

    // A test frees its local device after a check; its pipelines go with it.
    public static void Forget(RenderingDevice device)
    {
        ulong key = device.GetInstanceId();
        if (PerDevice.Remove(key, out var pipelines))
            pipelines.Free();
    }

    public Rid Shader(int kernel) => _shaders[kernel];

    public Rid Pipeline(int kernel) => _pipelines[kernel];

    // One kernel per call; true once all are made.
    public bool CreateNext()
    {
        if (Ready || Failed)
            return Ready;
        int k = _made;
        string path = Folder + Names[k] + ".glsl";
        var file = GD.Load<RDShaderFile>(path);
        if (file == null)
        {
            Error = "not imported: " + path;
            return false;
        }
        var spirv = file.GetSpirV();
        string compile = spirv.CompileErrorCompute ?? string.Empty;
        if (compile.Length > 0)
        {
            Error = Names[k] + ": " + compile.Trim();
            return false;
        }
        var shader = Device.ShaderCreateFromSpirV(spirv, Names[k]);
        if (!shader.IsValid)
        {
            Error = Names[k] + ": the device refused the shader";
            return false;
        }
        var pipeline = Device.ComputePipelineCreate(shader);
        if (!pipeline.IsValid)
        {
            Device.FreeRid(shader);
            Error = Names[k] + ": the device refused the pipeline";
            return false;
        }
        _shaders[k] = shader;
        _pipelines[k] = pipeline;
        _made++;
        return Ready;
    }

    public bool CreateAll()
    {
        while (!Ready && !Failed)
            CreateNext();
        return Ready;
    }

    private void Free()
    {
        for (int k = 0; k < Count; k++)
        {
            if (_pipelines[k].IsValid)
                Device.FreeRid(_pipelines[k]);
            if (_shaders[k].IsValid)
                Device.FreeRid(_shaders[k]);
            _pipelines[k] = default;
            _shaders[k] = default;
        }
        _made = 0;
    }
}
