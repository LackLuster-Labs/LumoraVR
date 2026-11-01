// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;

namespace Lumora.Fluids.Hooks;

// The surface and residue kernels on one device: shaders from the imported SPIR-V (no runtime compiler, which is
// the safer road on every platform) and one pipeline each. Made once per device and kept; a pass only borrows
// them. -xlinka
public sealed class FluidSurfaceKernels : IDisposable
{
    public const string Folder = "res://Modules/Fluids/Compute/";

    private static FluidSurfaceKernels? _shared;

    private FluidSurfaceKernels(RenderingDevice device)
    {
        Device = device;
    }

    public RenderingDevice Device { get; }

    public Rid ShapesShader { get; private set; }
    public Rid BricksShader { get; private set; }
    public Rid SplatShader { get; private set; }
    public Rid NetsShader { get; private set; }
    public Rid SmoothShader { get; private set; }
    public Rid TubesShader { get; private set; }
    public Rid SprayShader { get; private set; }
    public Rid ResidueShader { get; private set; }

    public Rid Shapes { get; private set; }
    public Rid Bricks { get; private set; }
    public Rid Splat { get; private set; }
    public Rid Nets { get; private set; }
    public Rid Smooth { get; private set; }
    public Rid Tubes { get; private set; }
    public Rid Spray { get; private set; }
    public Rid Residue { get; private set; }

    // The kernels for this device, loaded on first ask; null with the reason when one fails to load.
    public static FluidSurfaceKernels? For(RenderingDevice device, out string error)
    {
        error = string.Empty;
        if (_shared != null && ReferenceEquals(_shared.Device, device))
            return _shared;
        var kernels = new FluidSurfaceKernels(device);
        if (!kernels.LoadAll(out error))
        {
            kernels.Dispose();
            return null;
        }
        _shared?.Dispose();
        _shared = kernels;
        return kernels;
    }

    private bool LoadAll(out string error)
    {
        error = string.Empty;
        ShapesShader = Load("FluidSurfaceShapes", ref error);
        BricksShader = Load("FluidSurfaceBricks", ref error);
        SplatShader = Load("FluidSurfaceSplat", ref error);
        NetsShader = Load("FluidSurfaceNets", ref error);
        SmoothShader = Load("FluidSurfaceSmooth", ref error);
        TubesShader = Load("FluidSurfaceTubes", ref error);
        SprayShader = Load("FluidSurfaceSpray", ref error);
        ResidueShader = Load("FluidResidue", ref error);
        if (error.Length > 0)
            return false;
        Shapes = Device.ComputePipelineCreate(ShapesShader);
        Bricks = Device.ComputePipelineCreate(BricksShader);
        Splat = Device.ComputePipelineCreate(SplatShader);
        Nets = Device.ComputePipelineCreate(NetsShader);
        Smooth = Device.ComputePipelineCreate(SmoothShader);
        Tubes = Device.ComputePipelineCreate(TubesShader);
        Spray = Device.ComputePipelineCreate(SprayShader);
        Residue = Device.ComputePipelineCreate(ResidueShader);
        return true;
    }

    private Rid Load(string name, ref string error)
    {
        string path = Folder + name + ".glsl";
        var file = ResourceLoader.Exists(path) ? GD.Load<RDShaderFile>(path) : null;
        if (file == null)
        {
            error += name + ": not imported; ";
            return default;
        }
        var spirv = file.GetSpirV();
        string compile = spirv.CompileErrorCompute ?? string.Empty;
        if (compile.Length > 0)
        {
            error += name + ": " + compile + "; ";
            return default;
        }
        var shader = Device.ShaderCreateFromSpirV(spirv, name);
        if (!shader.IsValid)
            error += name + ": shader create failed; ";
        return shader;
    }

    public void Dispose()
    {
        foreach (var rid in new[] { Shapes, Bricks, Splat, Nets, Smooth, Tubes, Spray, Residue, ShapesShader, BricksShader, SplatShader, NetsShader, SmoothShader, TubesShader, SprayShader, ResidueShader })
        {
            if (rid.IsValid)
                Device.FreeRid(rid);
        }
        if (ReferenceEquals(_shared, this))
            _shared = null;
    }
}
