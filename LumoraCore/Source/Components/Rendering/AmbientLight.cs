// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// The world's ambient light, given as nine spherical harmonic coefficients of linear RGB, usually copied from
// a ProbeHarmonics. While it is the world's one ambient light it takes over from whatever ambient the sky
// sets, as a flat light of the coefficients' average.
//
// One per world counts, picked from synced state the same way the sky is: Priority, then the lower RefID,
// so every peer agrees on which. -xlinka
[ComponentCategory("Rendering")]
public class AmbientLight : ImplementableComponent, ICustomInspectorUI
{
    public readonly Sync<SphericalHarmonicsL2> Harmonics;

    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> Intensity;

    // higher wins; equal priorities fall back to the lower RefID
    public readonly Sync<int> Priority;

    private static readonly Dictionary<World, List<AmbientLight>> _registry = new();
    private static readonly object _registryLock = new();

    private bool _isActive;
    private bool _registered;

    public bool IsActiveAmbient => _isActive;

    public AmbientLight()
    {
        Harmonics = new Sync<SphericalHarmonicsL2>(this, default);
        Intensity = new Sync<float>(this, 1f);
        Priority = new Sync<int>(this, 0);
    }

    // Linear RGB, never negative.
    public float3 AverageLight
    {
        get
        {
            var average = CubemapHarmonics.Average(Harmonics.Value) * Intensity.Value;
            return new float3(System.Math.Max(average.x, 0f), System.Math.Max(average.y, 0f), System.Math.Max(average.z, 0f));
        }
    }

    public override void OnStart()
    {
        base.OnStart();
        Register();
    }

    public override void OnDestroy()
    {
        Unregister();
        base.OnDestroy();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        Resolve(World);
    }

    [SyncMethod]
    public void MakeActive()
    {
        int highest = int.MinValue;
        foreach (var other in Snapshot(World))
        {
            if (other != this && other.Priority.Value > highest)
                highest = other.Priority.Value;
        }
        if (highest != int.MinValue && Priority.Value <= highest)
            Priority.Value = highest + 1;
        Resolve(World);
    }

    private void Register()
    {
        var world = World;
        if (world == null || _registered)
            return;
        lock (_registryLock)
        {
            if (!_registry.TryGetValue(world, out var list))
                _registry[world] = list = new List<AmbientLight>();
            list.Add(this);
        }
        _registered = true;
        Resolve(world);
    }

    private void Unregister()
    {
        var world = World;
        if (!_registered)
            return;
        _registered = false;
        lock (_registryLock)
        {
            if (world != null && _registry.TryGetValue(world, out var list))
            {
                list.Remove(this);
                if (list.Count == 0)
                    _registry.Remove(world);
            }
        }
        if (_isActive)
        {
            _isActive = false;
            Resolve(world);
        }
    }

    private static AmbientLight[] Snapshot(World? world)
    {
        if (world == null)
            return System.Array.Empty<AmbientLight>();
        lock (_registryLock)
            return _registry.TryGetValue(world, out var list) ? list.ToArray() : System.Array.Empty<AmbientLight>();
    }

    // Only the ones whose standing flipped are told, or this would spin through the change queue.
    private static void Resolve(World? world)
    {
        var all = Snapshot(world);
        AmbientLight? winner = null;
        foreach (var candidate in all)
        {
            if (candidate.IsDestroyed || !candidate.Enabled || candidate.Slot == null || !candidate.Slot.IsActive)
                continue;
            if (winner == null || candidate.Priority.Value > winner.Priority.Value
                || (candidate.Priority.Value == winner.Priority.Value && candidate.ReferenceID < winner.ReferenceID))
                winner = candidate;
        }
        foreach (var candidate in all)
        {
            bool active = candidate == winner;
            if (candidate._isActive == active)
                continue;
            candidate._isActive = active;
            candidate.MarkChangeDirty();
        }
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        var light = AverageLight;
        InspectorStats.AddRow(ui, "Active", _isActive ? "yes" : "no, another ambient light outranks it");
        InspectorStats.AddRow(ui, "Average light", $"{light.x:0.000} {light.y:0.000} {light.z:0.000} (linear)");
    }
}
