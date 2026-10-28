// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Lumora.Core.Assets;
using Logger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// One local device, owned entirely by a background thread. Readback waits never stall the world or
// rendering thread. Serialize jobs to bound GPU memory and avoid competing texture imports in VR.
public sealed class GpuTextureVariants : ITextureVariantAccelerator, IDisposable
{
    private sealed record Job(byte[] Pixels, int SourceWidth, int SourceHeight, int Width, int Height, bool Mipmaps,
        CancellationToken Cancellation, TaskCompletionSource<byte[][]?> Completion);
    private readonly BlockingCollection<Job> _jobs = new(1);
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Thread _thread;
    private volatile bool _stopped;
    private volatile bool _failed;
    private RenderingDevice? _device;
    private Rid _shader, _pipeline, _source;
    private WeakReference<byte[]>? _sourceBytes;
    private static GpuTextureVariants? _installed;
    public long CompletedJobs => Interlocked.Read(ref _completed);
    private long _completed;

    public static void Install()
    {
        if (_installed != null || RenderingServer.GetRenderingDevice() == null)
            return;
        // Desktop first: mobile compute driver support needs separate hardware validation.
        if (OS.GetName() is not ("Windows" or "Linux" or "macOS"))
            return;
        TextureVariantStore.Accelerator = _installed = new GpuTextureVariants();
    }

    public static void Shutdown()
    {
        var worker = _installed;
        _installed = null;
        if (ReferenceEquals(TextureVariantStore.Accelerator, worker))
            TextureVariantStore.Accelerator = null;
        worker?.Dispose();
    }

    public GpuTextureVariants()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Texture variant GPU" };
        _thread.Start();
    }

    public async Task<byte[][]?> BuildAsync(byte[] rgba, int sourceWidth, int sourceHeight, int width, int height,
        bool mipmaps, CancellationToken cancellationToken = default)
    {
        // Tiny images cost more to transfer than to filter. Very large buffers use the CPU fallback.
        if (_stopped || _failed || (long)sourceWidth * sourceHeight < 512 * 512
            || sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > 16384 || sourceHeight > 16384 || width <= 0 || height <= 0
            || width > sourceWidth || height > sourceHeight || rgba.LongLength != (long)sourceWidth * sourceHeight * 4
            || rgba.LongLength > 128 * 1024 * 1024 || (!mipmaps && width == sourceWidth && height == sourceHeight))
            return null;
        long samples = ((sourceWidth + (long)width - 1) / width + 2) * ((sourceHeight + (long)height - 1) / height + 2);
        if (samples * 255 > uint.MaxValue) return null;
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped || _failed) return null;
            var completion = new TaskCompletionSource<byte[][]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { _jobs.Add(new Job(rgba, sourceWidth, sourceHeight, width, height, mipmaps, cancellationToken, completion), cancellationToken); }
            catch (InvalidOperationException) { return null; }
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _turn.Release(); }
    }

    private void Run()
    {
        try
        {
            // Made straight away, while the game is still starting. Making a local device holds the main
            // thread too, 100 to 120 ms measured, and made on the first job it landed in the middle of
            // play, on the first picture pasted or the first new texture a world brought. Its shader
            // compile and every job after it do not. -xlinka
            try
            {
                EnsureDevice();
            }
            catch (Exception ex)
            {
                _failed = true;
                Logger.Warn($"GPU texture variants unavailable; using CPU: {ex.Message}");
            }
            while (!_jobs.IsCompleted)
            {
                if (!_jobs.TryTake(out var job, 2000))
                {
                    ReleaseSource(); // Do not retain an imported avatar's texture on the GPU while idle.
                    continue;
                }
                try
                {
                    job.Cancellation.ThrowIfCancellationRequested();
                    var result = _failed ? null : Execute(job);
                    job.Cancellation.ThrowIfCancellationRequested();
                    if (result != null) Interlocked.Increment(ref _completed);
                    job.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException) { job.Completion.TrySetCanceled(job.Cancellation); }
                catch (Exception ex)
                {
                    _failed = true;
                    Logger.Warn($"GPU texture variants unavailable; using CPU: {ex.Message}");
                    job.Completion.TrySetResult(null);
                }
            }
        }
        finally
        {
            ReleaseSource();
            if (_device != null)
            {
                if (_pipeline.IsValid) _device.FreeRid(_pipeline);
                if (_shader.IsValid) _device.FreeRid(_shader);
                _device.Dispose();
            }
        }
    }

    private void EnsureDevice()
    {
        if (_device != null || _failed)
            return;
        _device = RenderingServer.CreateLocalRenderingDevice();
        if (_device == null) { _failed = true; return; }
        using var source = new RDShaderSource { SourceCompute = ComputeSource };
        using var spirv = _device.ShaderCompileSpirVFromSource(source);
        string error = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
        _shader = _device.ShaderCreateFromSpirV(spirv);
        _pipeline = _device.ComputePipelineCreate(_shader);
        if (!_pipeline.IsValid) throw new InvalidOperationException("compute pipeline could not be created");
        Logger.Log("GPU texture variants: resizing and mip generation enabled");
    }

    private byte[][]? Execute(Job job)
    {
        EnsureDevice();
        if (_device == null) return null;
        var rd = _device;
        if (!_source.IsValid || _sourceBytes?.TryGetTarget(out var previous) != true || !ReferenceEquals(previous, job.Pixels))
        {
            ReleaseSource();
            _source = rd.StorageBufferCreate((uint)job.Pixels.Length, job.Pixels);
            if (!_source.IsValid) return null;
            _sourceBytes = new WeakReference<byte[]>(job.Pixels);
        }
        int count = job.Mipmaps ? TextureMetadata.FullMipCount(job.Width, job.Height) : 1;
        var output = new byte[count][];
        var buffers = new Rid[count];
        var uniforms = new List<Rid>();
        var dispatches = new List<(Rid Set, byte[] Dimensions, uint X, uint Y)>();
        Rid boundsBuffer = default;
        bool unchanged = job.Width == job.SourceWidth && job.Height == job.SourceHeight;
        try
        {
            // Preserve the existing filter even at floating-point boundary cases (e.g. 512 -> 98).
            // Two tiny coordinate tables avoid requiring double-precision GPU support or changing
            // cached variant bytes. The pixel averaging itself remains entirely on the GPU.
            var bounds = new List<int>();
            var offsets = new int[count];
            int sourceW = job.SourceWidth, sourceH = job.SourceHeight, targetW = job.Width, targetH = job.Height;
            for (int level = 0; level < count; level++)
            {
                offsets[level] = bounds.Count;
                AddBounds(bounds, sourceW, targetW);
                AddBounds(bounds, sourceH, targetH);
                sourceW = targetW; sourceH = targetH;
                targetW = Math.Max(1, targetW >> 1); targetH = Math.Max(1, targetH >> 1);
            }
            byte[] coordinates = new byte[bounds.Count * sizeof(int)];
            Buffer.BlockCopy(bounds.ToArray(), 0, coordinates, 0, coordinates.Length);
            boundsBuffer = rd.StorageBufferCreate((uint)coordinates.Length, coordinates);
            if (!boundsBuffer.IsValid) throw new InvalidOperationException("variant coordinates allocation failed");
            int sw = job.SourceWidth, sh = job.SourceHeight, w = job.Width, h = job.Height;
            Rid input = _source;
            for (int level = 0; level < count; level++)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                if (level == 0 && unchanged)
                    output[0] = job.Pixels;
                else
                {
                    var buffer = rd.StorageBufferCreate((uint)(w * h * 4));
                    buffers[level] = buffer;
                    if (!buffer.IsValid) throw new InvalidOperationException("variant buffer allocation failed");
                    using var read = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
                    using var write = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
                    using var ranges = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 };
                    read.AddId(input);
                    write.AddId(buffer);
                    ranges.AddId(boundsBuffer);
                    var set = rd.UniformSetCreate(new global::Godot.Collections.Array<RDUniform> { read, write, ranges }, _shader, 0);
                    uniforms.Add(set);
                    if (!set.IsValid) throw new InvalidOperationException("variant bindings could not be created");
                    byte[] dimensions = new byte[20];
                    Buffer.BlockCopy(new[] { sw, sh, w, h, offsets[level] }, 0, dimensions, 0, 20);
                    dispatches.Add((set, dimensions, (uint)((w + 7) / 8), (uint)((h + 7) / 8)));
                    input = buffer;
                }
                sw = w; sh = h;
                w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1);
            }
            // Submit the dependency chain together. A barrier makes each mip visible to the next;
            // one worker-thread wait avoids a CPU/GPU round trip for every tiny level.
            if (dispatches.Count > 0)
            {
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, _pipeline);
                foreach (var dispatch in dispatches)
                {
                    rd.ComputeListBindUniformSet(list, dispatch.Set, 0);
                    rd.ComputeListSetPushConstant(list, dispatch.Dimensions, 20);
                    rd.ComputeListDispatch(list, dispatch.X, dispatch.Y, 1);
                    rd.ComputeListAddBarrier(list);
                }
                rd.ComputeListEnd();
                rd.Submit();
                rd.Sync();
                job.Cancellation.ThrowIfCancellationRequested();
                for (int level = 0; level < count; level++)
                    if (buffers[level].IsValid) output[level] = rd.BufferGetData(buffers[level]);
            }
            return output;
        }
        finally
        {
            foreach (var set in uniforms) if (set.IsValid) rd.FreeRid(set);
            foreach (var buffer in buffers) if (buffer.IsValid) rd.FreeRid(buffer);
            if (boundsBuffer.IsValid) rd.FreeRid(boundsBuffer);
        }
    }

    private static void AddBounds(List<int> bounds, int sourceSize, int targetSize)
    {
        double ratio = (double)sourceSize / targetSize;
        for (int p = 0; p < targetSize; p++)
        {
            int start = (int)(p * ratio);
            bounds.Add(start);
            bounds.Add(Math.Max(start + 1, Math.Min(sourceSize, (int)Math.Ceiling((p + 1) * ratio))));
        }
    }

    private void ReleaseSource()
    {
        if (_source.IsValid) _device?.FreeRid(_source);
        _source = default;
        _sourceBytes = null;
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _jobs.CompleteAdding();
        _thread.Join();
    }

    // Integer RGBA arithmetic preserves the CPU filter's straight alpha, channel values and row order.
    // Every invocation owns one destination texel; guard rounded-up dispatches for odd dimensions.
    private const string ComputeSource = """
        #version 450
        layout(local_size_x=8, local_size_y=8, local_size_z=1) in;
        layout(set=0, binding=0, std430) readonly restrict buffer Source { uint pixels[]; } src;
        layout(set=0, binding=1, std430) writeonly restrict buffer Destination { uint pixels[]; } dst;
        layout(set=0, binding=2, std430) readonly restrict buffer Bounds { uint values[]; } bounds;
        layout(push_constant, std430) uniform Dimensions { uint sw; uint sh; uint dw; uint dh; uint offset; } d;
        void main() {
            uvec2 p = gl_GlobalInvocationID.xy;
            if (p.x >= d.dw || p.y >= d.dh) return;
            uint xi = d.offset + p.x*2;
            uint yi = d.offset + d.dw*2 + p.y*2;
            uint x0 = bounds.values[xi], x1 = bounds.values[xi+1];
            uint y0 = bounds.values[yi], y1 = bounds.values[yi+1];
            uvec4 total = uvec4(0);
            for (uint y=y0; y<y1; ++y) for (uint x=x0; x<x1; ++x) {
                uint v=src.pixels[y*d.sw+x];
                total += uvec4(v&255u, (v>>8)&255u, (v>>16)&255u, v>>24);
            }
            uvec4 c=total/((x1-x0)*(y1-y0));
            dst.pixels[p.y*d.dw+p.x]=c.r|(c.g<<8)|(c.b<<16)|(c.a<<24);
        }
        """;
}
