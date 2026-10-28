// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime.InteropServices;
using Godot;
using Lumora.Simulation.SoftBody;

namespace Lumora.Godot.Hooks;

internal sealed class GpuClothCompute : IClothCompute
{
    private readonly ComputePhysicsDevice _device;
    private Rid _points, _edges, _triangles, _adjacent, _wind, _set;
    private ClothComputeData? _topology;
    private bool _failed, _disposed;
    public long AllocatedBytes { get; private set; }
    public string Status { get; private set; } = "GPU";

    public GpuClothCompute(ComputePhysicsDevice device) => _device = device;

    public bool Step(ClothComputeData data, in ClothComputeStep step)
    {
        if (_failed || _disposed) return false;
        var parameters = step;
        try { return _device.Run(() => Execute(data, parameters)); }
        catch (Exception e)
        {
            _failed = true;
            try { _device.Run(() => { Release(); return true; }); }
            catch (ObjectDisposedException) { }
            Status = "CPU: " + e.Message;
            Lumora.Core.Logging.Logger.Warn("Cloth compute fallback: " + e.Message);
            return false;
        }
    }

    private bool Execute(ClothComputeData data, in ClothComputeStep step)
    {
        var rd = _device.Rd;
        var kernel = _device.Kernel("Cloth");
        var points = MemoryMarshal.AsBytes(data.Points.AsSpan()).ToArray();
        var edges = MemoryMarshal.AsBytes(data.Edges.AsSpan()).ToArray();
        if (!ReferenceEquals(data, _topology))
        {
            Release();
            _points = _device.Buffer(points.Length);
            _edges = _device.Buffer(edges.Length);
            var triangles = MemoryMarshal.AsBytes(data.Triangles.AsSpan()).ToArray();
            var adjacent = MemoryMarshal.AsBytes(data.Adjacent.AsSpan()).ToArray();
            _triangles = _device.Buffer(triangles.Length, triangles.Length >= 16 ? triangles : null);
            _adjacent = _device.Buffer(adjacent.Length, adjacent.Length >= 16 ? adjacent : null);
            if (triangles.Length is > 0 and < 16) rd.BufferUpdate(_triangles, 0, (uint)triangles.Length, triangles);
            if (adjacent.Length is > 0 and < 16) rd.BufferUpdate(_adjacent, 0, (uint)adjacent.Length, adjacent);
            _wind = _device.Buffer(data.Points.Length * 16);
            _set = _device.Uniforms(kernel.Shader, _points, _edges, _triangles, _adjacent, _wind);
            _topology = data;
            AllocatedBytes = points.Length + System.Math.Max(16, edges.Length) + System.Math.Max(16, triangles.Length)
                + System.Math.Max(16, adjacent.Length) + data.Points.Length * 16L;
        }
        rd.BufferUpdate(_points, 0, (uint)points.Length, points);
        if (edges.Length > 0) rd.BufferUpdate(_edges, 0, (uint)edges.Length, edges);
        var pc = new float[] { 0, 0, data.Points.Length, step.Dt, step.PreviousDt, step.Damping, step.Ratio, step.MaxMove,
            step.Gravity.x, step.Gravity.y, step.Gravity.z, step.Moving ? 1 : 0, step.Wind.x, step.Wind.y, step.Wind.z, 0 };
        var push = new byte[64];
        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, kernel.Pipeline);
        rd.ComputeListBindUniformSet(list, _set, 0);
        void Dispatch(int stage, int start, int count, float stiffness = 0)
        {
            if (count == 0) return;
            pc[0] = stage; pc[1] = start; pc[2] = count; pc[15] = stiffness;
            System.Buffer.BlockCopy(pc, 0, push, 0, push.Length);
            rd.ComputeListSetPushConstant(list, push, 64);
            rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
            rd.ComputeListAddBarrier(list);
        }
        Dispatch(0, 0, data.Points.Length);
        Dispatch(1, 0, data.Points.Length);
        for (int iteration = 0; iteration < step.Iterations; iteration++)
            for (int group = 0; group < data.GroupStarts.Length - 1; group++)
            {
                float stiffness = group < data.StretchGroups ? step.Stretch : step.Bend;
                if (stiffness > 0f) Dispatch(2, data.GroupStarts[group], data.GroupStarts[group + 1] - data.GroupStarts[group], stiffness);
            }
        Dispatch(3, 0, data.Points.Length);
        rd.ComputeListEnd();
        rd.Submit();
        rd.Sync();
        var output = rd.BufferGetData(_points);
        var result = MemoryMarshal.Cast<byte, ClothPoint>(output.AsSpan());
        for (int i = 0; i < data.Points.Length; i++)
            if (!Finite(result[i].Position) || !Finite(result[i].Previous))
                throw new InvalidOperationException("non-finite cloth result");
        result[..data.Points.Length].CopyTo(data.Points);
        return true;
    }

    private static bool Finite(Lumora.Core.Math.float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private void Release()
    {
        _device.Free(ref _set);
        _device.Free(ref _wind);
        _device.Free(ref _adjacent);
        _device.Free(ref _triangles);
        _device.Free(ref _edges);
        _device.Free(ref _points);
        _topology = null;
        AllocatedBytes = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _device.Run(() => { Release(); return true; }); }
        catch (ObjectDisposedException) { }
    }
}
