// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Lumora.Simulation.SoftBody;

namespace Lumora.Godot.Hooks;

public static class ComputePhysics
{
    internal static ComputePhysicsDevice? Device;

    public static void Install()
    {
        if (Device != null || DisplayServer.GetName() == "headless" || RenderingServer.GetRenderingDevice() == null)
            return;
        var device = new ComputePhysicsDevice();
        Device = device;
        ClothCompute.Create = () => new GpuClothCompute(device);
    }

    public static void Shutdown()
    {
        ClothCompute.Create = null;
        var device = Device;
        Device = null;
        device?.Dispose();
    }
}

internal sealed class ComputePhysicsDevice : IDisposable
{
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly Thread _thread;
    private readonly Dictionary<string, (Rid Shader, Rid Pipeline)> _kernels = new();
    private readonly HashSet<Rid> _resources = new();
    private readonly HashSet<Rid> _uniformSets = new();
    private RenderingDevice? _device;
    internal RenderingDevice Rd => _device ??= RenderingServer.CreateLocalRenderingDevice()
        ?? throw new InvalidOperationException("compute device unavailable");

    public ComputePhysicsDevice()
    {
        _thread = new Thread(Work) { IsBackground = true, Name = "Physics compute" };
        _thread.Start();
    }

    public T Run<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _jobs.Add(() =>
            {
                try
                {
                    result.SetResult(action());
                }
                catch (Exception e) { result.SetException(e); }
            });
        }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(ComputePhysicsDevice)); }
        return result.Task.GetAwaiter().GetResult();
    }

    private void Work()
    {
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable()) job();
        }
        finally
        {
            if (_device != null)
            {
                foreach (var rid in _uniformSets)
                    if (_device.UniformSetIsValid(rid)) _device.FreeRid(rid);
                foreach (var rid in _resources)
                    if (!_uniformSets.Contains(rid)) _device.FreeRid(rid);
                foreach (var kernel in _kernels.Values) { _device.FreeRid(kernel.Pipeline); _device.FreeRid(kernel.Shader); }
                _device.Dispose();
            }
        }
    }

    public (Rid Shader, Rid Pipeline) Kernel(string name)
    {
        if (_kernels.TryGetValue(name, out var kernel)) return kernel;
        using var stream = typeof(ComputePhysicsDevice).Assembly.GetManifestResourceStream("PhysicsCompute." + name + ".glsl")
            ?? throw new InvalidOperationException("missing compute kernel " + name);
        using var reader = new StreamReader(stream);
        using var source = new RDShaderSource { SourceCompute = reader.ReadToEnd() };
        using var spirv = Rd.ShaderCompileSpirVFromSource(source);
        string error = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (error.Length != 0) throw new InvalidOperationException(error);
        var shader = Rd.ShaderCreateFromSpirV(spirv);
        var pipeline = Rd.ComputePipelineCreate(shader);
        if (!pipeline.IsValid)
        {
            if (shader.IsValid) Rd.FreeRid(shader);
            throw new InvalidOperationException("compute pipeline creation failed");
        }
        _kernels[name] = kernel = (shader, pipeline);
        return kernel;
    }

    public Rid Buffer(int bytes, byte[]? data = null)
    {
        var rid = Rd.StorageBufferCreate((uint)System.Math.Max(16, bytes), data ?? Array.Empty<byte>());
        if (!rid.IsValid) throw new InvalidOperationException("physics buffer allocation failed");
        _resources.Add(rid);
        return rid;
    }

    public Rid Uniforms(Rid shader, params Rid[] buffers)
    {
        var uniforms = new global::Godot.Collections.Array<RDUniform>();
        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                var uniform = new RDUniform { Binding = i, UniformType = RenderingDevice.UniformType.StorageBuffer };
                uniform.AddId(buffers[i]);
                uniforms.Add(uniform);
            }
            var rid = Rd.UniformSetCreate(uniforms, shader, 0);
            if (!rid.IsValid) throw new InvalidOperationException("physics bindings creation failed");
            _resources.Add(rid);
            _uniformSets.Add(rid);
            return rid;
        }
        finally { foreach (var uniform in uniforms) uniform.Dispose(); }
    }

    public void Free(ref Rid rid)
    {
        if (!rid.IsValid) return;
        _uniformSets.Remove(rid);
        if (_resources.Remove(rid)) Rd.FreeRid(rid);
        rid = default;
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _thread.Join();
    }
}
