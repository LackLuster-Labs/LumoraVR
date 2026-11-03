// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;

namespace Lumora.Fluids.Hooks;

// One uniform set per kernel, made once and rebuilt only when a buffer it binds was made again (FluidGpuBuffers
// .Version). Every set offers the whole binding table: the device takes the bindings the kernel declares and ignores
// the rest, so no kernel's binding list is kept twice. Building the RDUniform array allocates Godot objects, which
// is fine at a rebuild and never happens per frame. The sorted positions always end up in A, so one set per kernel
// is all there is: no parity to flip. -xlinka
public sealed class FluidUniformSets
{
    private readonly Rid[] _sets = new Rid[FluidGpuPipelines.Count];
    private int _version = -1;

    public FluidUniformSets(RenderingDevice device)
    {
        Device = device;
    }

    public RenderingDevice Device { get; }

    public int Rebuilds { get; private set; }

    public Rid this[int kernel] => _sets[kernel];

    public void Ensure(FluidGpuBuffers buffers, FluidGpuPipelines pipelines)
    {
        if (_version == buffers.Version && _sets[0].IsValid)
            return;
        Free();
        var uniforms = new global::Godot.Collections.Array<RDUniform>();
        for (int b = 0; b < FluidGpuBuffers.Count; b++)
        {
            var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b };
            u.AddId(buffers[b]);
            uniforms.Add(u);
        }
        for (int k = 0; k < FluidGpuPipelines.Count; k++)
            _sets[k] = Device.UniformSetCreate(uniforms, pipelines.Shader(k), 0);
        _version = buffers.Version;
        Rebuilds++;
    }

    public void Free()
    {
        for (int k = 0; k < _sets.Length; k++)
        {
            if (_sets[k].IsValid && Device.UniformSetIsValid(_sets[k]))
                Device.FreeRid(_sets[k]);
            _sets[k] = default;
        }
    }
}
