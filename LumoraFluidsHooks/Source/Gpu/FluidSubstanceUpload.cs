// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Lumora.Fluids.Hooks;

// Uploads the substance records when a field changed (FluidFrameBlock.SubstancesChanged), filling in what the tier
// owns (bond slots), and keeps the numbers the frame plan needs from them: the widest smoothing radius (the sort's
// cell is h + skin of the widest), the most viscous passes anyone wants, and the slowest substance's MaxSpeed. -xlinka
public sealed class FluidSubstanceUpload
{
    private readonly List<FluidSubstanceRecord> _records = new();

    public FluidSubstanceUpload(RenderingDevice device)
    {
        Device = device;
    }

    public RenderingDevice Device { get; }

    public float MaxSmoothingRadius { get; private set; } = 0.02f;

    public float MinSmoothingRadius { get; private set; } = 0.02f;

    // velocity passes after Update (unbonded kinds), and position passes over the bonds before it (bonded kinds)
    public int MaxViscousPasses { get; private set; }

    public int MaxBondPasses { get; private set; }

    // a viscous network is uploaded: the solver runs the tier's full substeps
    public bool Stiff { get; private set; }

    public int Count { get; private set; }

    public bool Uploaded { get; private set; }

    public void Upload(FluidGpuBuffers buffers, List<FluidSubstanceRecord> substances, int bondSlots)
    {
        _records.Clear();
        int n = Math.Min(substances.Count, FluidLayout.MaxSubstances);
        float maxH = 0f, minH = float.MaxValue;
        int passes = 0, bondPasses = 0;
        bool stiff = false;
        for (int i = 0; i < n; i++)
        {
            var r = substances[i];
            r.BondSlots = (uint)bondSlots;
            _records.Add(r);
            if (r.SmoothingRadius > 0f)
            {
                maxH = Math.Max(maxH, r.SmoothingRadius);
                minH = Math.Min(minH, r.SmoothingRadius);
            }
            if (r.BondCompliance > 0f)
                bondPasses = Math.Max(bondPasses, (int)r.ViscousPasses);
            else
                passes = Math.Max(passes, (int)r.ViscousPasses);
            stiff |= (r.Flags & FluidSubstanceParams.FlagStiff) != 0;
        }
        if (n == 0)
            _records.Add(default);
        MaxSmoothingRadius = maxH > 0f ? maxH : 0.02f;
        MinSmoothingRadius = minH < float.MaxValue ? minH : MaxSmoothingRadius;
        MaxViscousPasses = passes;
        MaxBondPasses = bondPasses;
        Stiff = stiff;
        Count = n;
        buffers.Upload(FluidGpuBuffers.Substances, _records);
        Uploaded = true;
    }
}
